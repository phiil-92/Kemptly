using System.Windows;
using System.Windows.Controls;
using Kemptly.Core;

namespace Kemptly.Views;

public partial class StartupPage : UserControl
{
    List<TaskItem> _allTasks = [];
    bool _loaded;

    public StartupPage()
    {
        InitializeComponent();
        OnlyLogonTasks.IsChecked = false;
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; Refresh(); } };
    }

    async void Refresh()
    {
        Busy.Visibility = Visibility.Visible;
        StatusText.Text = "Loading…";
        try
        {
            var items = await Task.Run(StartupManager.Load);
            StartupGrid.ItemsSource = items.OrderByDescending(i => i.Enabled).ThenBy(i => i.DisplayName).ToList();
            try { _allTasks = await Task.Run(TaskSchedulerReader.Load); }
            catch (Exception ex) { _allTasks = []; StatusText.Text = "Could not read scheduled tasks: " + ex.Message; }
            ApplyTaskFilter();
            StatusText.Text = $"{items.Count(i => i.Enabled)} of {items.Count} startup apps enabled · {_allTasks.Count(t => !t.IsMicrosoft)} third-party scheduled tasks.";
        }
        finally { Busy.Visibility = Visibility.Collapsed; }
    }

    void ApplyTaskFilter()
    {
        IEnumerable<TaskItem> q = _allTasks;
        if (ShowMsTasks.IsChecked != true) q = q.Where(t => !t.IsMicrosoft);
        if (OnlyLogonTasks.IsChecked == true) q = q.Where(t => t.RunsAtLogonOrBoot);
        TaskGrid.ItemsSource = q.OrderBy(t => t.FolderPath).ThenBy(t => t.Name).ToList();
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    void Filter_Click(object sender, RoutedEventArgs e) => ApplyTaskFilter();

    void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        var item = Ui.Item<StartupItem>(sender);
        if (item == null) return;
        bool want = ((CheckBox)sender).IsChecked == true;
        try
        {
            StartupManager.SetEnabled(item, want);
            StatusText.Text = $"{item.DisplayName} will {(want ? "now" : "no longer")} start with Windows.";
        }
        catch (Exception ex)
        {
            item.Raise(nameof(StartupItem.Enabled)); // snap the checkbox back
            if (Admin.IsAccessDenied(ex)) Admin.Require($"Changing \"{item.DisplayName}\" (an all-users entry)");
            else Ui.Error(ex.Message);
        }
    }

    void StartupShow_Click(object sender, RoutedEventArgs e)
    {
        var item = Ui.Item<StartupItem>(sender);
        if (item == null) return;
        Shell.ShowInExplorer(item.FileExists ? item.FilePath : item.ShortcutFile ?? item.FilePath);
    }

    void StartupRemove_Click(object sender, RoutedEventArgs e)
    {
        var item = Ui.Item<StartupItem>(sender);
        if (item == null) return;
        if (!Ui.Confirm($"Remove the startup entry \"{item.DisplayName}\" completely?\n\nThe program itself is not uninstalled. " +
                        "Tip: unticking it instead is reversible.")) return;
        try
        {
            StartupManager.Remove(item);
            Refresh();
        }
        catch (Exception ex)
        {
            if (Admin.IsAccessDenied(ex)) Admin.Require($"Removing \"{item.DisplayName}\"");
            else Ui.Error(ex.Message);
        }
    }

    async void TaskToggle_Click(object sender, RoutedEventArgs e)
    {
        var item = Ui.Item<TaskItem>(sender);
        if (item == null) return;
        bool want = ((CheckBox)sender).IsChecked == true;
        try
        {
            await Task.Run(() => TaskSchedulerReader.SetEnabled(item, want));
            StatusText.Text = $"Task \"{item.Name}\" {(want ? "enabled" : "disabled")}.";
        }
        catch (Exception ex)
        {
            item.Raise(nameof(TaskItem.Enabled));
            if (Admin.IsAccessDenied(ex)) Admin.Require($"Changing the task \"{item.Name}\"");
            else Ui.Error(ex.Message);
        }
    }

    void TaskShow_Click(object sender, RoutedEventArgs e)
    {
        var item = Ui.Item<TaskItem>(sender);
        if (item?.ExePath != null && File.Exists(item.ExePath)) Shell.ShowInExplorer(item.ExePath);
        else Shell.Open("taskschd.msc");
    }
}
