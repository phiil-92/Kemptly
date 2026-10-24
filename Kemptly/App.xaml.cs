using System.Text;
using System.Windows;
using System.Windows.Threading;
using Kemptly.Core;
using Kemptly.Views;

namespace Kemptly;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Needed to decode console output of tools like ipconfig in the OEM code page.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Weekly scheduled clean (Junk Cleaner > "Clean automatically every week"): no window, clean, exit.
        if (e.Args.Contains("--clean-silent"))
        {
            StartupUri = null;
            try { ScheduledClean.RunSilent(); } catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        SettingsPage.ApplyTheme(AppSettings.Current.Theme);
        base.OnStartup(e);
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}", "Kemptly",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
