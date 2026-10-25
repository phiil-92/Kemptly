using Microsoft.Win32;

namespace Kemptly.Core;

/// <summary>One registry value. OnValue = the privacy-friendly setting, DefaultValue = Windows' default (null = value absent).</summary>
public record RegValue(RegistryHive Hive, string Path, string Name, int OnValue, int? DefaultValue);

public class PrivacyTweak : ObservableObject
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required RegValue[] Values { get; init; }
    /// <summary>Services to stop when turning this on (their start type is set through Values).</summary>
    public string[] Services { get; init; } = [];
    /// <summary>Scheduled tasks to disable when turning this on (full paths; missing ones are skipped).</summary>
    public string[] Tasks { get; init; } = [];
    public string Note { get; init; } = "";
    public bool HasNote => Note.Length > 0;
    public bool NeedsAdmin => Values.Any(v => v.Hive == RegistryHive.LocalMachine);
    public bool ShowAdminNote => NeedsAdmin && !Admin.IsAdmin;

    bool _applied;
    public bool Applied { get => _applied; set => Set(ref _applied, value); }
}

/// <summary>Reversible Windows privacy settings. Every change is logged in History with the previous values.</summary>
public static class PrivacyTweaks
{
    const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    static RegValue U(string path, string name, int on, int? def) => new(RegistryHive.CurrentUser, path, name, on, def);
    static RegValue M(string path, string name, int on, int? def) => new(RegistryHive.LocalMachine, path, name, on, def);

    public static List<PrivacyTweak> Create() =>
    [
        new() { Id = "adid", Title = "Turn off advertising ID",
            Description = "Apps can't use your advertising ID to show you personalized ads.",
            Values = [U(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1)] },
        new() { Id = "tailored", Title = "Turn off tailored experiences",
            Description = "Microsoft doesn't use your diagnostic data for personalized tips, ads and recommendations.",
            Values = [U(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1)] },
        new() { Id = "websearch", Title = "No web results in Start search",
            Description = "Searching in the Start menu only shows your apps, files and settings, not Bing results.",
            Values = [U(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null),
                      U(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, null)],
            Note = "Takes effect after restarting Explorer (Tools page) or signing out." },
        new() { Id = "suggestions", Title = "No ads and suggestions in Windows",
            Description = "Stops app suggestions in Start, tips in Settings, \"fun facts\" on the lock screen and silently installed promo apps.",
            Values = [U(Cdm, "SubscribedContent-338388Enabled", 0, 1), U(Cdm, "SubscribedContent-338389Enabled", 0, 1),
                      U(Cdm, "SubscribedContent-338393Enabled", 0, 1), U(Cdm, "SubscribedContent-353694Enabled", 0, 1),
                      U(Cdm, "SubscribedContent-353696Enabled", 0, 1), U(Cdm, "SystemPaneSuggestionsEnabled", 0, 1),
                      U(Cdm, "SoftLandingEnabled", 0, 1), U(Cdm, "SilentInstalledAppsEnabled", 0, 1),
                      U(Cdm, "RotatingLockScreenOverlayEnabled", 0, 1), U(Cdm, "SubscribedContent-338387Enabled", 0, 1)] },
        new() { Id = "recommended", Title = "No promoted recommendations in Start",
            Description = "Hides tips, shortcuts and promoted apps in the Recommended section of the Start menu.",
            Values = [U(Adv, "Start_IrisRecommendations", 0, 1)] },
        new() { Id = "setupnag", Title = "No \"finish setting up your PC\" reminders",
            Description = "Stops the full-screen prompts suggesting Microsoft services after updates.",
            Values = [U(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, 1)] },
        new() { Id = "feedback", Title = "No feedback requests",
            Description = "Windows stops asking you to rate it or give feedback.",
            Values = [U(@"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, null)] },
        new() { Id = "recall", Title = "Turn off Recall snapshots",
            Description = "Recall (on Copilot+ PCs) won't save screenshots of what you do.",
            Values = [U(@"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, null)] },
        new() { Id = "copilot", Title = "Turn off the Copilot sidebar",
            Description = "Removes the built-in Copilot sidebar and taskbar button. The separate Copilot app can be removed on the Installed Apps page.",
            Values = [U(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null),
                      U(Adv, "ShowCopilotButton", 0, null)] },
        new() { Id = "activity", Title = "Turn off activity history",
            Description = "Windows stops collecting and uploading a timeline of the apps, files and websites you use.",
            Values = [M(@"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, null),
                      M(@"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, null)] },
        new() { Id = "telemetry", Title = "Send only required diagnostic data",
            Description = "Limits the diagnostic data Windows sends to Microsoft to the required minimum.",
            Values = [M(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1, null)] },
        new() { Id = "telemetryservices", Title = "Turn off telemetry services and tasks",
            Description = "Disables the Connected User Experiences and Telemetry service (and the push service it uses) and the scheduled " +
                          "tasks that collect usage data for Microsoft: Compatibility Appraiser, Customer Experience Program, disk diagnostics and feedback.",
            // Service start types: 4 = disabled; Windows' defaults are 2 (automatic) for DiagTrack and 3 (manual) for dmwappushservice.
            Values = [M(@"SYSTEM\CurrentControlSet\Services\DiagTrack", "Start", 4, 2),
                      M(@"SYSTEM\CurrentControlSet\Services\dmwappushservice", "Start", 4, 3)],
            Services = ["DiagTrack", "dmwappushservice"],
            Tasks = [@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
                     @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser Exp",
                     @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
                     @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
                     @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
                     @"\Microsoft\Windows\Autochk\Proxy",
                     @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
                     @"\Microsoft\Windows\Feedback\Siuf\DmClient",
                     @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload"],
            Note = "Windows Update keeps working. Windows Insider builds need this off. Big Windows updates may turn some tasks back on: check here afterwards." },
    ];

    static RegistryKey Base(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    static int? Read(RegValue v)
    {
        using var root = Base(v.Hive);
        using var key = root.OpenSubKey(v.Path);
        return key?.GetValue(v.Name) is int i ? i : null;
    }

    const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";

    static void Write(RegValue v, int? value)
    {
        // A service's start type goes through the Service Control Manager, which otherwise only rereads it after a restart.
        if (value != null && v.Hive == RegistryHive.LocalMachine && v.Name == "Start"
            && v.Path.StartsWith(ServicesKey, StringComparison.OrdinalIgnoreCase))
        {
            Native.SetServiceStartType(v.Path[ServicesKey.Length..], value.Value);
            return;
        }
        using var root = Base(v.Hive);
        if (value == null)
        {
            using var key = root.OpenSubKey(v.Path, true);
            key?.DeleteValue(v.Name, false);
        }
        else
        {
            using var key = root.CreateSubKey(v.Path, true);
            key.SetValue(v.Name, value.Value, RegistryValueKind.DWord);
        }
    }

    public static bool IsApplied(PrivacyTweak t) =>
        t.Values.All(v => Read(v) == v.OnValue) && t.Tasks.All(p => TaskSchedulerReader.GetEnabled(p) != true);

    /// <summary>Turns the privacy setting on (privacy-friendly) or back to the Windows default. Throws when admin rights are needed.</summary>
    public static void Set(PrivacyTweak t, bool on)
    {
        var data = new Dictionary<string, string> { ["id"] = t.Id };
        for (int i = 0; i < t.Values.Length; i++)
        {
            var prev = Read(t.Values[i]);
            data[$"v{i}"] = prev?.ToString() ?? "absent";
        }
        for (int i = 0; i < t.Tasks.Length; i++)
            data[$"t{i}"] = TaskSchedulerReader.GetEnabled(t.Tasks[i])?.ToString() ?? "absent";

        foreach (var v in t.Values) Write(v, on ? v.OnValue : v.DefaultValue);
        foreach (var task in t.Tasks)
            if (TaskSchedulerReader.GetEnabled(task) != null) TaskSchedulerReader.SetEnabled(task, !on);
        SetServicesRunning(t, !on);
        t.Applied = on;
        History.Add(ChangeKind.PrivacyTweak, $"{(on ? "Applied" : "Reverted")}: {t.Title}", t.Note, data);
    }

    /// <summary>Stops the tweak's services now (on), or starts the ones that aren't disabled (off/undo). Best effort.</summary>
    static void SetServicesRunning(PrivacyTweak t, bool run)
    {
        foreach (var name in t.Services)
        {
            try
            {
                using var sc = new System.ServiceProcess.ServiceController(name);
                if (!run && sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                }
                else if (run && sc.StartType == System.ServiceProcess.ServiceStartMode.Automatic
                         && sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped)
                    sc.Start();
            }
            catch { }
        }
    }

    /// <summary>Restores the exact previous values recorded by Set (used by undo).</summary>
    public static void Restore(Dictionary<string, string> data)
    {
        var t = Create().FirstOrDefault(x => x.Id == data.GetValueOrDefault("id")) ?? throw new InvalidOperationException("Unknown setting.");
        for (int i = 0; i < t.Values.Length; i++)
        {
            var prev = data.GetValueOrDefault($"v{i}", "absent");
            Write(t.Values[i], prev == "absent" ? null : int.Parse(prev));
        }
        for (int i = 0; i < t.Tasks.Length; i++)
        {
            var prev = data.GetValueOrDefault($"t{i}", "absent");
            if (prev != "absent" && TaskSchedulerReader.GetEnabled(t.Tasks[i]) != null)
                TaskSchedulerReader.SetEnabled(t.Tasks[i], prev == "True");
        }
        SetServicesRunning(t, true);
    }
}
