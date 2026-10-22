using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Vydrix.App;

namespace Vydrix.Desktop;

/// <summary>
/// Native Win32 System Tray (notification area) manager for VYDRIX.
/// Pure WPF with zero Windows Forms dependencies or namespace conflicts.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private const int WM_USER = 0x0400;
    public const int WM_TRAYICON = WM_USER + 101;
    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    private readonly Window _window;
    private readonly AppHost _host;
    private IntPtr _hwnd;
    private bool _added;
    private ContextMenu? _contextMenu;

    public TrayIconManager(Window window, AppHost host)
    {
        _window = window;
        _host = host;

        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();

        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        CreateContextMenu();
        AddTrayIcon();
    }

    private void AddTrayIcon()
    {
        var hIcon = GetWindowIconHandle();
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = "VYDRIX Cloud Virtual Storage"
        };
        _added = Shell_NotifyIcon(NIM_ADD, ref nid);
    }

    private IntPtr GetWindowIconHandle()
    {
        // Try getting the window's own icon
        var helper = new WindowInteropHelper(_window);
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            if (_window.Icon != null)
            {
                var iconUri = new Uri("pack://application:,,,/Assets/cloudplayx.ico");
                var streamInfo = Application.GetResourceStream(iconUri);
                if (streamInfo != null)
                {
                    using var stream = streamInfo.Stream;
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    var icon = new System.Drawing.Icon(new MemoryStream(ms.ToArray()));
                    hIcon = icon.Handle;
                }
            }
        }
        catch
        {
            // Fallback to generic application icon
            hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
        }
        return hIcon;
    }

    private void CreateContextMenu()
    {
        _contextMenu = new ContextMenu();

        var openItem = new MenuItem { Header = "Open VYDRIX", FontWeight = FontWeights.Bold };
        openItem.Click += (_, _) => ShowWindow();
        _contextMenu.Items.Add(openItem);

        var reconnectItem = new MenuItem { Header = "Reconnect Cloud Drives" };
        reconnectItem.Click += (_, _) => _ = _host.Mounts.OnSystemResumeAsync();
        _contextMenu.Items.Add(reconnectItem);

        _contextMenu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "Exit VYDRIX" };
        exitItem.Click += (_, _) =>
        {
            if (Application.Current is App app)
            {
                app.RequestExit();
            }
            else
            {
                _window.Close();
            }
        };
        _contextMenu.Items.Add(exitItem);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            var eventCode = lParam.ToInt32();
            if (eventCode is WM_LBUTTONDBLCLK or WM_LBUTTONUP)
            {
                ShowWindow();
                handled = true;
            }
            else if (eventCode == WM_RBUTTONUP)
            {
                SetForegroundWindow(_hwnd);
                if (_contextMenu != null)
                {
                    _contextMenu.IsOpen = true;
                }
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Dispose()
    {
        if (_added && _hwnd != IntPtr.Zero)
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1
            };
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _added = false;
        }
    }
}
