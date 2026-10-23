using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WinTidy.Core;

namespace WinTidy.Views;

public class FileEntry
{
    public required string FullPath { get; init; }
    public required long Size { get; init; }
    public required DateTime Modified { get; init; }
    public string Name => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
    public string SizeText => Fmt.Bytes(Size);
}

public record BarEntry(string Name, string? FullPath, long Size, double Percent)
{
    public string SizeText => Fmt.Bytes(Size);
}

public partial class DiskAnalyzerPage : UserControl
{
    const int MaxFiles = 300;
    readonly ObservableCollection<FileEntry> _files = new();
    CancellationTokenSource? _cts;
    long _scannedFiles, _scannedBytes;

    public DiskAnalyzerPage()
    {
        InitializeComponent();
        FileGrid.ItemsSource = _files;
        PathBox.Text = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            var used = d.TotalSize - d.TotalFreeSpace;
            var btn = new Button
            {
                Content = $"{d.Name.TrimEnd('\\')}  {Fmt.Bytes(d.TotalFreeSpace)} free",
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = 12,
                Tag = d.Name,
                ToolTip = $"{d.VolumeLabel}  —  {Fmt.Bytes(used)} used of {Fmt.Bytes(d.TotalSize)}",
            };
            btn.Click += (_, _) => { PathBox.Text = (string)btn.Tag; StartScan(); };
            DrivePanel.Children.Add(btn);
        }
    }

    void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanBtn.Content = busy ? "Stop" : "Scan";
    }

    void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        StartScan();
    }

    void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) StartScan();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = PathBox.Text };
        if (dlg.ShowDialog() == true) { PathBox.Text = dlg.FolderName; StartScan(); }
    }

    void Up_Click(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(PathBox.Text.TrimEnd('\\'));
        if (parent != null) { PathBox.Text = parent.FullName; StartScan(); }
    }

    void FolderList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderList.SelectedItem is BarEntry { FullPath: not null } b && Directory.Exists(b.FullPath))
        {
            PathBox.Text = b.FullPath;
            StartScan();
        }
    }

    async void StartScan()
    {
        _cts?.Cancel();
        var root = PathBox.Text.Trim();
        if (!Directory.Exists(root)) { Ui.Error("That folder doesn't exist."); return; }
        if (!double.TryParse(MinSizeBox.Text, out var minMb) || minMb < 0) minMb = 100;
        long minBytes = (long)(minMb * 1024 * 1024);

        var cts = _cts = new CancellationTokenSource();
        _scannedFiles = _scannedBytes = 0;
        SetBusy(true);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => StatusText.Text =
            $"Scanning… {Interlocked.Read(ref _scannedFiles):N0} files, {Fmt.Bytes(Interlocked.Read(ref _scannedBytes))}";
        timer.Start();

        try
        {
            var result = await Task.Run(() => DoScan(root, minBytes, cts.Token));
            Show(result);
            StatusText.Text = $"{result.Files:N0} files, {Fmt.Bytes(result.Total)} in {root}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan stopped.";
        }
        finally
        {
            timer.Stop();
            if (_cts == cts) { _cts = null; SetBusy(false); }
        }
    }

    record ScanResult(long Total, long Files, List<BarEntry> Folders, List<BarEntry> Types, List<FileEntry> Largest);

    ScanResult DoScan(string root, long minBytes, CancellationToken ct)
    {
        var rootFull = Path.GetFullPath(root);
        int rootLen = rootFull.TrimEnd('\\').Length + 1;
        var heap = new PriorityQueue<FileEntry, long>();
        var folders = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long total = 0, count = 0;

        foreach (var f in FileWalker.Walk(rootFull, ct: ct))
        {
            long len;
            try { len = f.Length; } catch { continue; }
            total += len;
            count++;
            if ((count & 255) == 0)
            {
                Interlocked.Exchange(ref _scannedFiles, count);
                Interlocked.Exchange(ref _scannedBytes, total);
            }

            var rel = f.FullName.Length > rootLen ? f.FullName[rootLen..] : f.Name;
            int slash = rel.IndexOf('\\');
            var key = slash < 0 ? "" : rel[..slash];
            folders[key] = folders.GetValueOrDefault(key) + len;

            var ext = f.Extension.Length is > 0 and < 12 ? f.Extension.ToLowerInvariant() : "(no extension)";
            types[ext] = types.GetValueOrDefault(ext) + len;

            // System files (pagefile, hiberfil) can't be deleted here, so don't list them.
            if (len >= minBytes && (f.Attributes & FileAttributes.System) == 0)
            {
                heap.Enqueue(new FileEntry { FullPath = f.FullName, Size = len, Modified = f.LastWriteTime }, len);
                if (heap.Count > MaxFiles) heap.Dequeue();
            }
        }

        double max = Math.Max(1, folders.Values.DefaultIfEmpty(0).Max());
        var folderList = folders.OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key == ""
                ? new BarEntry("(files directly in this folder)", null, kv.Value, kv.Value / max * 100)
                : new BarEntry(kv.Key, Path.Combine(rootFull, kv.Key), kv.Value, kv.Value / max * 100))
            .ToList();

        double maxT = Math.Max(1, types.Values.DefaultIfEmpty(0).Max());
        var typeList = types.OrderByDescending(kv => kv.Value).Take(60)
            .Select(kv => new BarEntry(kv.Key, null, kv.Value, kv.Value / maxT * 100)).ToList();

        var largest = new List<FileEntry>();
        while (heap.Count > 0) largest.Add(heap.Dequeue());
        largest.Reverse();
        return new ScanResult(total, count, folderList, typeList, largest);
    }

    void Show(ScanResult r)
    {
        FolderList.ItemsSource = r.Folders;
        TypeList.ItemsSource = r.Types;
        _files.Clear();
        foreach (var f in r.Largest) _files.Add(f);
    }

    void Show_Click(object sender, RoutedEventArgs e) => Shell.ShowInExplorer(Ui.Item<FileEntry>(sender)?.FullPath);

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var clicked = Ui.Item<FileEntry>(sender);
        if (clicked == null) return;
        // If the clicked row is part of a multi-selection, delete the whole selection.
        var targets = FileGrid.SelectedItems.Contains(clicked) && FileGrid.SelectedItems.Count > 1
            ? FileGrid.SelectedItems.Cast<FileEntry>().ToList()
            : [clicked];

        bool recycle = RecycleCheck.IsChecked == true;
        var what = targets.Count == 1 ? $"\"{targets[0].Name}\" ({targets[0].SizeText})"
                                      : $"{targets.Count} files ({Fmt.Bytes(targets.Sum(t => t.Size))})";
        var how = recycle ? "Move to the Recycle Bin" : "PERMANENTLY delete";
        if (!Ui.Confirm($"{how} {what}?")) return;

        var failed = new List<string>();
        foreach (var t in targets)
        {
            try
            {
                if (recycle) { if (!Shell.Recycle(t.FullPath)) throw new IOException("Could not move to Recycle Bin"); }
                else File.Delete(t.FullPath);
                _files.Remove(t);
            }
            catch (Exception ex) { failed.Add($"{t.Name}: {ex.Message}"); }
        }
        if (failed.Count > 0) Ui.Error("Some files could not be deleted (in use or protected):\n\n" + string.Join("\n", failed.Take(10)));
    }
}
