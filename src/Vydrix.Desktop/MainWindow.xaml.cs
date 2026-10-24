using System.Windows;

namespace Vydrix.Desktop;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var appHost = (DataContext as ViewModels.MainViewModel)?.Host;
        var about = new Views.AboutWindow(appHost) { Owner = this };
        about.ShowDialog();
    }
}
