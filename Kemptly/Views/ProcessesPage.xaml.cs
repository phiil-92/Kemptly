using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Kemptly.Core;

namespace Kemptly.Views;

public partial class ProcessesPage : UserControl
{
    readonly ObservableCollection<ProcRow> _rows = new();
    readonly ProcessSampler _sampler = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    List<ProcSample> _last = [];
    bool _sampling;

    public ProcessesPage()
    {
        InitializeComponent();
        ProcGrid.ItemsSource = _rows;
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
        view.SortDescriptions.Add(new SortDescription(nameof(ProcRow.Memory), ListSortDirection.Descending));
        view.IsLiveSorting = true;
        MemCol.SortDirection = ListSortDirection.Descending;

        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); if (AutoCheck.IsChecked == true) _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    async void Refresh()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            _last = await Task.Run(_sampler.Sample);
            Apply();
        }
        finally { _sampling = false; }
    }

    void Apply()
    {
        var filter = SearchBox.Text.Trim();
        bool grouped = GroupCheck.IsChecked == true;
        var groups = grouped
            ? _last.GroupBy(s => s.Name.ToLowerInvariant())
            : _last.GroupBy(s => s.Pid.ToString());

        var existing = _rows.ToDictionary(r => r.Key);
        var seen = new HashSet<string>();
        foreach (var g in groups)
        {
            var first = g.First();
            var path = g.Select(x => x.Path).FirstOrDefault(p => p != null);
            if (filter.Length > 0 && !first.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !(path?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)) continue;

            seen.Add(g.Key);
            if (!existing.TryGetValue(g.Key, out var row))
            {
                row = new ProcRow { Key = g.Key, Name = grouped ? first.Name : $"{first.Name} ({first.Pid})" };
                _rows.Add(row);
            }
            row.Count = g.Count();
            row.Memory = g.Sum(x => x.Memory);
            row.Cpu = g.Sum(x => x.Cpu);
            row.Path = path;
            row.Pids = g.Select(x => x.Pid).ToList();
        }
        for (int i = _rows.Count - 1; i >= 0; i--)
            if (!seen.Contains(_rows[i].Key)) _rows.RemoveAt(i);

        var mem = SystemInfo.Memory();
        StatusText.Text = $"{_last.Count} processes · {mem.Load}% memory in use ({Fmt.Bytes(mem.Total - mem.Available)} of {Fmt.Bytes(mem.Total)})";
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e) => Apply();

    void Group_Click(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        Apply();
    }

    void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (AutoCheck.IsChecked == true) _timer.Start(); else _timer.Stop();
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    void Show_Click(object sender, RoutedEventArgs e)
    {
        var row = Ui.Item<ProcRow>(sender);
        if (row?.Path != null) Shell.ShowInExplorer(row.Path);
        else Ui.Info("The location of this process isn't accessible (it's probably a protected system process).");
    }

    void Kill_Click(object sender, RoutedEventArgs e)
    {
        var row = Ui.Item<ProcRow>(sender);
        if (row == null) return;
        var baseName = _last.FirstOrDefault(s => row.Pids.Contains(s.Pid))?.Name ?? row.Name;
        if (ProcessSampler.IsCritical(baseName))
        {
            Ui.Error($"{baseName} is a critical Windows process. Ending it would crash or destabilise Windows.");
            return;
        }
        var what = row.Count > 1 ? $"all {row.Count} \"{baseName}\" processes" : $"\"{baseName}\"";
        if (!Ui.Confirm($"End {what}? Unsaved work in that app will be lost.")) return;

        int failed = 0;
        foreach (var pid in row.Pids)
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
            catch (ArgumentException) { /* already exited */ }
            catch { failed++; }
        }
        if (failed < row.Pids.Count) History.Add(ChangeKind.ProcessEnded, $"Ended {baseName}", row.Path ?? "");
        if (failed > 0) Ui.Error($"{failed} process(es) could not be ended. They may need administrator rights.");
        Refresh();
    }
}
