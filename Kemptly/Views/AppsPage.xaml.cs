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
            StatusText.Text = $"Uninstaller for {app.Name} started. Press Refresh when it's finished.";
        }
        catch (Exception ex) { Ui.Error($"Could not start the uninstaller:\n{ex.Message}\n\nCommand: {app.UninstallString}"); }
    }
}
