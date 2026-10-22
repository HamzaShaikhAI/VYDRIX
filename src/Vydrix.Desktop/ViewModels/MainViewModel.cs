using System.Collections.ObjectModel;
using Vydrix.App;
using Vydrix.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Vydrix.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(AppHost host)
    {
        Host = host;
        Pages = new ObservableCollection<PageViewModel>
        {
            new DashboardViewModel(host, Navigate),
            new CloudDrivesViewModel(host),
            new GameLibraryViewModel(host),
            new AcceleratorViewModel(host),
            new SmartCacheViewModel(host),
            new PerformanceViewModel(host),
            new SettingsViewModel(host),
            new DiagnosticsViewModel(host),
        };
        _selected = Pages[0];
        host.StateChanged += () => Ui(() =>
        {
            Selected.OnStateChanged();
            UpdateStatus(host.Metrics.Latest);
        });
        host.Metrics.Updated += m => Ui(() =>
        {
            Selected.OnMetrics(m);
            UpdateStatus(m);
        });
    }

    public AppHost Host { get; }
    public ObservableCollection<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel _selected;
    [ObservableProperty] private string _drivesStatus = "No drives mounted";
    [ObservableProperty] private string _rateStatus = "";
    [ObservableProperty] private string _engineStatus = "";
    [ObservableProperty] private string _gameStatus = "";
    [ObservableProperty] private string _version = "v" + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0");

    partial void OnSelectedChanged(PageViewModel value) => value?.OnShown();

    public void Navigate(string title)
    {
        var p = Pages.FirstOrDefault(x => x.Title == title);
        if (p != null) Selected = p;
    }

    private void UpdateStatus(EngineMetrics m)
    {
        var sessions = Host.Mounts.Sessions;
        DrivesStatus = sessions.Count == 0 ? "No drives mounted"
            : string.Join("  ", sessions.Select(s => $"{s.DriveLetter}: {s.State}"));
        RateStatus = m.DownloadBytesPerSec is { } r ? $"↓ {Bandwidth.BytesPerSecondToMbps(r):0.0} Mbps" : "";
        EngineStatus = Host.DiskPressure ? "Prefetch paused (disk space)" : Host.Engine.Scheduler.Active > 0 ? $"Prefetching ({Host.Engine.Queue.Count} queued)" : "";
        GameStatus = Host.CurrentLaunch is { State: Games.LaunchState.Running } l ? $"▶ {l.Game.Name}" : "";
    }

    private static void Ui(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) a();
        else d.InvokeAsync(a);
    }
}
