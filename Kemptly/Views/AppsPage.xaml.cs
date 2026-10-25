using System.Windows;
using System.Windows.Controls;
using Kemptly.Core;

namespace Kemptly.Views;

public partial class AppsPage : UserControl
{
    List<InstalledApp> _all = [];
    bool _loaded;

    public AppsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; Refresh(); } };
    }

    async void Refresh()
    {
        StatusText.Text = "Loading…";
        _all = await Task.Run(InstalledApps.Load);
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var f = SearchBox.Text.Trim();
        var list = f.Length == 0 ? _all : _all.Where(a => a.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                                                       || a.Publisher.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        AppGrid.ItemsSource = list;
        StatusText.Text = $"{list.Count} apps · {Fmt.Bytes(list.Sum(a => a.Size))} reported by installers (some apps don't report their size)";
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    void StoreApps_Click(object sender, RoutedEventArgs e) => Shell.Open("ms-settings:appsfeatures");

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        var app = Ui.Item<InstalledApp>(sender);
        if (app == null) return;
        if (Directory.Exists(app.InstallLocation)) Shell.ShowInExplorer(app.InstallLocation);
        else Ui.Info("This app didn't register its install folder.");
    }

    void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var app = Ui.Item<InstalledApp>(sender);
        if (app == null) return;
        // Current-user entries can be written by any program running as you. Running one from an elevated
        // Kemptly would hand that command admin rights, so show it and warn first.
        var warning = Admin.IsAdmin && app.Scope == "Current user"
            ? "\n\nWARNING: Kemptly is running as administrator and this entry was registered for your account only. " +
              "Its uninstaller will also run as administrator. Only continue if the command below looks right."
            : "";
        if (!Ui.Ask($"Start the uninstaller for \"{app.Name}\"?\n\nThe app's own uninstaller will open - follow its steps.{warning}" +
                    $"\n\nCommand:\n{app.UninstallString}", "Uninstall")) return;
        try
        {
            InstalledApps.RunUninstaller(app);
            History.Add(ChangeKind.AppUninstalled, $"Started the uninstaller for {app.Name}", app.UninstallString);
            WatchUninstall(app);
        }
        catch (Exception ex)
        {
            Ui.Error($"Could not start the uninstaller:\n{ex.Message}\n\nCommand: {app.UninstallString}\n\n" +
                     "If the uninstaller is missing, use the ... button next to Uninstall and choose Forced uninstall.");
        }
    }

    readonly HashSet<string> _watching = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Waits until the app's uninstall entry disappears (its uninstaller finished), then offers to remove leftovers.</summary>
    async void WatchUninstall(InstalledApp app)
    {
        var id = app.Hive + "\\" + app.KeyPath;
        if (!_watching.Add(id)) return;
        StatusText.Text = $"Waiting for the {app.Name} uninstaller to finish… Kemptly then looks for leftovers.";
        var until = DateTime.Now.AddMinutes(30);
        try
        {
            while (DateTime.Now < until)
            {
                await Task.Delay(2000);
                if (InstalledApps.StillInstalled(app)) continue;
                Refresh();
                ShowLeftovers(app, forced: false);
                return;
            }
            StatusText.Text = $"{app.Name} still seems to be installed. If its uninstaller failed, try Forced uninstall from the ... menu.";
        }
        finally { _watching.Remove(id); }
    }

    void ShowLeftovers(InstalledApp app, bool forced)
    {
        var w = new LeftoversWindow(app, forced);
        w.ShowDialog();
        if (w.Changed) Refresh();
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } b)
        {
            menu.PlacementTarget = b;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.DataContext = b.DataContext;
            menu.IsOpen = true;
        }
    }

    void Forced_Click(object sender, RoutedEventArgs e)
    {
        var app = Ui.Item<InstalledApp>(sender);
        if (app == null) return;
        if (!Ui.Confirm($"Forced uninstall of \"{app.Name}\"?\n\nKemptly won't run the app's own uninstaller. Instead it looks for the app's folders, " +
                        "registry keys, shortcuts, startup entries and tasks, and its entry in this list. You review everything before anything is removed.\n\n" +
                        "Use this only when the normal Uninstall doesn't work.", "Forced uninstall")) return;
        ShowLeftovers(app, forced: true);
    }
}
