using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Vydrix.App;
using Vydrix.Core;

namespace Vydrix.Desktop.Views;

public partial class AboutWindow : Window
{
    private readonly AppHost? _host;

    public AboutWindow(AppHost? host = null)
    {
        _host = host;
        InitializeComponent();

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        if (ver != null)
        {
            VersionText.Text = $"Version {ver.Major}.{ver.Minor}.{ver.Build}";
        }
    }

    private void OpenLicences_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var notices = Path.Combine(baseDir, "THIRD_PARTY_NOTICES.md");
            if (File.Exists(notices))
            {
                Process.Start(new ProcessStartInfo("notepad.exe", notices) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show(
                    "VYDRIX is built upon open-source components including rclone (MIT License, Copyright Nick Craig-Wood) and WinFsp (GPLv3 with FLOSS exception, Navimatics LLC).",
                    "Open Source Licences",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
