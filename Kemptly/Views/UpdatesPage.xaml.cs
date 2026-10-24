using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Kemptly.Core;

namespace Kemptly.Views;

public partial class UpdatesPage : UserControl
{
    readonly ObservableCollection<WingetUpdate> _updates = new();
    string? _winget;
    bool _busy;

    public UpdatesPage()
    {
        InitializeComponent();
        UpdateGrid.ItemsSource = _updates;
    }

    void SetBusy(bool busy, string text = "")
    {
        _busy = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CheckBtn.IsEnabled = !busy;
        UpdateAllBtn.IsEnabled = !busy && _updates.Any(u => u.CanUpdate);
        ProgressText.Text = text;
    }

    void ShowEmpty(string title, string text)
    {
        UpdateGrid.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        EmptyTitle.Text = title;
        EmptyText.Text = text;
    }

    async void Check_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Looking for winget…");
        try
        {
            _winget ??= await Winget.FindAsync();
            if (_winget == null)
            {
                ShowEmpty("winget isn't available",
                    Admin.IsAdmin
                        ? "Kemptly couldn't find winget in a protected location. Restart Kemptly without administrator rights and try again."
                        : "Install \"App Installer\" from the Microsoft Store, then try again.");
                return;
            }
            SetBusy(true, "Checking for updates… (this can take a minute)");
            var list = await Winget.ListAsync(_winget);
            _updates.Clear();
            foreach (var u in list) _updates.Add(u);
            if (list.Count == 0) ShowEmpty("Everything is up to date", "winget found no updates for your installed programs.");
            else
            {
                EmptyState.Visibility = Visibility.Collapsed;
                UpdateGrid.Visibility = Visibility.Visible;
            }
            SetBusy(false, list.Count == 0 ? "" : $"{list.Count} update(s) available.");
        }
        catch (Exception ex) { SetBusy(false, "Check failed: " + ex.Message); }
        finally { if (_busy) SetBusy(false, ProgressText.Text); }
    }

    async Task UpdateOne(WingetUpdate u)
    {
        u.Busy = true;
        u.Status = "Updating…";
        try
        {
            var r = await Winget.UpgradeAsync(_winget!, u);
            u.Status = r.Code == 0 ? "Updated" : Winget.Explain(r.Code);
        }
        catch (Exception ex) { u.Status = "Failed: " + ex.Message; }
        finally { u.Busy = false; }
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        var u = Ui.Item<WingetUpdate>(sender);
        if (u == null || _winget == null) return;
        SetBusy(true, $"Updating {u.Name}…");
        await UpdateOne(u);
        SetBusy(false, $"{u.Name}: {u.Status}");
    }

    async void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        var todo = _updates.Where(u => u.CanUpdate).ToList();
        if (todo.Count == 0 || _winget == null) return;
        if (!Ui.Ask($"Update {todo.Count} app(s)? Close them first; installers may show permission prompts.", "Update all")) return;
        int ok = 0;
        for (int i = 0; i < todo.Count; i++)
        {
            SetBusy(true, $"Updating {i + 1} of {todo.Count}: {todo[i].Name}…");
            await UpdateOne(todo[i]);
            if (todo[i].Status == "Updated") ok++;
        }
        SetBusy(false, $"Done: {ok} of {todo.Count} updated.");
    }
}
