using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Kemptly.Core;

namespace Kemptly.Views;

public partial class DuplicatesView : UserControl
{
    List<DupGroup> _groups = [];
    CancellationTokenSource? _cts;

    public DuplicatesView()
    {
        InitializeComponent();
        PathBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        RecycleCheck.IsChecked = AppSettings.Current.RecycleByDefault;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = PathBox.Text };
        if (dlg.ShowDialog() == true) PathBox.Text = dlg.FolderName;
    }

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        var root = PathBox.Text.Trim();
        if (!Directory.Exists(root)) { Ui.Error("That folder doesn't exist."); return; }
        if (!double.TryParse(MinSizeBox.Text, out var mb) || mb < 0) mb = 1;

        var cts = _cts = new CancellationTokenSource();
        ScanBtn.Content = "Stop";
        DeleteBtn.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        var progress = new Progress<string>(s => ProgressText.Text = s);
        try
        {
            _groups = await Task.Run(() => DuplicateFinder.Find(root, (long)(mb * 1024 * 1024), progress, cts.Token));
            GroupList.ItemsSource = _groups;
            ProgressText.Text = "";
            StatusText.Text = _groups.Count == 0 ? "No duplicates found."
                : $"{_groups.Count} group(s) of duplicates, {Fmt.Bytes(_groups.Sum(g => g.Wasted))} could be freed.";
            DeleteBtn.IsEnabled = _groups.Count > 0;
        }
        catch (OperationCanceledException) { ProgressText.Text = "Stopped."; }
        finally
        {
            _cts = null;
            ScanBtn.Content = "Find duplicates";
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    void Show_Click(object sender, RoutedEventArgs e) => Shell.ShowInExplorer(Ui.Item<DupFile>(sender)?.FullPath);

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        // Never delete every copy of a file: groups where all boxes are ticked are skipped.
        var unsafeGroups = _groups.Count(g => g.Files.All(f => f.Delete));
        var targets = _groups.Where(g => !g.Files.All(f => f.Delete)).SelectMany(g => g.Files.Where(f => f.Delete)).ToList();
        if (targets.Count == 0) { Ui.Info(unsafeGroups > 0 ? "Keep at least one copy unticked in each group." : "Nothing is ticked."); return; }

        bool recycle = RecycleCheck.IsChecked == true;
        var size = Fmt.Bytes(targets.Sum(t => t.Size));
        var msg = $"{(recycle ? "Move" : "PERMANENTLY delete")} {targets.Count} duplicate file(s) ({size}){(recycle ? " to the Recycle Bin" : "")}?" +
                  (unsafeGroups > 0 ? $"\n\n{unsafeGroups} group(s) with every copy ticked will be skipped." : "");
        if (!Ui.Confirm(msg, "Delete duplicates")) return;

        int ok = 0;
        long freed = 0;
        foreach (var f in targets)
        {
            try
            {
                if (recycle) { if (!Shell.Recycle(f.FullPath)) continue; }
                else File.Delete(f.FullPath);
                ok++;
                freed += f.Size;
            }
            catch { }
        }
        History.Add(ChangeKind.FilesDeleted, $"{(recycle ? "Moved" : "Deleted")} {ok} duplicate file(s) ({Fmt.Bytes(freed)}){(recycle ? " to the Recycle Bin" : "")}",
            string.Join("\n", targets.Take(10).Select(t => t.FullPath)), History.Data(("recycled", recycle)));

        // Drop deleted files from the list.
        foreach (var g in _groups) g.Files.RemoveAll(f => f.Delete && !File.Exists(f.FullPath));
        _groups = _groups.Where(g => g.Files.Count > 1).ToList();
        GroupList.ItemsSource = _groups;
        StatusText.Text = $"{ok} of {targets.Count} file(s) {(recycle ? "moved to the Recycle Bin" : "deleted")}, {Fmt.Bytes(freed)}.";
        DeleteBtn.IsEnabled = _groups.Count > 0;
    }
}
