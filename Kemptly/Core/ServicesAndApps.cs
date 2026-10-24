using System.ServiceProcess;
using Microsoft.Win32;

namespace Kemptly.Core;

public class ServiceItem : ObservableObject
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = "";
    public string Path { get; init; } = "";
    public bool IsMicrosoft { get; init; }

    string _status = "";
    public string Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(IsRunning)); } }
    public bool IsRunning => Status == "Running";

    string _startType = "";
    public string StartType { get => _startType; set => Set(ref _startType, value); }
}

public static class ServiceManager
{
    public static readonly string[] StartTypes = ["Automatic", "Automatic (Delayed)", "Manual", "Disabled"];
    const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";

    public static List<ServiceItem> Load()
    {
        var list = new List<ServiceItem>();
        foreach (var sc in ServiceController.GetServices())
        {
            using (sc)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(ServicesKey + sc.ServiceName);
                    var image = key?.GetValue("ImagePath")?.ToString() ?? "";
                    var desc = key?.GetValue("Description")?.ToString() ?? "";
                    if (desc.StartsWith('@')) desc = "";
                    bool delayed = key?.GetValue("DelayedAutostart") is int d && d == 1;
                    var lower = image.ToLowerInvariant();
                    var start = sc.StartType switch
                    {
                        ServiceStartMode.Automatic => delayed ? "Automatic (Delayed)" : "Automatic",
                        ServiceStartMode.Manual => "Manual",
                        ServiceStartMode.Disabled => "Disabled",
                        ServiceStartMode.Boot => "Boot",
                        ServiceStartMode.System => "System",
                        _ => sc.StartType.ToString(),
                    };
                    list.Add(new ServiceItem
                    {
                        Name = sc.ServiceName,
                        DisplayName = sc.DisplayName,
                        Description = desc,
                        Path = image,
                        IsMicrosoft = lower.Contains(@"\windows\") || lower.Contains("%systemroot%") || lower.StartsWith("system32")
                                      || lower.Contains(@"\windows defender\") || lower.Contains(@"\microsoft\windows defender"),
                        Status = sc.Status.ToString(),
                        StartType = start,
                    });
                }
                catch { }
            }
        }
        return list;
    }

    /// <summary>Changes how a service starts (registry Start value). Needs admin.</summary>
    public static void SetStartType(ServiceItem s, string type)
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServicesKey + s.Name, true)
                        ?? throw new InvalidOperationException("Service not found.");
        int start = type switch { "Automatic" or "Automatic (Delayed)" => 2, "Manual" => 3, "Disabled" => 4, _ => throw new ArgumentException(type) };
        key.SetValue("Start", start, RegistryValueKind.DWord);
        if (start == 2) key.SetValue("DelayedAutostart", type == "Automatic (Delayed)" ? 1 : 0, RegistryValueKind.DWord);
        s.StartType = type;
    }

    public static void StartStop(ServiceItem s, bool start)
    {
        using var sc = new ServiceController(s.Name);
        if (start) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)); }
        else { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
        sc.Refresh();
        s.Status = sc.Status.ToString();
    }
}

public class InstalledApp
{
    public required string Name { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public long Size { get; init; }
    public DateTime? InstallDate { get; init; }
    public string UninstallString { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public string Scope { get; init; } = "";
    public string SizeText => Size > 0 ? Fmt.Bytes(Size) : "";
    public string DateText => InstallDate?.ToString("d") ?? "";
}

public static class InstalledApps
{
    const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    const string Uninstall32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledApp> Load()
    {
        var list = new List<InstalledApp>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        Read(list, hklm, Uninstall, "All users");
        Read(list, hklm, Uninstall32, "All users (32-bit)");
        Read(list, Registry.CurrentUser, Uninstall, "Current user");
        return list
            .GroupBy(a => (a.Name.ToLowerInvariant(), a.Version))
            .Select(g => g.First())
            .OrderBy(a => a.Name)
            .ToList();
    }

    static void Read(List<InstalledApp> list, RegistryKey root, string path, string scope)
    {
        using var key = root.OpenSubKey(path);
        if (key == null) return;
        foreach (var sub in key.GetSubKeyNames())
        {
            try
            {
                using var k = key.OpenSubKey(sub);
                if (k == null) continue;
                var name = k.GetValue("DisplayName")?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (k.GetValue("ParentKeyName") != null) continue; // updates/patches of another product
                var uninstall = k.GetValue("UninstallString")?.ToString() ?? "";
                if (uninstall == "") continue;

                long size = k.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0;
                DateTime? date = null;
                if (DateTime.TryParseExact(k.GetValue("InstallDate")?.ToString(), "yyyyMMdd",
                        null, System.Globalization.DateTimeStyles.None, out var d)) date = d;

                list.Add(new InstalledApp
                {
                    Name = name.Trim(),
                    Publisher = k.GetValue("Publisher")?.ToString()?.Trim() ?? "",
                    Version = k.GetValue("DisplayVersion")?.ToString() ?? "",
                    Size = size,
                    InstallDate = date,
                    UninstallString = uninstall,
                    InstallLocation = k.GetValue("InstallLocation")?.ToString()?.Trim('"') ?? "",
                    Scope = scope,
                });
            }
            catch { }
        }
    }

    /// <summary>Launches the app's own uninstaller.</summary>
    public static void RunUninstaller(InstalledApp app)
    {
        var cmd = app.UninstallString.Trim();
        string file, args;
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            file = cmd[1..end];
            args = cmd[(end + 1)..].Trim();
        }
        else
        {
            int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0) { file = cmd[..(exe + 4)]; args = cmd[(exe + 4)..].Trim(); }
            else { file = cmd; args = ""; }
        }
        // MSI "repair/modify" entries (/I) → uninstall (/X)
        if (Path.GetFileName(file).Equals("MsiExec.exe", StringComparison.OrdinalIgnoreCase))
            args = System.Text.RegularExpressions.Regex.Replace(args, @"/I\s*\{", "/X{", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ExpandEnvironmentVariables(file), args)
        {
            UseShellExecute = true,
        });
    }
}
