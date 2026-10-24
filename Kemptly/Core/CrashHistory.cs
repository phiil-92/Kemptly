using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;

namespace Kemptly.Core;

public enum CrashKind { BlueScreen, UnexpectedShutdown, AppCrash, AppHang }

public class CrashEvent
{
    public required DateTime Time { get; init; }
    public required CrashKind Kind { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string TimeText => Time.ToString("g");
    public string KindText => Kind switch
    {
        CrashKind.BlueScreen => "Blue screen",
        CrashKind.UnexpectedShutdown => "Unexpected shutdown",
        CrashKind.AppCrash => "App crash",
        _ => "App froze",
    };
    public string SeverityText => Kind is CrashKind.BlueScreen or CrashKind.UnexpectedShutdown ? "High" : "Low";
}

/// <summary>Reads blue screens, unexpected shutdowns and app crashes from the Windows event logs and explains them.</summary>
public static class CrashHistory
{
    static readonly Dictionary<string, string> BugChecks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0x0000000a"] = "IRQL_NOT_LESS_OR_EQUAL: usually a faulty driver.",
        ["0x0000001a"] = "MEMORY_MANAGEMENT: often faulty RAM or a driver. Run Windows Memory Diagnostic.",
        ["0x0000001e"] = "KMODE_EXCEPTION_NOT_HANDLED: usually a faulty driver.",
        ["0x0000003b"] = "SYSTEM_SERVICE_EXCEPTION: usually a driver or system file problem.",
        ["0x00000050"] = "PAGE_FAULT_IN_NONPAGED_AREA: often faulty RAM, a driver or antivirus software.",
        ["0x0000007e"] = "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED: usually a faulty driver.",
        ["0x000000d1"] = "DRIVER_IRQL_NOT_LESS_OR_EQUAL: a driver problem, often network or graphics.",
        ["0x000000ef"] = "CRITICAL_PROCESS_DIED: a core Windows process stopped. Try the SFC/DISM repairs on the Tools page.",
        ["0x00000101"] = "CLOCK_WATCHDOG_TIMEOUT: often CPU overclocking, overheating or BIOS problems.",
        ["0x00000116"] = "VIDEO_TDR_FAILURE: the graphics driver stopped responding. Update or reinstall it.",
        ["0x00000124"] = "WHEA_UNCORRECTABLE_ERROR: a hardware error (CPU, RAM, overheating or overclocking).",
        ["0x00000133"] = "DPC_WATCHDOG_VIOLATION: often a storage (SSD) driver or firmware problem.",
        ["0x00000139"] = "KERNEL_SECURITY_CHECK_FAILURE: usually a driver or corrupted system files.",
    };

    public static List<CrashEvent> Load(int days = 30)
    {
        var list = new List<CrashEvent>();
        long ms = days * 24L * 3600 * 1000;
        string Recent(string inner) => $"*[System[{inner} and TimeCreated[timediff(@SystemTime) <= {ms}]]]";

        Query("System", Recent("Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and (EventID=1001)"), r =>
        {
            var msg = Describe(r);
            var code = Regex.Match(msg, @"0x[0-9a-fA-F]{8}").Value;
            var hint = BugChecks.GetValueOrDefault(code, "Windows stopped because of a serious error, often a driver or hardware problem.");
            list.Add(new CrashEvent { Time = r.TimeCreated ?? DateTime.MinValue, Kind = CrashKind.BlueScreen,
                Title = code.Length > 0 ? $"Blue screen ({code})" : "Blue screen", Detail = hint });
        });
        Query("System", Recent("Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41)"), r =>
            list.Add(new CrashEvent { Time = r.TimeCreated ?? DateTime.MinValue, Kind = CrashKind.UnexpectedShutdown,
                Title = "PC turned off unexpectedly",
                Detail = "Windows didn't shut down properly: power loss, holding the power button, a freeze, overheating or the power supply." }));
        Query("Application", Recent("Provider[@Name='Application Error'] and (EventID=1000)"), r =>
        {
            var app = Prop(r, 0);
            var module = Prop(r, 3);
            list.Add(new CrashEvent { Time = r.TimeCreated ?? DateTime.MinValue, Kind = CrashKind.AppCrash,
                Title = $"{(app.Length > 0 ? app : "An app")} crashed",
                Detail = module.Length > 0 && !module.Equals(app, StringComparison.OrdinalIgnoreCase) ? $"Faulting module: {module}" : "" });
        });
        Query("Application", Recent("Provider[@Name='Application Hang'] and (EventID=1002)"), r =>
        {
            var app = Prop(r, 0);
            list.Add(new CrashEvent { Time = r.TimeCreated ?? DateTime.MinValue, Kind = CrashKind.AppHang,
                Title = $"{(app.Length > 0 ? app : "An app")} stopped responding", Detail = "It was closed after freezing." });
        });
        return list.OrderByDescending(e => e.Time).ToList();
    }

    static void Query(string log, string xpath, Action<EventRecord> add)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true });
            int n = 0;
            for (var r = reader.ReadEvent(); r != null && n < 300; r = reader.ReadEvent(), n++)
                using (r) { try { add(r); } catch { } }
        }
        catch { }
    }

    static string Prop(EventRecord r, int i) =>
        r.Properties.Count > i ? r.Properties[i].Value?.ToString()?.Trim() ?? "" : "";

    static string Describe(EventRecord r)
    {
        try { return r.FormatDescription() ?? string.Join(" ", r.Properties.Select(p => p.Value)); }
        catch { return string.Join(" ", r.Properties.Select(p => p.Value)); }
    }
}
