using System.Windows;
using System.Windows.Controls;
using Kemptly.Core;

namespace Kemptly.Views;

public partial class ExtensionsView : UserControl
{
    bool _loaded;

    public ExtensionsView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible && !_loaded) { _loaded = true; Refresh(); } };
    }

    async void Refresh()
    {
        StatusText.Text = "Reading browser extensions…";
        var list = await Task.Run(BrowserExtensions.Load);
        ExtGrid.ItemsSource = list;
        int high = list.Count(x => x.Risk == Severity.High), med = list.Count(x => x.Risk == Severity.Medium);
        StatusText.Text = list.Count == 0 ? "No browser extensions found."
            : $"{list.Count} extension(s): {high} high, {med} medium risk. Browsers must be closed for the list to be fully up to date.";
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    void Folder_Click(object sender, RoutedEventArgs e) => Shell.ShowInExplorer(Ui.Item<BrowserExtension>(sender)?.Folder);

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        var x = Ui.Item<BrowserExtension>(sender);
        if (x == null) return;
        var page = x.Browser switch
        {
            "Firefox" => "about:addons",
            "Edge" => "edge://extensions",
            "Brave" => "brave://extensions",
            "Opera" or "Opera GX" => "opera://extensions",
            "Vivaldi" => "vivaldi://extensions",
            _ => "chrome://extensions",
        };
        Ui.Info($"To remove \"{x.Name}\":\n\n1. Open {x.Browser}{(x.Profile is "Default" or "" ? "" : $" (profile \"{x.Profile}\")")}.\n" +
                $"2. Type {page} into the address bar and press Enter.\n3. Find \"{x.Name}\" and click Remove.\n\n" +
                "Removing it from inside the browser is the safe way: deleting the folder directly can leave the browser confused.");
    }
}
