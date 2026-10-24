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
    ];

    static RegistryKey Base(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    static int? Read(RegValue v)
    {
        using var root = Base(v.Hive);
        using var key = root.OpenSubKey(v.Path);
        return key?.GetValue(v.Name) is int i ? i : null;
    }

    static void Write(RegValue v, int? value)
    {
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

    public static bool IsApplied(PrivacyTweak t) => t.Values.All(v => Read(v) == v.OnValue);

    /// <summary>Turns the privacy setting on (privacy-friendly) or back to the Windows default. Throws when admin rights are needed.</summary>
    public static void Set(PrivacyTweak t, bool on)
    {
        var data = new Dictionary<string, string> { ["id"] = t.Id };
        for (int i = 0; i < t.Values.Length; i++)
        {
            var prev = Read(t.Values[i]);
            data[$"v{i}"] = prev?.ToString() ?? "absent";
        }
        foreach (var v in t.Values) Write(v, on ? v.OnValue : v.DefaultValue);
        t.Applied = on;
        History.Add(ChangeKind.PrivacyTweak, $"{(on ? "Applied" : "Reverted")}: {t.Title}", t.Note, data);
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
    }
}
