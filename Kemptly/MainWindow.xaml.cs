using System.Windows;
using System.Windows.Controls;
using Kemptly.Core;
using Kemptly.Views;

namespace Kemptly;

public partial class MainWindow : Window
{
    public static MainWindow Instance { get; private set; } = null!;

    readonly Dictionary<string, UserControl> _pages = new();

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;

        if (Admin.IsAdmin)
        {
            AdminIcon.Text = "";
            AdminTitle.Text = "Administrator";
            AdminText.Text = "All features are available.";
            ElevateBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            AdminTitle.Text = "Standard user";
            AdminText.Text = "System-wide cleanup, services and machine startup entries need admin rights.";
        }

        // Optional start page, e.g. "Kemptly.exe --page cleaner" (handy for shortcuts).
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--page");
        if (i > 0 && i + 1 < args.Length)
        {
            // "--scan" also starts the page's scan right away.
            var page = Navigate(args[i + 1]);
            if (args.Contains("--scan"))
            {
                if (page is CleanerPage cp) cp.StartScan();
                else if (page is SecurityPage sp) sp.StartScan();
            }
        }
        else Nav.SelectedIndex = 0;
    }

    /// <summary>Switches to a page by its key and returns it.</summary>
    public UserControl Navigate(string key)
    {
        foreach (ListBoxItem item in Nav.Items)
            if ((string)item.Tag == key) { Nav.SelectedItem = item; break; }
        return GetPage(key);
    }

    UserControl GetPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "cleaner" => new CleanerPage(),
                "disk" => new DiskAnalyzerPage(),
                "apps" => new AppsPage(),
                "startup" => new StartupPage(),
                "processes" => new ProcessesPage(),
                "services" => new ServicesPage(),
                "network" => new NetworkPage(),
                "security" => new SecurityPage(),
                "tools" => new ToolsPage(),
                _ => new DashboardPage(),
            };
            _pages[key] = page;
        }
        return page;
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is ListBoxItem item)
            Host.Content = GetPage((string)item.Tag);
    }

    void Elevate_Click(object sender, RoutedEventArgs e) => Admin.RestartElevated();
}
