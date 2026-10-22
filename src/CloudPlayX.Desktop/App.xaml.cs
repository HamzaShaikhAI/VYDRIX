using System.Threading;
using System.Windows;
using CloudPlayX.App;
using CloudPlayX.Desktop.ViewModels;
using CloudPlayX.Desktop.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CloudPlayX.Desktop;

public partial class App : Application
{
    private Mutex? _single;
    private AppHost? _host;
    private bool _shuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\CloudPlayX.Desktop.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("CLOUDPLAY X is already running.", "CLOUDPLAY X", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _host = AppHost.Create();
        if (!_host.TryAcquireInstanceLock())
        {
            MessageBox.Show("Another CLOUDPLAY X process (for example the command-line tool) is using the data folder.", "CLOUDPLAY X");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            _host.LoggerFactory.CreateLogger("App").LogError(args.Exception, "Unhandled UI exception");
            MessageBox.Show(Diagnostics.Redactor.Redact(args.Exception.Message), "CLOUDPLAY X error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        SystemEvents.PowerModeChanged += (_, args) =>
        {
            if (args.Mode == PowerModes.Resume && _host != null) _ = _host.Mounts.OnSystemResumeAsync();
        };

        var vm = new MainViewModel(_host);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Closing += OnClosing;
        window.Show();

        try
        {
            await _host.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(window, "Startup problem: " + Diagnostics.Redactor.Redact(ex.Message), "CLOUDPLAY X", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (!_host.Settings.FirstRunCompleted)
        {
            var first = new FirstRunWindow(_host) { Owner = window };
            first.ShowDialog();
        }
        vm.Selected.OnShown();
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shuttingDown || _host is null) return;
        e.Cancel = true;
        var keep = _host.Settings.KeepMountsAfterExit;
        if (_host.Mounts.Sessions.Count > 0)
        {
            var running = _host.CurrentLaunch is { State: Games.LaunchState.Running };
            var answer = MessageBox.Show(MainWindow!,
                (running ? "A game is still running.\n\n" : "") +
                "Keep the cloud drives mounted after closing CLOUDPLAY X?\n\nYes: drives stay available and are re-attached next time.\nNo: drives are unmounted (pending uploads get up to 60 s to finish).",
                "Close CLOUDPLAY X", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, keep || running ? MessageBoxResult.Yes : MessageBoxResult.No);
            if (answer == MessageBoxResult.Cancel) return;
            keep = answer == MessageBoxResult.Yes;
        }
        _shuttingDown = true;
        MainWindow!.IsEnabled = false;
        MainWindow.Title = "CLOUDPLAY X — closing…";
        try
        {
            await _host.ShutdownAsync(keep);
            await _host.DisposeAsync();
        }
        catch (Exception) { }
        _single?.ReleaseMutex();
        Shutdown();
    }
}
