using System.Windows;
using System.Windows.Controls;
using WinTidy.Core;

namespace WinTidy.Views;

public partial class ServicesPage : UserControl
{
    public static string[] StartTypes => ServiceManager.StartTypes;

    List<ServiceItem> _all = [];
    ComboBox? _openedCombo;
    bool _loaded;

    public ServicesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; Refresh(); } };
    }

    async void Refresh()
    {
        StatusText.Text = "Loading services…";
        _all = await Task.Run(ServiceManager.Load);
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var q = _all.AsEnumerable();
        if (ShowMs.IsChecked != true) q = q.Where(s => !s.IsMicrosoft);
        if (OnlyAuto.IsChecked == true) q = q.Where(s => s.StartType.StartsWith("Automatic"));
        var f = SearchBox.Text.Trim();
        if (f.Length > 0) q = q.Where(s => s.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase)
                                        || s.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
        var list = q.OrderBy(s => s.DisplayName).ToList();
        SvcGrid.ItemsSource = list;
        StatusText.Text = $"{list.Count} services shown · {list.Count(s => s.IsRunning)} running · " +
                          $"{list.Count(s => s.StartType.StartsWith("Automatic"))} start automatically" +
                          (Admin.IsAdmin ? "" : " · Changing services needs administrator rights");
    }

    void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();
    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    // Only react to changes the user makes (not to the initial binding).
    void Combo_Opened(object? sender, EventArgs e) => _openedCombo = sender as ComboBox;

    void StartType_Changed(object sender, SelectionChangedEventArgs e)
    {
        var combo = (ComboBox)sender;
        if (combo != _openedCombo && !combo.IsKeyboardFocusWithin) return;
        _openedCombo = null;
        var svc = Ui.Item<ServiceItem>(sender);
        if (svc == null || combo.SelectedItem is not string type || type == svc.StartType) return;

        if (!Admin.Require("Changing a service's startup type"))
        {
            svc.Raise(nameof(ServiceItem.StartType));
            return;
        }
        try
        {
            ServiceManager.SetStartType(svc, type);
            StatusText.Text = $"{svc.DisplayName} is now set to {type}.";
        }
        catch (Exception ex)
        {
            svc.Raise(nameof(ServiceItem.StartType));
            Ui.Error(ex.Message);
        }
    }

    async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        var svc = Ui.Item<ServiceItem>(sender);
        if (svc == null) return;
        bool start = !svc.IsRunning;
        if (!Admin.Require(start ? "Starting a service" : "Stopping a service")) return;
        var btn = (Button)sender;
        btn.IsEnabled = false;
        StatusText.Text = $"{(start ? "Starting" : "Stopping")} {svc.DisplayName}…";
        try
        {
            await Task.Run(() => ServiceManager.StartStop(svc, start));
            StatusText.Text = $"{svc.DisplayName}: {svc.Status}";
        }
        catch (Exception ex) { Ui.Error($"Could not {(start ? "start" : "stop")} {svc.DisplayName}:\n{ex.Message}"); }
        finally { btn.IsEnabled = true; }
    }
}
