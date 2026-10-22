using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Vydrix.App;
using Vydrix.Core;
using Vydrix.Desktop.ViewModels;
using Vydrix.Desktop.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Vydrix.Desktop;

public partial class App : Application
{
    private Mutex? _single;
    private AppHost? _host;
    private TrayIconManager? _tray;
    private bool _shuttingDown;
    private bool _forceExit;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // DEF-01: Handle CLI password helper in-process before acquiring GUI mutex or starting WPF UI
        if (TryHandleCliCommand(e.Args))
        {
            Shutdown(Environment.ExitCode);
            return;
        }

        var startMinimized = e.Args.Any(a => a is "--minimized" or "--tray" or "--background");

        _single = new Mutex(true, @"Local\Vydrix.Desktop.SingleInstance", out var created);
        if (!created)
        {
            if (!startMinimized)
            {
                MessageBox.Show("VYDRIX is already running.", "VYDRIX", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        _host = AppHost.Create();
        if (!_host.TryAcquireInstanceLock())
        {
            MessageBox.Show("Another VYDRIX process (for example the command-line tool) is using the data folder.", "VYDRIX");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            _host.LoggerFactory.CreateLogger("App").LogError(args.Exception, "Unhandled UI exception");
            MessageBox.Show(Diagnostics.Redactor.Redact(args.Exception.Message), "VYDRIX error", MessageBoxButton.OK, MessageBoxImage.Error);
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

        // Initialize native system tray icon
        _tray = new TrayIconManager(window, _host);

        if (!startMinimized)
        {
            window.Show();
        }
        else
        {
            // Ensure window handle is created so tray icon can receive messages
            new WindowInteropHelper(window).EnsureHandle();
        }

        try
        {
            await _host.InitializeAsync();
        }
        catch (Exception ex)
        {
            if (!startMinimized)
            {
                MessageBox.Show(window, "Startup problem: " + Diagnostics.Redactor.Redact(ex.Message), "VYDRIX", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                _host.LoggerFactory.CreateLogger("App").LogError(ex, "Startup initialization error in background mode");
            }
        }

        if (!_host.Settings.FirstRunCompleted && !startMinimized)
        {
            var first = new FirstRunWindow(_host) { Owner = window };
            first.ShowDialog();
        }
        vm.Selected.OnShown();
    }

    public void RequestExit()
    {
        _forceExit = true;
        MainWindow?.Close();
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shuttingDown || _host is null) return;

        // If user didn't explicitly request exit from tray menu, and LaunchMinimizedToTray is enabled, just hide to tray
        if (!_forceExit && _host.Settings.LaunchMinimizedToTray)
        {
            e.Cancel = true;
            MainWindow?.Hide();
            return;
        }

        e.Cancel = true;
        var keep = _host.Settings.KeepMountsAfterExit;
        if (_host.Mounts.Sessions.Count > 0)
        {
            var running = _host.CurrentLaunch is { State: Games.LaunchState.Running };
            var answer = MessageBox.Show(MainWindow!,
                (running ? "A game is still running.\n\n" : "") +
                "Keep the cloud drives mounted after closing VYDRIX?\n\nYes: drives stay available and are re-attached next time.\nNo: drives are unmounted (pending uploads get up to 60 s to finish).",
                "Close VYDRIX", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, keep || running ? MessageBoxResult.Yes : MessageBoxResult.No);
            if (answer == MessageBoxResult.Cancel) return;
            keep = answer == MessageBoxResult.Yes;
        }
        _shuttingDown = true;
        MainWindow!.IsEnabled = false;
        MainWindow.Title = "VYDRIX — closing…";
        _tray?.Dispose();
        try
        {
            await _host.ShutdownAsync(keep);
            await _host.DisposeAsync();
        }
        catch (Exception) { }
        _single?.ReleaseMutex();
        Shutdown();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private static bool TryHandleCliCommand(string[] args)
    {
        if (args.Length > 1 && args[0] == "secret" && args[1] == "emit-config-pass")
        {
            AttachConsole(-1);
            var home = Opt(args, "--home");
            var paths = new AppPaths(home);
            var pass = Diagnostics.SecretStore.Load(paths.ConfigPasswordFile);
            if (pass is not null)
            {
                using var stdout = Console.OpenStandardOutput();
                using var writer = new StreamWriter(stdout, System.Text.Encoding.UTF8) { AutoFlush = true };
                writer.Write(pass);
                Environment.ExitCode = 0;
            }
            else
            {
                using var stderr = Console.OpenStandardError();
                using var writer = new StreamWriter(stderr, System.Text.Encoding.UTF8) { AutoFlush = true };
                writer.WriteLine("No configuration key found for this Windows user.");
                Environment.ExitCode = 1;
            }
            return true;
        }
        return false;
    }

    private static string? Opt(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
