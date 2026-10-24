using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Kemptly;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Needed to decode console output of tools like ipconfig in the OEM code page.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}", "Kemptly",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
