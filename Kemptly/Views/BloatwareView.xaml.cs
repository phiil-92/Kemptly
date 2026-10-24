using System.Windows;
using System.Windows.Controls;
using Kemptly.Core;

namespace Kemptly.Views;

public partial class BloatwareView : UserControl
{
    List<StoreApp> _apps = [];
    bool _loaded;

    public BloatwareView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible && !_loaded) { _loaded = true; Refresh(); } };
    }

    async void Refresh()
    {
        Busy.Visibility = Visibility.Visible;
        StatusText.Text = "Looking for preinstalled apps…";
        _apps = await Bloatware.LoadAsync();
        AppGrid.ItemsSource = _apps;
        Busy.Visibility = Visibility.Collapsed;
        StatusText.Text = _apps.Count == 0 ? "No known preinstalled extras found. Nice and clean."
            : $"{_apps.Count} preinstalled app(s) found, {_apps.Count(a => a.Recommended)} of them safe to remove.";
    }

    void SelectRecommended_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in _apps) a.Selected = a.Recommended && a.Status != "Removed";
    }

    async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var todo = _apps.Where(a => a.Selected && a.Status != "Removed").ToList();
        if (todo.Count == 0) { Ui.Info("Tick the apps you want to remove first."); return; }
        if (!Ui.Confirm($"Remove these {todo.Count} app(s) for your account?\n\n" + string.Join("\n", todo.Select(a => "  - " + a.Name)) +
                        "\n\nYou can reinstall them from the Microsoft Store.", "Remove apps")) return;
        RemoveBtn.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        int ok = 0;
        foreach (var a in todo)
        {
            a.Status = "Removing…";
            bool removed = await Bloatware.RemoveAsync(a);
            a.Status = removed ? "Removed" : "Couldn't remove";
            a.Selected = false;
            if (removed) ok++;
        }
        Busy.Visibility = Visibility.Collapsed;
        RemoveBtn.IsEnabled = true;
        StatusText.Text = $"Removed {ok} of {todo.Count} app(s).";
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
}
