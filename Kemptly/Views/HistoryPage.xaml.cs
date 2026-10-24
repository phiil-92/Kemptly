using System.Windows;
using System.Windows.Controls;
using Kemptly.Core;

namespace Kemptly.Views;

public partial class HistoryPage : UserControl
{
    public HistoryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        History.Changed += () => Dispatcher.BeginInvoke(Refresh);
    }

    void Refresh()
    {
        var all = History.All;
        HistoryGrid.ItemsSource = all;
        HistoryGrid.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = all.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (StatusText.Text.Length == 0 || !IsLoaded)
            StatusText.Text = $"{all.Count} change(s) recorded. Stored only on this PC.";
    }

    async void Undo_Click(object sender, RoutedEventArgs e)
    {
        var entry = Ui.Item<ChangeEntry>(sender);
        if (entry == null) return;
        if (AppSettings.Current.ConfirmChanges && !Ui.Ask($"{entry.UndoText}: \"{entry.Title}\"?", "Undo")) return;
        try { StatusText.Text = await History.UndoAsync(entry); }
        catch (Exception ex)
        {
            if (Admin.IsAccessDenied(ex)) Admin.Require("Undoing this change");
            else Ui.Error(ex.Message);
        }
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("Clear the change history? Changes themselves stay as they are, but you won't be able to undo them from here anymore.")) return;
        History.Clear();
        StatusText.Text = "History cleared.";
    }
}
