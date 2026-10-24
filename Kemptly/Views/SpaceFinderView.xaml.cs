using System.Windows;
using System.Windows.Controls;
using Kemptly.Core;

namespace Kemptly.Views;

public partial class SpaceFinderView : UserControl
{
    CancellationTokenSource? _cts;

    public SpaceFinderView() => InitializeComponent();

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        var cts = _cts = new CancellationTokenSource();
        ScanBtn.Content = "Stop";
        Busy.Visibility = Visibility.Visible;
        try
        {
            var items = await SpaceFinder.FindAsync(new Progress<string>(s => ProgressText.Text = s), cts.Token);
            ItemList.ItemsSource = items;
            ProgressText.Text = $"Found {items.Count} item(s), {Fmt.Bytes(items.Sum(i => i.Size))} in total.";
        }
        catch (OperationCanceledException) { ProgressText.Text = "Stopped."; }
        finally
        {
            _cts = null;
            ScanBtn.Content = "Find space";
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    void Action_Click(object sender, RoutedEventArgs e)
    {
        var item = Ui.Item<SpaceItem>(sender);
        if (item == null) return;
        switch (item.Action)
        {
            case SpaceAction.ShowFile:
            case SpaceAction.OpenFolder:
                Shell.ShowInExplorer(item.Path);
                break;
            case SpaceAction.DiskCleanup:
                Shell.Open("cleanmgr.exe");
                break;
            case SpaceAction.SystemProtection:
                Shell.Open("SystemPropertiesProtection.exe");
                break;
            case SpaceAction.ToolsPage:
                MainWindow.Instance.Navigate("tools");
                break;
            case SpaceAction.DeleteFolder when item.Path != null:
                if (!Ui.Confirm($"Move this folder ({item.SizeText}) to the Recycle Bin?\n\n{item.Path}", "Delete node_modules")) return;
                if (Shell.Recycle(item.Path))
                {
                    History.Add(ChangeKind.FilesDeleted, $"Moved node_modules ({item.SizeText}) to the Recycle Bin", item.Path,
                        History.Data(("recycled", true)));
                    if (ItemList.ItemsSource is List<SpaceItem> list)
                    {
                        list.Remove(item);
                        ItemList.Items.Refresh();
                    }
                }
                else Ui.Error("The folder could not be deleted (files may be in use).");
                break;
        }
    }
}
