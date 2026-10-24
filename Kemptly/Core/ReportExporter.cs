using System.Net;
using System.Text;
using System.Text.Json;

namespace Kemptly.Core;

/// <summary>Builds a self-contained HTML summary of the PC, e.g. to send to someone helping remotely.</summary>
public static class ReportExporter
{
    static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static async Task<string> BuildAsync(IProgress<string> progress)
    {
        var sb = new StringBuilder();
        void Row(params string[] cells) => sb.Append("<tr>").Append(string.Concat(cells.Select(c => $"<td>{E(c)}</td>"))).Append("</tr>");
        void Table(string[] head, Action rows)
        {
            sb.Append("<table><tr>").Append(string.Concat(head.Select(h => $"<th>{E(h)}</th>"))).Append("</tr>");
            rows();
            sb.Append("</table>");
        }

        sb.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Kemptly PC report</title><style>
            body{font-family:"Segoe UI",system-ui,sans-serif;max-width:960px;margin:32px auto;padding:0 16px;color:#1b2333;background:#f7f9fc}
            h1{margin:0 0 4px}h2{margin:32px 0 10px;font-size:20px}.muted{color:#5b6b82}
            table{width:100%;border-collapse:collapse;background:#fff;border:1px solid #dfe6ef;border-radius:8px;overflow:hidden}
            th,td{text-align:left;padding:8px 10px;border-bottom:1px solid #eef2f7;font-size:14px;vertical-align:top}th{background:#eef3f9}
            .note{background:#fff8e1;border:1px solid #f3dd8b;padding:10px 14px;border-radius:8px;font-size:14px}
            .High{color:#c42b1c;font-weight:600}.Medium{color:#ca5010;font-weight:600}.Low{color:#8e7400}.Info{color:#0067c0}
            </style></head><body>
            """);
        sb.Append($"<h1>Kemptly PC report</h1><p class=\"muted\">Created {E(DateTime.Now.ToString("f"))} with Kemptly {E(AppInfo.Version)}</p>");
        sb.Append("<p class=\"note\">This report contains information about this PC, such as program names and file paths. Only share it with people you trust.</p>");

        progress.Report("Collecting system information…");
        var (total, avail, load) = SystemInfo.Memory();
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        sb.Append("<h2>System</h2>");
        Table(["Item", "Value"], () =>
        {
            Row("Windows", SystemInfo.OsName);
            Row("Computer", Environment.MachineName);
            Row("Processor", $"{SystemInfo.CpuName} ({Environment.ProcessorCount} threads)");
            Row("Memory", $"{Fmt.Bytes(total - avail)} of {Fmt.Bytes(total)} in use ({load}%)");
            Row("Uptime", $"{(int)up.TotalDays} days {up.Hours} h");
        });

        sb.Append("<h2>Drives</h2>");
        Table(["Drive", "Free", "Size", "Used"], () =>
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
                Row($"{d.Name} {d.VolumeLabel}", Fmt.Bytes(d.TotalFreeSpace), Fmt.Bytes(d.TotalSize),
                    d.TotalSize == 0 ? "" : $"{(d.TotalSize - d.TotalFreeSpace) * 100 / d.TotalSize}%");
        });

        progress.Report("Reading disk health…");
        var disks = await Shell.PowerShellJsonAsync("ConvertTo-Json -Compress -InputObject @(Get-PhysicalDisk | ForEach-Object { [pscustomobject]@{ Name = \"$($_.FriendlyName)\"; Media = \"$($_.MediaType)\"; Health = \"$($_.HealthStatus)\"; Size = [int64]$_.Size } })");
        if (disks is JsonElement { ValueKind: JsonValueKind.Array } da)
        {
            sb.Append("<h2>Disk health</h2>");
            Table(["Disk", "Type", "Size", "Health"], () =>
            {
                foreach (var d in da.EnumerateArray())
                    Row(d.GetProperty("Name").ToString(), d.GetProperty("Media").ToString(),
                        Fmt.Bytes(d.GetProperty("Size").ValueKind == JsonValueKind.Number ? d.GetProperty("Size").GetInt64() : 0), d.GetProperty("Health").ToString());
            });
        }

        progress.Report("Measuring junk files…");
        var cats = Cleaner.CreateCategories();
        var sizes = await Task.Run(() => cats.Select(c => (c.Name, Cleaner.Scan(c, CancellationToken.None).Size)).Where(x => x.Size > 0).ToList());
        sb.Append($"<h2>Junk files ({E(Fmt.Bytes(sizes.Sum(s => s.Size)))} removable)</h2>");
        Table(["Category", "Size"], () => { foreach (var (n, s) in sizes.OrderByDescending(s => s.Size)) Row(n, Fmt.Bytes(s)); });

        progress.Report("Listing startup apps…");
        var startup = await Task.Run(StartupManager.Load);
        sb.Append($"<h2>Startup apps ({startup.Count(s => s.Enabled)} of {startup.Count} enabled)</h2>");
        Table(["App", "Publisher", "Status"], () => { foreach (var s in startup.OrderByDescending(s => s.Enabled)) Row(s.DisplayName, s.Publisher, s.StateText); });

        progress.Report("Running the security check…");
        var scan = await SecurityScanner.RunAsync(progress);
        sb.Append("<h2>Security</h2>");
        Table(["Check", "Status"], () =>
        {
            Row("Antivirus", scan.Status.Antivirus);
            Row("Virus definitions", scan.Status.Definitions);
            Row("Firewall", scan.Status.Firewall);
            Row("User Account Control", scan.Status.Uac);
        });
        if (scan.Findings.Count > 0)
        {
            sb.Append("<p></p>");
            sb.Append("<table><tr><th>Priority</th><th>Finding</th><th>Details</th></tr>");
            foreach (var f in scan.Findings)
                sb.Append($"<tr><td class=\"{f.SeverityText}\">{E(f.SeverityText)}</td><td>{E(f.Title)}</td><td>{E(f.Detail)}{(f.HasPath ? "<br><span class=\"muted\">" + E(f.Path) + "</span>" : "")}</td></tr>");
            sb.Append("</table>");
        }
        else sb.Append("<p>No suspicious items found.</p>");

        progress.Report("Reading crash history…");
        var crashes = await Task.Run(() => CrashHistory.Load(30));
        sb.Append($"<h2>Crashes in the last 30 days ({crashes.Count})</h2>");
        if (crashes.Count == 0) sb.Append("<p>None recorded.</p>");
        else Table(["When", "Type", "What happened"], () =>
        {
            foreach (var c in crashes.Take(40)) Row(c.TimeText, c.KindText, $"{c.Title}. {c.Detail}".Trim());
        });

        sb.Append("<p class=\"muted\" style=\"margin-top:32px\">Generated locally by Kemptly. Nothing was sent anywhere.</p></body></html>");
        return sb.ToString();
    }
}
