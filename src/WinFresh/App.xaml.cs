using System.Windows;

namespace WinFresh;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"WinFresh Studio encountered an unexpected error:\n\n{args.Exception.Message}",
                "WinFresh Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
        };
    }
}
