using System.Collections.ObjectModel;
using System.Diagnostics;
using Vydrix.App;
using Vydrix.Cache;
using Vydrix.Core;
using Vydrix.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vydrix.Desktop.ViewModels;

public sealed partial class DriveItem : ObservableObject
{
    public required MountDefinition Definition { get; init; }
    [ObservableProperty] private string _letter = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _tone = "muted";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _isMounted;
    public string Name => Definition.DisplayName;
    public string Remote => Definition.RemoteSpec;
}

public sealed record ProviderRow(string Name, string Support, string Tone, string Notes);

public sealed partial class CloudDrivesViewModel : PageViewModel
{
    public CloudDrivesViewModel(AppHost host) : base(host, "Cloud Drives", "") { }

    public ObservableCollection<KeyValueRow> Accounts { get; } = new();
    public ObservableCollection<DriveItem> Drives { get; } = new();
    public ObservableCollection<ProviderRow> ProviderRows { get; } = new(ProviderRegistry.All.Select(p => new ProviderRow(
        p.DisplayName,
        p.Support switch { SupportLevel.Supported => "Supported", SupportLevel.AvailableUntested => "Via rclone · untested", _ => "Not supported" },
        p.Support switch { SupportLevel.Supported => "ok", SupportLevel.AvailableUntested => "warn", _ => "error" },
        p.Capabilities.Notes ?? p.SetupSummary)));

    [ObservableProperty] private KeyValueRow? _selectedAccount;
    [ObservableProperty] private DriveItem? _selectedDrive;

    public override void OnShown() => OnStateChanged();

    public override void OnStateChanged()
    {
        Accounts.ReplaceWith(Host.Remotes.Select(r => new KeyValueRow(r.Name, ProviderRegistry.ForBackend(r.Type)?.DisplayName ?? r.Type)));
        var sessions = Host.Mounts.Sessions;
        var selected = SelectedDrive?.Definition.Id;
        Drives.ReplaceWith(Host.Settings.Mounts.Select(d =>
        {
            var s = sessions.FirstOrDefault(x => x.Id == d.Id);
            var state = s?.State ?? MountState.Stopped;
            return new DriveItem
            {
                Definition = d,
                Letter = s != null ? $"{s.DriveLetter}:" : (d.DriveLetter is { } l ? $"{l}:" : "auto"),
                State = state.ToString(),
                Tone = Fmt.Tone(state),
                IsMounted = state is MountState.Mounted or MountState.Degraded,
                Detail = (s?.Message ?? "") + (s?.Warnings.Count > 0 ? " · " + string.Join(" ", s.Warnings) : "") + (d.ReadOnly ? " · read-only" : "")
            };
        }));
        SelectedDrive = Drives.FirstOrDefault(d => d.Definition.Id == selected);
    }

    [RelayCommand]
    private async Task ConnectGoogleDrive()
    {
        var w = new Views.ConnectDriveWindow(Host) { Owner = System.Windows.Application.Current.MainWindow };
        if (w.ShowDialog() == true)
        {
            await Host.RefreshRemotesAsync();
            await ReplaceOldGoogleConnectionsAsync(w.RemoteName);
            OnStateChanged();
            // One click: mount the whole Google Drive straight away on the next free drive letter.
            // A read-only login must give a read-only drive, so writes (e.g. a Steam library) fail immediately and clearly.
            var def = new MountDefinition { DisplayName = "Google Drive", RemoteName = w.RemoteName, RemotePath = "", AutoMount = true, ReadOnly = w.ReadOnlyScope };
            Host.Settings.Mounts.Add(def);
            Host.SaveSettings();
            Say("Google Drive connected. Mounting…", "accent");
            await RunAsync(async () =>
            {
                var s = await Host.MountAsync(def);
                def.DriveLetter = s.DriveLetter.ToString();
                Host.SaveSettings();
                Say($"Google Drive is ready as drive {s.DriveLetter}: in File Explorer{(def.ReadOnly ? " (read-only)" : "")}. Next: Game Library → Add game.", "ok");
            });
            OnStateChanged();
        }
    }

    /// <summary>
    /// A new Google sign-in replaces earlier ones made by VYDRIX ("gdrive*" accounts and their drives), so the user
    /// never has to clean up by hand. Only local logins and drive entries are removed — files in Google Drive are untouched.
    /// Drives used by a running game are left alone.
    /// </summary>
    private async Task ReplaceOldGoogleConnectionsAsync(string keep)
    {
        if (Host.Rclone is null) return;
        var running = Host.CurrentLaunch is { State: Games.LaunchState.Running } l ? l.Game.MountId : null;
        var old = Host.Remotes.Where(r => r.Type == "drive" && r.Name != keep && r.Name.StartsWith("gdrive", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var remote in old)
        {
            var mounts = Host.Settings.Mounts.Where(m => m.RemoteName == remote.Name).ToList();
            if (mounts.Any(m => m.Id == running)) continue;
            foreach (var m in mounts)
            {
                try { if (Host.Mounts.Get(m.Id) != null) await Host.UnmountAsync(m.Id); } catch (Exception) { }
                Host.Settings.Mounts.Remove(m);
            }
            try { await Host.RcloneConfig.DeleteRemoteAsync(Host.Rclone, remote.Name); } catch (Exception) { }
        }
        Host.SaveSettings();
        await Host.RefreshRemotesAsync();
    }

    [RelayCommand]
    private async Task AddDrive(string? remote)
    {
        if (Host.Remotes.Count == 0)
        {
            Say("Connect a cloud account first.", "warn");
            return;
        }
        var w = new Views.AddDriveWindow(Host, remote) { Owner = System.Windows.Application.Current.MainWindow };
        if (w.ShowDialog() != true || w.Result is null) return;
        Host.Settings.Mounts.Add(w.Result);
        Host.SaveSettings();
        OnStateChanged();
        await RunAsync(async () =>
        {
            var s = await Host.MountAsync(w.Result);
            Host.SaveSettings();
            Say($"{w.Result.DisplayName} mounted at {s.DriveLetter}:", "ok");
        });
        OnStateChanged();
    }

    [RelayCommand]
    private async Task Mount(DriveItem? d)
    {
        if (d is null) return;
        await RunAsync(async () =>
        {
            var s = await Host.MountAsync(d.Definition);
            Host.SaveSettings();
            Say($"{d.Name} mounted at {s.DriveLetter}:", "ok");
        });
        OnStateChanged();
    }

    [RelayCommand]
    private async Task Unmount(DriveItem? d)
    {
        if (d is null) return;
        if (Host.CurrentLaunch is { State: Games.LaunchState.Running } l && l.Game.MountId == d.Definition.Id
            && !Confirm($"{l.Game.Name} is running from this drive. Unmounting will break it. Continue?")) return;
        await RunAsync(() => Host.UnmountAsync(d.Definition.Id), $"{d.Name} unmounted. Pending uploads were given up to 60 s to finish.");
        OnStateChanged();
    }

    [RelayCommand]
    private void Open(DriveItem? d)
    {
        if (d is null || Host.Mounts.Get(d.Definition.Id) is not { } s) return;
        Process.Start(new ProcessStartInfo("explorer.exe", s.Root) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task Refresh(DriveItem? d)
    {
        if (d is null) return;
        await RunAsync(() => Host.Mounts.ForgetAsync(d.Definition.Id), "Directory cache refreshed; changed cloud files will be re-validated on next access.");
    }

    [RelayCommand]
    private async Task RemoveDrive(DriveItem? d)
    {
        if (d is null) return;
        if (!Confirm($"Remove the drive '{d.Name}' from VYDRIX?\n\nYour cloud files are NOT touched. The drive will be unmounted.")) return;
        await RunAsync(async () =>
        {
            if (Host.Mounts.Get(d.Definition.Id) != null) await Host.UnmountAsync(d.Definition.Id);
            Host.Settings.Mounts.RemoveAll(m => m.Id == d.Definition.Id);
            Host.SaveSettings();
            var dir = Host.Paths.MountCacheDir(Host.Settings, d.Definition.Id);
            if (Directory.Exists(dir) && Confirm("Also delete this drive's local cache? (Only cached copies are deleted, never cloud files.)"))
            {
                var r = CacheMaintenance.ClearMountCache(dir, Host.Paths.CacheRootFor(Host.Settings));
                Say(r.Message, r.Cleared ? "ok" : "warn");
            }
            else Say("Drive removed.", "ok");
        });
        OnStateChanged();
    }

    [RelayCommand]
    private async Task RemoveAccount(KeyValueRow? a)
    {
        if (a is null || Host.Rclone is null) return;
        if (Host.Settings.Mounts.Any(m => m.RemoteName == a.Key))
        {
            Say("Remove the drives that use this account first.", "warn");
            return;
        }
        if (!Confirm($"Disconnect account '{a.Key}'? The saved login is deleted from this PC; your cloud files are not touched.")) return;
        await RunAsync(async () =>
        {
            await Host.RcloneConfig.DeleteRemoteAsync(Host.Rclone, a.Key);
            await Host.RefreshRemotesAsync();
        }, "Account disconnected.");
        OnStateChanged();
    }
}
