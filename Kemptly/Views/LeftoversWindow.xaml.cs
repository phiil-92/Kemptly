using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using Kemptly.Core;

namespace Kemptly.Views;

/// <summary>Shows what an app left behind after its uninstaller (or for a forced uninstall) and removes the selected items.</summary>
public partial class LeftoversWindow : Window
{
    readonly InstalledApp _app;
    readonly bool _forced;
    readonly ObservableCollection<Leftover> _items = new();
    bool _busy;

    /// <summary>True if something was removed (the app list should be refreshed).</summary>
    public bool Changed { get; private set; }

    public LeftoversWindow(InstalledApp app, bool forced)
    {
        InitializeComponent();
        _app = app;
        _forced = forced;
        Owner = Application.Current.MainWindow;
        LeftoverGrid.ItemsSource = _items;

        Title = $"Leftovers - {app.Name}";
        TitleText.Text = forced ? $"Forced uninstall: {app.Name}" : $"Leftovers of {app.Name}";
        IntroText.Text = (forced
            ? "Kemptly skips the app's own uninstaller and removes what belongs to it directly. Use this when the uninstaller is broken or missing. "
            : "The uninstaller has finished. These items were left behind. ")
            + "Folders and shortcuts go to the Recycle Bin. Registry keys and scheduled tasks are backed up first, so you can undo on the History page.";
        if (forced)
        {
            WarningBox.Visibility = Visibility.Visible;
            WarningText.Text = "The app is still installed. Close it first. Removing its files without its uninstaller can leave drivers or services behind, so prefer the normal Uninstall when it works.";
        }
        Loaded += async (_, _) => await ScanAsync();
    }

    async Task ScanAsync()
    {
        _busy = true;
        List<Leftover> found;
        try { found = await Task.Run(() => Leftovers.Scan(_app, _forced)); }
        catch (Exception ex) { ScanningPanel.Visibility = Visibility.Collapsed; SummaryText.Text = "Scan failed: " + ex.Message; _busy = false; return; }

        ScanningPanel.Visibility = Visibility.Collapsed;
        foreach (var l in found) _items.Add(l);
        if (found.Count == 0) { EmptyState.Visibility = Visibility.Visible; _busy = false; UpdateSummary(); return; }
        LeftoverGrid.Visibility = Visibility.Visible;
        _busy = false;
        UpdateSummary();

        // Folder sizes can take a moment, fill them in afterwards.
        foreach (var l in found.Where(l => l.Kind == LeftoverKind.Folder))
            l.Size = await Task.Run(() => Leftovers.FolderSize(l.Location));
        UpdateSummary();
    }

    void UpdateSummary()
    {
        var sel = _items.Where(i => i.Selected && i.CanSelect).ToList();
        RemoveBtn.IsEnabled = !_busy && sel.Count > 0;
        if (_items.Count == 0) { SummaryText.Text = ""; return; }
        var removed = _items.Count(i => i.Status == "Removed");
        var text = $"{_items.Count} item(s) found";
        if (removed > 0) text += $", {removed} removed";
        text += $" · {sel.Count} selected ({Fmt.Bytes(sel.Sum(i => i.Size))})";
        if (sel.Any(i => i.NeedsAdmin) && !Admin.IsAdmin) text += " · Windows will ask for administrator permission once";
        SummaryText.Text = text;
    }

    void Check_Click(object sender, RoutedEventArgs e) => UpdateSummary();

    void Show_Click(object sender, RoutedEventArgs e)
    {
        var l = Ui.Item<Leftover>(sender);
        if (l == null) return;
        switch (l.Kind)
        {
            case LeftoverKind.Folder:
            case LeftoverKind.Shortcut:
                Shell.ShowInExplorer(l.Location);
                break;
            case LeftoverKind.StartupEntry:
                Shell.ShowInExplorer(l.Startup?.ShortcutFile ?? l.Startup?.FilePath);
                break;
            case LeftoverKind.ScheduledTask:
                Shell.Open("taskschd.msc");
                break;
            default:
                // Registry Editor opens at the key it was last closed on, so point that at this key first.
                if (l.Status == "Removed") { Ui.Info("This key has been removed."); return; }
                try
                {
                    using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit");
                    k.SetValue("LastKey", "Computer\\" + (l.Hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE\\" : "HKEY_CURRENT_USER\\") + l.SubKey);
                }
                catch { }
                Shell.Open("regedit.exe");
                break;
        }
    }

    async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var sel = _items.Where(i => i.Selected && i.CanSelect).ToList();
        if (sel.Count == 0) return;
        var msg = $"Remove {sel.Count} item(s) left by {_app.Name}?\n\n" +
                  "Folders and shortcuts go to the Recycle Bin (very large folders may be deleted directly if they don't fit). " +
                  "Registry keys and scheduled tasks are backed up so you can undo this on the History page.";
        if (!Ui.Confirm(msg, _forced ? "Forced uninstall" : "Remove leftovers")) return;

        _busy = true;
        RemoveBtn.IsEnabled = false;
        SummaryText.Text = "Removing…";
        var (ok, failed) = await Leftovers.RemoveAsync(sel, _app.Name);
        if (ok > 0) Changed = true;
        _busy = false;
        UpdateSummary();
        SummaryText.Text = failed == 0 ? $"Removed {ok} item(s). " + SummaryText.Text
                                       : $"Removed {ok}, {failed} could not be removed (see Status). " + SummaryText.Text;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
