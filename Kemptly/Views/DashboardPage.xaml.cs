using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kemptly.Core;

namespace Kemptly.Views;

public record DriveRow(string Title, string Detail, double Percent)
{
    public string PercentText => $"{Percent:0}% used";
}

public record DiskRow(string Name, string Detail, string Health);

public partial class DashboardPage : UserControl
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public DashboardPage()
    {
        InitializeComponent();
        OsText.Text = $"{SystemInfo.OsName}   ·   {Environment.MachineName}   ·   {Environment.UserName}";
        CpuName.Text = $"{SystemInfo.CpuName} · {Environment.ProcessorCount} threads";
        CpuName.ToolTip = CpuName.Text;
        var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        UptimeDetail.Text = $"Since {boot:g}";

        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => { SystemInfo.CpuUsage(); Tick(); LoadDrives(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        _ = LoadDisksAsync();
        _ = LoadStartupCountAsync();
    }

    void Tick()
    {
        var cpu = SystemInfo.CpuUsage();
        CpuValue.Text = $"{cpu:0}%";
        CpuBar.Value = cpu;

        var (total, avail, load) = SystemInfo.Memory();
        MemValue.Text = $"{load}%";
        MemBar.Value = load;
        MemDetail.Text = $"{Fmt.Bytes(total - avail)} of {Fmt.Bytes(total)} in use";

        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        UptimeValue.Text = up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h" : $"{up.Hours}h {up.Minutes}m";

        var procs = Process.GetProcesses();
        ProcValue.Text = procs.Length.ToString();
        foreach (var p in procs) p.Dispose();
    }

    void LoadDrives()
    {
        DrivesList.ItemsSource = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d =>
            {
                long used = d.TotalSize - d.TotalFreeSpace;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
                return new DriveRow($"{label} ({d.Name.TrimEnd('\\')})",
                    $"{Fmt.Bytes(d.TotalFreeSpace)} free of {Fmt.Bytes(d.TotalSize)} · {d.DriveFormat}",
                    d.TotalSize == 0 ? 0 : used * 100.0 / d.TotalSize);
            }).ToList();
    }

    async Task LoadDisksAsync()
    {
        const string script = """
            ConvertTo-Json -Compress -InputObject @(Get-PhysicalDisk | ForEach-Object {
              [pscustomobject]@{ Name = "$($_.FriendlyName)"; Media = "$($_.MediaType)"; Bus = "$($_.BusType)"; Health = "$($_.HealthStatus)"; Size = [int64]$_.Size }
            })
            """;
        var json = await Shell.PowerShellJsonAsync(script);
        if (json is not JsonElement { ValueKind: JsonValueKind.Array } arr)
        {
            DisksStatus.Text = "Disk health information isn't available.";
            return;
        }
        var rows = arr.EnumerateArray().Select(d =>
        {
            string S(string n) => d.TryGetProperty(n, out var v) ? v.ToString() : "";
            long size = d.TryGetProperty("Size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
            var media = S("Media") is "Unspecified" or "" ? "" : S("Media") + " · ";
            var health = S("Health");
            return new DiskRow(S("Name"), $"{media}{S("Bus")} · {Fmt.Bytes(size)}",
                health == "Healthy" ? "✔ Healthy" : $"⚠ {health}");
        }).ToList();
        DisksList.ItemsSource = rows;
        DisksStatus.Text = rows.Any(r => !r.Health.Contains("Healthy"))
            ? "A disk reports a problem — back up your data soon!"
            : "Health status reported by the drives themselves (S.M.A.R.T.).";
    }

    async Task LoadStartupCountAsync()
    {
        try
        {
            var items = await Task.Run(StartupManager.Load);
            StartupDetail.Text = $"{items.Count(i => i.Enabled)} apps launch at sign-in";
        }
        catch { }
    }

    void QuickClean_Click(object sender, RoutedEventArgs e) =>
        ((CleanerPage)MainWindow.Instance.Navigate("cleaner")).StartScan();

    void QuickSecurity_Click(object sender, RoutedEventArgs e) =>
        ((SecurityPage)MainWindow.Instance.Navigate("security")).StartScan();

    void QuickDisk_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.Navigate("disk");
    void QuickStartup_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.Navigate("startup");
}
