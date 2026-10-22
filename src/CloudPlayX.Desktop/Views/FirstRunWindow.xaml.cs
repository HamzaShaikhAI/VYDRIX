using System.Globalization;
using System.Windows;
using CloudPlayX.App;
using CloudPlayX.Core;
using CloudPlayX.Desktop.ViewModels;
using CloudPlayX.Diagnostics;
using Microsoft.Win32;

namespace CloudPlayX.Desktop.Views;

public partial class FirstRunWindow : Window
{
    private readonly AppHost _host;

    public FirstRunWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Deps.ItemsSource = host.Dependencies.Select(d => new KeyValueRow(d.Name, d.Detail + (d.Remedy != null ? " → " + d.Remedy : ""),
            d.State switch { DependencyState.Ok => "ok", DependencyState.Warning => "warn", _ => "error" })).ToList();
        CacheBox.Text = SuggestCacheRoot(host);
        BudgetBox.ItemsSource = new[] { "10 GB", "20 GB", "30 GB", "40 GB", "60 GB", "100 GB" };
        BudgetBox.SelectedItem = $"{host.Settings.Cache.BudgetBytes / ByteSize.GiB} GB";
        if (BudgetBox.SelectedItem is null) BudgetBox.SelectedIndex = 1;
        MbpsBox.Text = host.Settings.Network.DeclaredDownloadMbps.ToString(CultureInfo.InvariantCulture);
        MbpsBox.TextChanged += (_, _) => UpdateEstimate();
        UpdateEstimate();
    }

    /// <summary>Suggests the fixed NTFS drive with the most free space (the system drive is often nearly full).</summary>
    public static string SuggestCacheRoot(AppHost host)
    {
        var current = host.Paths.CacheRootFor(host.Settings);
        try
        {
            var best = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
            var (curFree, _) = DiskInfo.FreeSpace(current);
            if (best != null && best.AvailableFreeSpace > curFree * 1.5 && !current.StartsWith(best.Name, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(best.Name, "CloudPlayX", "cache");
        }
        catch (Exception) { }
        return current;
    }

    private void UpdateEstimate()
    {
        if (!double.TryParse(MbpsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps) || mbps <= 0) return;
        var bps = Bandwidth.MbpsToBytesPerSecond(mbps);
        Estimate.Text = $"{mbps:0} Mbps = {Bandwidth.BytesPerSecondToMBps(bps):0.00} MB/s at best. Downloading 10 GB takes about {Bandwidth.FormatDuration(Bandwidth.EstimateTransfer(10 * ByteSize.GiB, mbps))}; " +
                        "CLOUDPLAY X measures the real speed during Optimize & Prepare.";
    }

    private void CacheChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        try
        {
            var (free, total) = DiskInfo.FreeSpace(CacheBox.Text);
            var (fs, sparse) = DiskInfo.VolumeFileSystem(Directory.Exists(CacheBox.Text) ? CacheBox.Text : DiskInfo.VolumeRoot(CacheBox.Text));
            CacheInfo.Text = $"{ByteSize.Format(free)} free of {ByteSize.Format(total)} · {fs}{(sparse ? " with sparse files" : " (no sparse-file support)")}";
        }
        catch (Exception ex) { CacheInfo.Text = ex.Message; }
    }

    private void Browse(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Choose a folder on an NTFS drive for the cache" };
        if (d.ShowDialog() == true) CacheBox.Text = Path.Combine(d.FolderName, "CloudPlayX-cache");
    }

    private void Ok(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ReserveBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var reserve) || reserve < 2
            || !double.TryParse(MbpsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps) || mbps <= 0)
        {
            Status.Text = "Reserve must be at least 2 GB and internet speed a positive number.";
            return;
        }
        try { Directory.CreateDirectory(CacheBox.Text); }
        catch (Exception ex)
        {
            Status.Text = "Cannot use that folder: " + ex.Message;
            return;
        }
        var s = _host.Settings;
        s.Cache.CacheRoot = CacheBox.Text.Trim();
        s.Cache.BudgetBytes = long.Parse(((string)BudgetBox.SelectedItem).Split(' ')[0]) * ByteSize.GiB;
        s.Cache.MinFreeSpaceBytes = (long)(reserve * ByteSize.GiB);
        s.Network.DeclaredDownloadMbps = mbps;
        s.LocalGamesRoot ??= Path.Combine(Path.GetDirectoryName(s.Cache.CacheRoot.TrimEnd('\\'))!, "local-games");
        s.FirstRunCompleted = true;
        _host.SaveSettings();
        _host.RefreshDependencies();
        DialogResult = true;
    }

    private void Later(object sender, RoutedEventArgs e) => DialogResult = false;
}
