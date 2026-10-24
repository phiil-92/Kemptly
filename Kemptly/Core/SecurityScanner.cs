using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Kemptly.Core;

public enum Severity { High, Medium, Low, Info }

public class Finding
{
    public required Severity Severity { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string? Path { get; init; }
    public int? Pid { get; init; }
    public StartupItem? Startup { get; init; }
    public TaskItem? Task { get; init; }
    public bool CanDeleteFile { get; init; }

    public string SeverityText => Severity.ToString();
    public bool HasPath => !string.IsNullOrEmpty(Path);
    public bool HasPid => Pid is > 4;
    public bool CanDisableStartup => Startup is { Enabled: true };
    public bool CanDisableTask => Task is { Enabled: true };
}

public class SecurityStatus
{
    public string Antivirus { get; set; } = "Unknown";
    public bool? AntivirusOk { get; set; }
    public string Firewall { get; set; } = "Unknown";
    public bool? FirewallOk { get; set; }
    public string Uac { get; set; } = "Unknown";
    public bool? UacOk { get; set; }
    public string Definitions { get; set; } = "Unknown";
    public bool? DefinitionsOk { get; set; }
}

/// <summary>
/// Heuristic checks for common signs of malware, persistence tricks and weakened security settings.
/// This is not an antivirus - it points out things worth a closer look.
/// </summary>
public static class SecurityScanner
{
    static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static readonly string Temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static readonly string ProgData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string Public = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";
    static readonly string Downloads = Native.GetDownloadsFolder();

    static bool Under(string path, string dir) =>
        path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns a reason if the file sits somewhere legit software rarely runs from.</summary>
    public static string? SuspiciousLocation(string path)
    {
        var dir = (Path.GetDirectoryName(path) ?? "").TrimEnd('\\');
        if (Under(path, Temp) || Under(path, Path.Combine(Win, "Temp"))) return "runs from a Temp folder";
        if (Under(path, Downloads)) return "runs directly from the Downloads folder";
        if (Under(path, Public)) return "runs from the shared Public user folder";
        if (path.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase)) return "runs from inside the Recycle Bin";
        foreach (var root in new[] { Roaming, Local, ProgData })
            if (dir.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return $"sits directly in {root} (a common malware hiding spot)";
        return null;
    }

    static bool IsUserWritable(string path) =>
        (Under(path, UserProfile) || Under(path, ProgData) || Under(path, Public))
        && !Under(path, Path.Combine(Local, "Microsoft", "WindowsApps"));

    static readonly (Regex Pattern, Severity Severity, string Reason)[] CommandPatterns =
    [
        (new(@"powershell(\.exe)?.*\s-(e|ec|en|enc|encodedcommand)\s+[A-Za-z0-9+/=]{16,}", RegexOptions.IgnoreCase), Severity.High, "runs an encoded (hidden) PowerShell command"),
        (new(@"powershell(\.exe)?.*(downloadstring|downloadfile|invoke-webrequest|iwr\s|frombase64string|iex\s*\(|invoke-expression)", RegexOptions.IgnoreCase), Severity.High, "PowerShell that downloads or executes code"),
        (new(@"powershell(\.exe)?.*-w(indowstyle)?\s+hid", RegexOptions.IgnoreCase), Severity.Medium, "starts a hidden PowerShell window"),
        (new(@"mshta(\.exe)?\s+.*(http|javascript:|vbscript:)", RegexOptions.IgnoreCase), Severity.High, "uses mshta to run remote/script code"),
        (new(@"regsvr32(\.exe)?.*(/i:http|scrobj)", RegexOptions.IgnoreCase), Severity.High, "regsvr32 script-loading trick"),
        (new(@"rundll32(\.exe)?\s+.*\\(appdata|temp|users\\public|programdata)\\", RegexOptions.IgnoreCase), Severity.High, "rundll32 loads a DLL from a user-writable folder"),
        (new(@"rundll32(\.exe)?\s+.*javascript:", RegexOptions.IgnoreCase), Severity.High, "rundll32 executing script"),
        (new(@"certutil(\.exe)?.*-urlcache", RegexOptions.IgnoreCase), Severity.High, "certutil used to download files"),
        (new(@"bitsadmin(\.exe)?.*/transfer", RegexOptions.IgnoreCase), Severity.High, "bitsadmin used to download files"),
        (new(@"(wscript|cscript)(\.exe)?.*\\(appdata|temp|users\\public|programdata)\\.*\.(vbs|vbe|js|jse|wsf)", RegexOptions.IgnoreCase), Severity.High, "runs a script from a user-writable folder"),
        (new(@"cmd(\.exe)?\s+/c\s+.*(curl|powershell|bitsadmin|certutil)", RegexOptions.IgnoreCase), Severity.Medium, "cmd chain that launches download tools"),
    ];

    static (Severity Severity, string Reason)? CheckCommand(string command)
    {
        foreach (var (pattern, sev, reason) in CommandPatterns)
            if (pattern.IsMatch(command)) return (sev, reason);
        return null;
    }

    public record ScanResult(List<Finding> Findings, SecurityStatus Status);

    public static async Task<ScanResult> RunAsync(IProgress<string> progress)
    {
        var findings = new List<Finding>();
        progress.Report("Checking Windows security settings…");
        var status = await CheckSecuritySettingsAsync(findings);

        await System.Threading.Tasks.Task.Run(() =>
        {
            Step(findings, progress, "running processes", CheckProcesses);
            Step(findings, progress, "startup entries", CheckStartup);
            Step(findings, progress, "scheduled tasks", CheckTasks);
            Step(findings, progress, "system hijack points", CheckHijacks);
            Step(findings, progress, "hosts file and proxy", CheckHostsAndProxy);
            Step(findings, progress, "network connections", CheckNetwork);
            Step(findings, progress, "disguised files", CheckDisguisedFiles);
        });

        var ordered = findings
            .GroupBy(f => (f.Title, f.Path)).Select(g => g.First())
            .OrderBy(f => f.Severity).ThenBy(f => f.Category).ToList();
        return new ScanResult(ordered, status);
    }

    static void Step(List<Finding> findings, IProgress<string> progress, string what, Action<List<Finding>> check)
    {
        progress.Report($"Checking {what}…");
        try { check(findings); }
        catch (Exception ex)
        {
            findings.Add(new Finding { Severity = Severity.Info, Category = "Scanner", Title = $"Could not check {what}", Detail = ex.Message });
        }
    }

    // ---------------- Security settings ----------------

    const string StatusScript = """
        $s = Get-MpComputerStatus -ErrorAction SilentlyContinue
        $p = Get-MpPreference -ErrorAction SilentlyContinue
        $fw = @(Get-NetFirewallProfile -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{ Name = "$($_.Name)"; Enabled = "$($_.Enabled)" } })
        $av = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue | ForEach-Object { "$($_.displayName)" })
        [pscustomobject]@{
          HasDefender = [bool]$s
          AV = if ($s) { [bool]$s.AntivirusEnabled } else { $false }
          RTP = if ($s) { [bool]$s.RealTimeProtectionEnabled } else { $false }
          SigAge = if ($s) { [int]$s.AntivirusSignatureAge } else { -1 }
          ExPath = @($p.ExclusionPath | Where-Object { $_ })
          ExProc = @($p.ExclusionProcess | Where-Object { $_ })
          ExExt = @($p.ExclusionExtension | Where-Object { $_ })
          Firewall = $fw
          AVProducts = $av
        } | ConvertTo-Json -Depth 4 -Compress
        """;

    static async Task<SecurityStatus> CheckSecuritySettingsAsync(List<Finding> findings)
    {
        var st = new SecurityStatus();
        var json = await Shell.PowerShellJsonAsync(StatusScript);
        if (json is JsonElement j)
        {
            var others = Strings(j, "AVProducts").Where(p => !p.Contains("Defender", StringComparison.OrdinalIgnoreCase)).ToList();
            bool hasDefender = Bool(j, "HasDefender"), rtp = Bool(j, "RTP");

            if (hasDefender && rtp) { st.Antivirus = "Microsoft Defender · real-time on"; st.AntivirusOk = true; }
            else if (others.Count > 0) { st.Antivirus = string.Join(", ", others); st.AntivirusOk = true; }
            else
            {
                st.Antivirus = "Real-time protection OFF";
                st.AntivirusOk = false;
                findings.Add(new Finding
                {
                    Severity = Severity.High, Category = "Protection",
                    Title = "Antivirus real-time protection is turned off",
                    Detail = "No active antivirus was found. Turn on real-time protection in Windows Security. Malware often switches it off.",
                });
            }

            int sigAge = j.TryGetProperty("SigAge", out var sa) && sa.ValueKind == JsonValueKind.Number ? sa.GetInt32() : -1;
            if (hasDefender && others.Count == 0 && sigAge >= 0)
            {
                st.Definitions = sigAge == 0 ? "Up to date (today)" : $"{sigAge} day(s) old";
                st.DefinitionsOk = sigAge <= 7;
                if (sigAge > 7)
                    findings.Add(new Finding
                    {
                        Severity = Severity.Medium, Category = "Protection",
                        Title = $"Virus definitions are {sigAge} days old",
                        Detail = "Use 'Update definitions' above or run Windows Update.",
                    });
            }
            else if (others.Count > 0) { st.Definitions = "Managed by " + others[0]; st.DefinitionsOk = true; }

            // Non-admins get "N/A: Must be an administrator to view exclusions" instead of the real list.
            static bool Real(string e) => !e.StartsWith("N/A", StringComparison.OrdinalIgnoreCase);
            var exclusions = Strings(j, "ExPath").Where(Real)
                .Concat(Strings(j, "ExProc").Where(Real))
                .Concat(Strings(j, "ExExt").Where(Real).Select(e => "*" + e)).ToList();
            if (exclusions.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.Medium, Category = "Protection",
                    Title = $"Microsoft Defender skips {exclusions.Count} location(s)",
                    Detail = "Malware likes to add itself to the exclusion list. Make sure you recognise these: " + string.Join(", ", exclusions.Take(10)),
                });

            if (j.TryGetProperty("Firewall", out var fw) && fw.ValueKind == JsonValueKind.Array && fw.GetArrayLength() > 0)
            {
                var off = fw.EnumerateArray()
                    .Where(p => !string.Equals(p.GetProperty("Enabled").GetString(), "True", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.GetProperty("Name").GetString()).ToList();
                st.Firewall = off.Count == 0 ? "On for all networks" : "OFF for: " + string.Join(", ", off);
                st.FirewallOk = off.Count == 0;
                if (off.Count > 0)
                    findings.Add(new Finding
                    {
                        Severity = Severity.High, Category = "Protection",
                        Title = "Windows Firewall is turned off for " + string.Join(", ", off),
                        Detail = "Turn it back on in Windows Security → Firewall & network protection (unless another firewall product is managing it).",
                    });
            }
        }

        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
        {
            bool lua = k?.GetValue("EnableLUA") is not int l || l != 0;
            int consent = k?.GetValue("ConsentPromptBehaviorAdmin") is int c ? c : 5;
            st.UacOk = lua && consent != 0;
            st.Uac = !lua ? "Disabled" : consent == 0 ? "Never notifies" : "On";
            if (!lua)
                findings.Add(new Finding { Severity = Severity.High, Category = "Protection", Title = "User Account Control (UAC) is disabled", Detail = "Every program runs with full admin rights. Re-enable UAC in Control Panel → User Accounts." });
            else if (consent == 0)
                findings.Add(new Finding { Severity = Severity.Medium, Category = "Protection", Title = "UAC elevates programs without asking", Detail = "Set UAC back to the default level in Control Panel → User Accounts → Change User Account Control settings." });
        }

        using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server"))
        {
            if (k?.GetValue("fDenyTSConnections") is int deny && deny == 0)
                findings.Add(new Finding { Severity = Severity.Info, Category = "Protection", Title = "Remote Desktop is enabled", Detail = "Fine if you use it. Otherwise turn it off in Settings → System → Remote Desktop." });
        }
        return st;
    }

    static bool Bool(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static List<string> Strings(JsonElement j, string name)
    {
        if (!j.TryGetProperty(name, out var v)) return [];
        if (v.ValueKind == JsonValueKind.String) return [v.GetString()!];
        if (v.ValueKind != JsonValueKind.Array) return [];
        return v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
    }

    // ---------------- Processes ----------------

    static readonly HashSet<string> SystemProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost", "csrss", "lsass", "winlogon", "services", "smss", "wininit", "spoolsv", "taskhostw",
        "dllhost", "conhost", "rundll32", "explorer", "dwm", "sihost", "ctfmon", "lsm", "wuauclt", "searchindexer",
    };

    static void CheckProcesses(List<Finding> findings)
    {
        var sys32 = Environment.SystemDirectory;
        var wow = Path.Combine(Win, "SysWOW64");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Environment.ProcessPath ?? "" };

        foreach (var p in new ProcessSampler().Sample())
        {
            if (p.Path == null || !seen.Add(p.Path)) continue;
            var dir = (Path.GetDirectoryName(p.Path) ?? "").TrimEnd('\\');

            if (SystemProcessNames.Contains(p.Name)
                && !dir.Equals(sys32, StringComparison.OrdinalIgnoreCase)
                && !dir.Equals(wow, StringComparison.OrdinalIgnoreCase)
                && !dir.Equals(Win, StringComparison.OrdinalIgnoreCase)
                && !Under(p.Path, Path.Combine(Win, "WinSxS")))
            {
                findings.Add(new Finding
                {
                    Severity = Severity.High, Category = "Processes",
                    Title = $"\"{p.Name}\" pretends to be a Windows process",
                    Detail = $"The real {p.Name}.exe lives in C:\\Windows\\System32, but this one runs from {dir}. This is a classic malware disguise.",
                    Path = p.Path, Pid = p.Pid,
                });
                continue;
            }

            if (Signature.IsWindowsPath(p.Path) || Signature.IsStoreAppPath(p.Path)) continue;
            var signed = Signature.Check(p.Path).Signed;
            var loc = SuspiciousLocation(p.Path);
            if (loc != null)
            {
                findings.Add(new Finding
                {
                    Severity = signed ? Severity.Low : Severity.High, Category = "Processes",
                    Title = $"{(signed ? "" : "Unsigned ")}program {loc}: {p.Name}",
                    Detail = signed
                        ? $"Signed by {Signature.Check(p.Path).Publisher}. Installers often run from here - fine if you just started one."
                        : "Unsigned programs running from this location are a common sign of malware. If you don't recognise it, end it and scan with Defender.",
                    Path = p.Path, Pid = p.Pid,
                });
            }
            else if (!signed && IsUserWritable(p.Path))
            {
                findings.Add(new Finding
                {
                    Severity = Severity.Medium, Category = "Processes",
                    Title = $"Unsigned program running from your user folder: {p.Name}",
                    Detail = "Many small tools are unsigned, but so is most malware. Check that you know what this is.",
                    Path = p.Path, Pid = p.Pid,
                });
            }
        }
    }

    // ---------------- Startup & tasks ----------------

    static void CheckStartup(List<Finding> findings)
    {
        foreach (var item in StartupManager.Load())
        {
            var cmdHit = CheckCommand(item.Command);
            if (cmdHit is { } hit)
            {
                findings.Add(new Finding
                {
                    Severity = hit.Severity, Category = "Startup",
                    Title = $"Startup entry \"{item.DisplayName}\" {hit.Reason}",
                    Detail = "Command: " + item.Command, Path = item.FilePath, Startup = item,
                });
                continue;
            }
            if (!item.FileExists)
            {
                findings.Add(new Finding
                {
                    Severity = Severity.Low, Category = "Startup",
                    Title = $"Broken startup entry: \"{item.DisplayName}\"",
                    Detail = "It points to a file that no longer exists (leftover from an uninstalled app). Safe to remove on the Startup page. Command: " + item.Command,
                    Startup = item,
                });
                continue;
            }
            if (Signature.IsWindowsPath(item.FilePath!)) continue;
            bool signed = Signature.Check(item.FilePath!).Signed;
            var loc = SuspiciousLocation(item.FilePath!);
            if (loc != null)
                findings.Add(new Finding
                {
                    Severity = signed ? Severity.Medium : Severity.High, Category = "Startup",
                    Title = $"Startup entry \"{item.DisplayName}\" {loc}",
                    Detail = "Command: " + item.Command, Path = item.FilePath, Startup = item,
                });
            else if (!signed && IsUserWritable(item.FilePath!))
                findings.Add(new Finding
                {
                    Severity = Severity.Medium, Category = "Startup",
                    Title = $"Unsigned program starts with Windows: \"{item.DisplayName}\"",
                    Detail = "Command: " + item.Command, Path = item.FilePath, Startup = item,
                });
        }
    }

    static void CheckTasks(List<Finding> findings)
    {
        foreach (var t in TaskSchedulerReader.Load())
        {
            if (t.ExePath == null) continue;
            var full = t.Action;
            if (CheckCommand(full) is { } hit)
            {
                findings.Add(new Finding
                {
                    Severity = hit.Severity, Category = "Scheduled tasks",
                    Title = $"Scheduled task \"{t.Name}\" {hit.Reason}",
                    Detail = $"{t.FullPath} · runs: {t.Triggers}\nAction: {full}", Path = t.ExePath, Task = t,
                });
                continue;
            }
            if (Signature.IsWindowsPath(t.ExePath) || !File.Exists(t.ExePath)) continue;
            var loc = SuspiciousLocation(t.ExePath);
            bool signed = Signature.Check(t.ExePath).Signed;
            if (loc != null)
                findings.Add(new Finding
                {
                    Severity = signed ? Severity.Medium : Severity.High, Category = "Scheduled tasks",
                    Title = $"Scheduled task \"{t.Name}\" {loc}",
                    Detail = $"{t.FullPath} · runs: {t.Triggers}\nAction: {full}", Path = t.ExePath, Task = t,
                });
            else if (!signed && IsUserWritable(t.ExePath) && t.Enabled)
                findings.Add(new Finding
                {
                    Severity = t.IsMicrosoft ? Severity.High : Severity.Medium, Category = "Scheduled tasks",
                    Title = t.IsMicrosoft
                        ? $"Task in the Microsoft folder runs an unsigned user program: \"{t.Name}\""
                        : $"Scheduled task runs an unsigned program: \"{t.Name}\"",
                    Detail = $"{t.FullPath} · runs: {t.Triggers}\nAction: {full}", Path = t.ExePath, Task = t,
                });
        }
    }

    // ---------------- Registry hijacks ----------------

    static void CheckHijacks(List<Finding> findings)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        using (var ifeo = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options"))
        {
            if (ifeo != null)
                foreach (var name in ifeo.GetSubKeyNames())
                {
                    string? dbg;
                    try
                    {
                        using var k = ifeo.OpenSubKey(name);
                        dbg = k?.GetValue("Debugger") as string;
                    }
                    catch { continue; } // some subkeys are admin-only
                    if (dbg != null && dbg.Trim().Length > 0)
                        findings.Add(new Finding
                        {
                            Severity = name.Equals("taskmgr.exe", StringComparison.OrdinalIgnoreCase) && dbg.Contains("procexp", StringComparison.OrdinalIgnoreCase)
                                ? Severity.Info : Severity.High,
                            Category = "System hijacks",
                            Title = $"Launching {name} is redirected to another program",
                            Detail = $"Image File Execution Options 'Debugger' = {dbg}. Malware uses this to block security tools or to start itself. (Legit if you set up Process Explorer to replace Task Manager.)",
                            Path = Shell.ExtractExePath(dbg),
                        });
                }
        }

        using (var wl = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
        {
            var shell = (wl?.GetValue("Shell") as string ?? "explorer.exe").Trim();
            if (!shell.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
                && !shell.Equals(Path.Combine(Win, "explorer.exe"), StringComparison.OrdinalIgnoreCase))
                findings.Add(new Finding { Severity = Severity.High, Category = "System hijacks", Title = "The Windows shell has been replaced", Detail = $"Winlogon Shell = \"{shell}\" (should be explorer.exe).", Path = Shell.ExtractExePath(shell) });

            var userinit = (wl?.GetValue("Userinit") as string ?? "").Trim().TrimEnd(',').Trim();
            var expected = Path.Combine(Environment.SystemDirectory, "userinit.exe");
            if (userinit.Length > 0 && !userinit.Equals(expected, StringComparison.OrdinalIgnoreCase)
                && !userinit.Equals("userinit.exe", StringComparison.OrdinalIgnoreCase))
                findings.Add(new Finding { Severity = Severity.High, Category = "System hijacks", Title = "Something extra runs at every sign-in (Userinit)", Detail = $"Winlogon Userinit = \"{userinit}\" (should only be {expected})." });
        }

        using (var wl = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Winlogon"))
        {
            if (wl?.GetValue("Shell") is string s && s.Trim().Length > 0)
                findings.Add(new Finding { Severity = Severity.High, Category = "System hijacks", Title = "Your account's Windows shell has been replaced", Detail = $"HKCU Winlogon Shell = \"{s}\".", Path = Shell.ExtractExePath(s) });
        }

        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows" })
        {
            using var k = hklm.OpenSubKey(path);
            if (k?.GetValue("AppInit_DLLs") is string dlls && dlls.Trim().Length > 0 && k.GetValue("LoadAppInit_DLLs") is int load && load == 1)
                findings.Add(new Finding { Severity = Severity.High, Category = "System hijacks", Title = "DLLs are injected into every program (AppInit_DLLs)", Detail = dlls });
        }
    }

    // ---------------- Hosts & proxy ----------------

    static readonly Regex SensitiveHost = new(
        @"(microsoft|windowsupdate|windows\.com|live\.com|google|bing|apple|paypal|amazon|facebook|bank|defender|avast|avg|kaspersky|norton|symantec|mcafee|eset|malwarebytes|bitdefender|virustotal|sophos|trendmicro)",
        RegexOptions.IgnoreCase);

    static void CheckHostsAndProxy(List<Finding> findings)
    {
        var hosts = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        if (File.Exists(hosts))
        {
            var custom = new List<string>();
            foreach (var raw in File.ReadAllLines(hosts))
            {
                var line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var ip = parts[0];
                foreach (var host in parts.Skip(1))
                {
                    if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) continue;
                    bool blocking = ip is "0.0.0.0" or "127.0.0.1" or "::1" or "::";
                    if (SensitiveHost.IsMatch(host))
                        findings.Add(new Finding
                        {
                            Severity = blocking ? Severity.Medium : Severity.High, Category = "Network",
                            Title = blocking ? $"Hosts file blocks {host}" : $"Hosts file redirects {host} to {ip}",
                            Detail = blocking
                                ? "Malware sometimes blocks security/update sites this way. Fine if you added it yourself (e.g. a privacy blocklist)."
                                : "Redirecting well-known sites is a phishing technique. Remove the line unless you added it on purpose.",
                            Path = hosts,
                        });
                    else custom.Add($"{host} → {ip}");
                }
            }
            if (custom.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.Info, Category = "Network",
                    Title = $"Hosts file has {custom.Count} custom entr{(custom.Count == 1 ? "y" : "ies")}",
                    Detail = string.Join(", ", custom.Take(8)) + (custom.Count > 8 ? ", …" : ""),
                    Path = hosts,
                });
        }

        using var inet = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (inet?.GetValue("ProxyEnable") is int pe && pe == 1)
            findings.Add(new Finding
            {
                Severity = Severity.Medium, Category = "Network",
                Title = "A proxy server is configured for your internet traffic",
                Detail = $"Proxy: {inet.GetValue("ProxyServer")}. Fine for work/school networks; otherwise it could be intercepting your traffic. Check Settings → Network → Proxy.",
            });
        if (inet?.GetValue("AutoConfigURL") is string pac && pac.Length > 0)
            findings.Add(new Finding
            {
                Severity = Severity.Medium, Category = "Network",
                Title = "An automatic proxy script (PAC) is configured",
                Detail = $"Script: {pac}. Malware/adware uses this to redirect browsing. Check Settings → Network → Proxy.",
            });
    }

    // ---------------- Network ----------------

    static void CheckNetwork(List<Finding> findings)
    {
        var procs = Network.ProcessNames();
        var byPid = Network.GetTcpConnections()
            .Where(c => c.State == "Established" && Network.IsPublic(c.RemoteAddress))
            .GroupBy(c => c.Pid);
        foreach (var g in byPid)
        {
            if (!procs.TryGetValue(g.Key, out var p) || p.Path == null) continue;
            if (Signature.IsTrusted(p.Path)) continue;
            var remotes = g.Select(c => Network.Format(c.RemoteAddress, c.RemotePort)).Distinct().Take(5);
            findings.Add(new Finding
            {
                Severity = Severity.Medium, Category = "Network",
                Title = $"Unsigned program is connected to the internet: {p.Name}",
                Detail = "Connected to " + string.Join(", ", remotes) + ". Make sure you know what this program is.",
                Path = p.Path, Pid = g.Key,
            });
        }
    }

    // ---------------- Files ----------------

    static readonly Regex DoubleExtension = new(
        @"\.(pdf|docx?|xlsx?|pptx?|jpe?g|png|gif|bmp|txt|rtf|mp3|mp4|avi|mov|zip|rar|7z)\s*\.(exe|scr|com|bat|cmd|pif|vbs|vbe|js|jse|hta|msi|ps1|wsf|lnk|cpl)$",
        RegexOptions.IgnoreCase);

    static void CheckDisguisedFiles(List<Finding> findings)
    {
        var dirs = new[]
        {
            Downloads,
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
            foreach (var f in FileWalker.Walk(dir, maxDepth: 3))
            {
                if (DoubleExtension.IsMatch(f.Name))
                    findings.Add(new Finding
                    {
                        Severity = Severity.High, Category = "Files",
                        Title = $"Disguised program: {f.Name}",
                        Detail = "This looks like a document or picture but is actually a program. Don't open it - delete it.",
                        Path = f.FullName, CanDeleteFile = true,
                    });
                else if (f.Name.Contains('\u202E'))
                    findings.Add(new Finding
                    {
                        Severity = Severity.High, Category = "Files",
                        Title = "File name uses a hidden right-to-left trick",
                        Detail = "The name is reversed with an invisible character to hide its real extension. Delete it unless you know what it is.",
                        Path = f.FullName, CanDeleteFile = true,
                    });
            }
    }
}
