using System.Windows;
using Vydrix.App;
using Vydrix.Core;
using Vydrix.Diagnostics;
using Vydrix.Games;

namespace Vydrix.Desktop.Views;

public partial class GameSettingsWindow : Window
{
    private readonly GameEntry _game;

    public GameSettingsWindow(AppHost host, GameEntry game)
    {
        _game = game;
        InitializeComponent();
        TitleText.Text = game.Name;
        NameBox.Text = game.Name;
        ArgsBox.Text = game.Arguments;
        WorkBox.Text = game.WorkingDirectory ?? "";
        var candidates = new List<string>();
        if (host.Mounts.Get(game.MountId) is { } m)
        {
            var root = Path.Combine(m.Root, game.RootPath.Replace('/', '\\'));
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(root, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true })
                    .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Take(200));
                ExeHint.Text = $"{candidates.Count} executable(s) found. The scanner's choice was: {game.Compatibility?.Executable ?? "none"}.";
            }
            catch (IOException) { }
        }
        else ExeHint.Text = "Mount the drive to list executables.";
        if (game.Executable != null && !candidates.Contains(game.Executable)) candidates.Insert(0, game.Executable);
        ExeBox.ItemsSource = candidates;
        ExeBox.SelectedItem = game.Executable;
        ModeCloud.IsChecked = game.Mode == ExecutionMode.CloudDirect;
        ModeHybrid.IsChecked = game.Mode == ExecutionMode.HybridOptimized;
        ModeLocal.IsChecked = game.Mode == ExecutionMode.FullLocal;
        var canLink = DependencyChecker.CanCreateSymlinks();
        ModeHybrid.IsEnabled = canLink;
        ModeHint.Text = (canLink ? "" : "Hybrid needs Windows Developer Mode (Settings → System → For developers) or administrator rights for symbolic links. ")
                        + $"Recommended by analysis: {game.Compatibility?.RecommendedMode.ToString() ?? "unknown"}.";
        AwareBox.IsChecked = game.GameAwareEnabled;
        AwareHint.Text = $"Requires a mod/plugin that writes JSON lines to \\\\.\\pipe\\vydrix-telemetry-{new string(game.Id.Where(char.IsLetterOrDigit).ToArray())} (current Windows user only). " +
                         "No game ships such a plugin today; without one this option has no effect. See docs/GAME_AWARE_PROTOCOL.md.";
    }

    private void Ok(object sender, RoutedEventArgs e)
    {
        _game.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? _game.Name : NameBox.Text.Trim();
        _game.Executable = ExeBox.SelectedItem as string ?? _game.Executable;
        _game.Arguments = ArgsBox.Text;
        _game.WorkingDirectory = string.IsNullOrWhiteSpace(WorkBox.Text) ? null : WorkBox.Text.Trim().Replace('\\', '/');
        _game.Mode = ModeHybrid.IsChecked == true ? ExecutionMode.HybridOptimized : ModeLocal.IsChecked == true ? ExecutionMode.FullLocal : ExecutionMode.CloudDirect;
        _game.GameAwareEnabled = AwareBox.IsChecked == true;
        DialogResult = true;
    }
}
