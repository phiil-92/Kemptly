using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kemptly.Core;

namespace Kemptly.Views;

public partial class NetworkPage : UserControl
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly Dictionary<string, string> _dns = new();
    List<ConnRow> _all = [];
    bool _refreshing;

    public NetworkPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); if (AutoCheck.IsChecked == true) _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    async void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var dns = new Dictionary<string, string>(_dns);
            _all = await Task.Run(() => Build(dns));
            ApplyFilter();
            ResolveHosts();
        }
        finally { _refreshing = false; }
    }

    static List<ConnRow> Build(Dictionary<string, string> dns)
    {
        var procs = Network.ProcessNames();
        var rows = new List<ConnRow>();
        foreach (var c in Network.GetTcpConnections())
        {
            if (c.Pid == 0) continue; // closed sockets lingering in TIME_WAIT
            var (name, path) = procs.TryGetValue(c.Pid, out var p) ? p : (c.Pid == 4 ? "System" : "?", null);
            string signed = path == null ? "" :
                Signature.IsWindowsPath(path) ? "Windows" :
                Signature.Check(path) is { Signed: true } s ? s.Publisher ?? "Signed" : "UNSIGNED";
            var remoteIp = c.RemoteAddress.IsIPv4MappedToIPv6 ? c.RemoteAddress.MapToIPv4() : c.RemoteAddress;
            rows.Add(new ConnRow
            {
                Key = $"{c.LocalAddress}:{c.LocalPort}-{c.RemoteAddress}:{c.RemotePort}-{c.Pid}",
                Process = name,
                Pid = c.Pid,
                ProcessPath = path,
                Local = Network.Format(c.LocalAddress, c.LocalPort),
                Remote = c.State == "Listening" ? "—" : Network.Format(c.RemoteAddress, c.RemotePort),
                RemoteIp = remoteIp.ToString(),
                State = c.State,
                IsPublic = c.State != "Listening" && Network.IsPublic(c.RemoteAddress),
                Signed = signed,
                RemoteHost = dns.GetValueOrDefault(remoteIp.ToString(), ""),
            });
        }
        return rows;
    }

    void ApplyFilter()
    {
        var q = _all.AsEnumerable();
        bool listening = ShowListening.IsChecked == true;
        if (OnlyInternet.IsChecked == true) q = q.Where(r => r.IsPublic || (listening && r.State == "Listening"));
        else if (!listening) q = q.Where(r => r.State != "Listening");
        var f = SearchBox.Text.Trim();
        if (f.Length > 0) q = q.Where(r => r.Process.Contains(f, StringComparison.OrdinalIgnoreCase)
                                        || r.Remote.Contains(f) || r.RemoteHost.Contains(f, StringComparison.OrdinalIgnoreCase));
        var list = q.OrderBy(r => r.Signed == "UNSIGNED" ? 0 : 1).ThenBy(r => r.Process).ToList();
        ConnGrid.ItemsSource = list;
        int unsigned = list.Where(r => r.Signed == "UNSIGNED").Select(r => r.Pid).Distinct().Count();
        StatusText.Text = $"{list.Count} connections shown · {_all.Count(r => r.IsPublic)} to the internet" +
                          (unsigned > 0 ? $" · {unsigned} unsigned program(s) — check them" : "");
    }

    /// <summary>Reverse-DNS lookups for public IPs, done once per IP in the background.</summary>
    void ResolveHosts()
    {
        foreach (var row in _all.Where(r => r.IsPublic))
        {
            if (_dns.ContainsKey(row.RemoteIp)) { row.RemoteHost = _dns[row.RemoteIp]; continue; }
            var ip = row.RemoteIp;
            _dns[ip] = "";
            _ = Task.Run(async () =>
            {
                try
                {
                    var lookup = Dns.GetHostEntryAsync(ip);
                    if (await Task.WhenAny(lookup, Task.Delay(3000)) == lookup) return lookup.Result.HostName;
                }
                catch { }
                return "";
            }).ContinueWith(t =>
            {
                _dns[ip] = t.Result;
                foreach (var r in _all.Where(r => r.RemoteIp == ip)) r.RemoteHost = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();
    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (AutoCheck.IsChecked == true) _timer.Start(); else _timer.Stop();
    }

    void Show_Click(object sender, RoutedEventArgs e)
    {
        var row = Ui.Item<ConnRow>(sender);
        if (row?.ProcessPath != null) Shell.ShowInExplorer(row.ProcessPath);
        else Ui.Info("The location of this process isn't accessible.");
    }

    void Kill_Click(object sender, RoutedEventArgs e)
    {
        var row = Ui.Item<ConnRow>(sender);
        if (row == null) return;
        if (ProcessSampler.IsCritical(row.Process) || row.Pid <= 4)
        {
            Ui.Error($"{row.Process} is a critical Windows process and can't be ended safely.");
            return;
        }
        if (!Ui.Confirm($"End \"{row.Process}\" (PID {row.Pid})? Unsaved work will be lost.")) return;
        try { using var p = Process.GetProcessById(row.Pid); p.Kill(true); }
        catch (Exception ex) { Ui.Error(ex.Message); }
        Refresh();
    }
}
