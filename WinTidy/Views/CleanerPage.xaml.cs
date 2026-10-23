using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WinTidy.Core;

namespace WinTidy.Views;

public partial class CleanerPage : UserControl
{
    readonly ObservableCollection<CleanCategory> _cats = new(Cleaner.CreateCategories());
    bool _busy;

    public CleanerPage()
    {
        InitializeComponent();
        CatGrid.ItemsSource = _cats;
        foreach (var c in _cats) c.PropertyChanged += OnCategoryChanged;
    }

    void OnCategoryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CleanCategory.IsSelected) or nameof(CleanCategory.Size)) UpdateTotal();
    }

    void UpdateTotal()
    {
        var selected = _cats.Where(c => c.IsSelected && c.Scanned).ToList();
        TotalText.Text = selected.Count == 0 ? "–" : Fmt.Bytes(selected.Sum(c => c.Size));
        CleanBtn.IsEnabled = !_busy && selected.Any(c => c.Size > 0);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanBtn.IsEnabled = !busy;
        UpdateTotal();
    }

    public async void StartScan()
    {
        if (_busy) return;
        SetBusy(true);
        StatusText.Text = "Scanning…";
        foreach (var c in _cats) c.Status = "Scanning…";

        var tasks = _cats.Select(async c =>
        {
            var (size, files) = await Task.Run(() => Cleaner.Scan(c, CancellationToken.None));
            c.Size = size;
            c.Files = files;
            c.Scanned = true;
            c.Status = size == 0 ? "Nothing to clean" : "Ready";
        });
        await Task.WhenAll(tasks);

        var total = _cats.Sum(c => c.Size);
        StatusText.Text = $"Found {Fmt.Bytes(total)} of removable files in total.";
        SetBusy(false);
    }

    void Scan_Click(object sender, RoutedEventArgs e) => StartScan();

    async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var selected = _cats.Where(c => c.IsSelected && c.Scanned && c.Size > 0).ToList();
        if (selected.Count == 0) return;

        var list = string.Join("\n", selected.Select(c => $"  • {c.Name}  ({c.SizeText})"));
        var extra = selected.Any(c => c.Name.StartsWith("Old downloads"))
            ? "\n\nWARNING: 'Old downloads' permanently deletes files from your Downloads folder."
            : "";
        if (!Ui.Confirm($"Permanently delete these files?\n\n{list}{extra}", "Clean junk files")) return;

        SetBusy(true);
        long freedTotal = 0;
        int failedTotal = 0;
        foreach (var c in selected)
        {
            c.Status = "Cleaning…";
            var (freed, deleted, failed) = await Task.Run(() => Cleaner.Clean(c, CancellationToken.None));
            freedTotal += freed;
            failedTotal += failed;
            c.Status = failed == 0
                ? $"Freed {Fmt.Bytes(freed)}"
                : $"Freed {Fmt.Bytes(freed)}, {failed} in use/denied";

            var (size, files) = await Task.Run(() => Cleaner.Scan(c, CancellationToken.None));
            c.Size = size;
            c.Files = files;
        }

        StatusText.Text = $"Done. Freed {Fmt.Bytes(freedTotal)}." +
            (failedTotal > 0 ? $" {failedTotal} files were skipped because they are in use or need admin rights." : "");
        SetBusy(false);
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        var c = Ui.Item<CleanCategory>(sender);
        if (c == null) return;
        if (c.IsRecycleBin) { Shell.Open("shell:RecycleBinFolder"); return; }
        var folder = c.FirstExistingFolder;
        if (folder == null) Ui.Info("None of this category's folders exist on this PC.");
        else Shell.ShowInExplorer(folder);
    }
}
