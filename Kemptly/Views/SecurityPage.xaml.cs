using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Kemptly.Core;

namespace Kemptly.Views;

public partial class SecurityPage : UserControl
{
    static readonly string MpCmdRun = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");

    readonly ObservableCollection<Finding> _findings = new();
    bool _busy;

    public SecurityPage()
    {
        InitializeComponent();
        FindingList.ItemsSource = _findings;
    }

    void SetBusy(bool busy, string text = "")
    {
        _busy = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanBtn.IsEnabled = QuickScanBtn.IsEnabled = FullScanBtn.IsEnabled = UpdateBtn.IsEnabled = !busy;
        ProgressText.Text = text;
    }

    public async void StartScan()
    {
        if (_busy) return;
        SetBusy(true, "Starting…");
        try
        {
            var progress = new Progress<string>(s => ProgressText.Text = s);
            var result = await SecurityScanner.RunAsync(progress);

            var st = result.Status;
            AvText.Text = st.Antivirus;
            DefText.Text = st.Definitions;
            FwText.Text = st.Firewall;
            UacText.Text = st.Uac;
            Colour(AvText, st.AntivirusOk);
            Colour(DefText, st.DefinitionsOk);
            Colour(FwText, st.FirewallOk);
            Colour(UacText, st.UacOk);

            _findings.Clear();
            foreach (var f in result.Findings) _findings.Add(f);
            ShowList();

            int high = result.Findings.Count(f => f.Severity == Severity.High);
            int med = result.Findings.Count(f => f.Severity == Severity.Medium);
            SetBusy(false, high + med == 0
                ? $"Done — nothing serious found ({result.Findings.Count} informational notes)."
                : $"Done — {high} high and {med} medium priority item(s) to review.");
        }
        catch (Exception ex)
        {
            SetBusy(false, "Scan failed: " + ex.Message);
        }
    }

    void ShowList()
    {
        bool any = _findings.Count > 0;
        FindingList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any)
        {
            EmptyTitle.Text = "All clear";
            EmptyText.Text = "No suspicious items were found. Keep Windows and your apps updated, and run a Defender scan now and then.";
        }
    }

    void Colour(TextBlock tb, bool? ok)
    {
        tb.SetResourceReference(TextBlock.ForegroundProperty, ok switch
        {
            true => "SystemFillColorSuccessBrush",
            false => "SystemFillColorCriticalBrush",
            _ => "TextFillColorPrimaryBrush",
        });
    }

    void Scan_Click(object sender, RoutedEventArgs e) => StartScan();

    async Task RunDefender(string args, string label)
    {
        if (!File.Exists(MpCmdRun)) { Ui.Error("Microsoft Defender's command-line tool wasn't found (another antivirus may be installed)."); return; }
        SetBusy(true, $"{label}… this can take a while. You can keep using Kemptly.");
        try
        {
            var r = await Shell.RunAsync(MpCmdRun, args);
            var lines = r.All.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var summary = string.Join("\n", lines.TakeLast(8));
            SetBusy(false, $"{label} finished (exit code {r.Code}).");
            if (r.Code == 2) Ui.Error($"{label} found threats! Open Windows Security → Protection history to handle them.\n\n{summary}");
            else Ui.Info($"{label} finished.\n\n{summary}");
        }
        catch (Exception ex) { SetBusy(false, ex.Message); }
    }

    async void QuickScan_Click(object sender, RoutedEventArgs e) => await RunDefender("-Scan -ScanType 1", "Defender quick scan");

    async void FullScan_Click(object sender, RoutedEventArgs e)
    {
        if (Ui.Ask("A full scan checks every file and can take an hour or more. Start it?", "Full scan"))
            await RunDefender("-Scan -ScanType 2", "Defender full scan");
    }

    async void Update_Click(object sender, RoutedEventArgs e) => await RunDefender("-SignatureUpdate", "Definition update");

    void OpenWinSec_Click(object sender, RoutedEventArgs e) => Shell.Open("windowsdefender:");

    void Show_Click(object sender, RoutedEventArgs e) => Shell.ShowInExplorer(Ui.Item<Finding>(sender)?.Path);

    void Kill_Click(object sender, RoutedEventArgs e)
    {
        var f = Ui.Item<Finding>(sender);
        if (f?.Pid is not int pid) return;
        if (!Ui.Confirm($"End this process (PID {pid})?\n\n{f.Path}")) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(true);
            Ui.Info("Process ended. If it was malware, also disable its startup entry and run a Defender scan.");
        }
        catch (ArgumentException) { Ui.Info("The process has already exited."); }
        catch (Exception ex) { Ui.Error(ex.Message); }
    }

    void DisableStartup_Click(object sender, RoutedEventArgs e)
    {
        var f = Ui.Item<Finding>(sender);
        if (f?.Startup == null) return;
        try
        {
            StartupManager.SetEnabled(f.Startup, false);
            Ui.Info($"\"{f.Startup.DisplayName}\" will no longer start with Windows. You can re-enable it on the Startup page.");
            Refresh(f);
        }
        catch (Exception ex)
        {
            if (Admin.IsAccessDenied(ex)) Admin.Require("Disabling this all-users startup entry");
            else Ui.Error(ex.Message);
        }
    }

    async void DisableTask_Click(object sender, RoutedEventArgs e)
    {
        var f = Ui.Item<Finding>(sender);
        if (f?.Task == null) return;
        try
        {
            await Task.Run(() => TaskSchedulerReader.SetEnabled(f.Task, false));
            Ui.Info($"Task \"{f.Task.Name}\" disabled. You can re-enable it on the Startup page.");
            Refresh(f);
        }
        catch (Exception ex)
        {
            if (Admin.IsAccessDenied(ex)) Admin.Require("Disabling this scheduled task");
            else Ui.Error(ex.Message);
        }
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var f = Ui.Item<Finding>(sender);
        if (f?.Path == null) return;
        if (!Ui.Confirm($"Move this file to the Recycle Bin?\n\n{f.Path}")) return;
        if (Shell.Recycle(f.Path)) { _findings.Remove(f); ShowList(); }
        else Ui.Error("The file could not be deleted (it may be in use or protected).");
    }

    async void ScanFile_Click(object sender, RoutedEventArgs e)
    {
        var f = Ui.Item<Finding>(sender);
        if (f?.Path == null) return;
        await RunDefender($"-Scan -ScanType 3 -File \"{f.Path}\" -DisableRemediation", "Defender file scan");
    }

    void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        var f = Ui.Item<Finding>(sender);
        if (f == null) return;
        _findings.Remove(f);
        ShowList();
    }

    // Re-insert so the button visibility bindings re-evaluate.
    void Refresh(Finding f)
    {
        int i = _findings.IndexOf(f);
        if (i < 0) return;
        _findings.RemoveAt(i);
        _findings.Insert(i, f);
    }
}
