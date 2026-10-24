using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kemptly.Core;

public class StoreApp : ObservableObject
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required string FamilyName { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }
    public bool Recommended => Category == SafeCategory;
    public const string SafeCategory = "Safe to remove";

    bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }
}

/// <summary>Preinstalled Store apps that are safe to remove for the current user. Removed apps can be reinstalled from the Store.</summary>
public static class Bloatware
{
    const string Safe = StoreApp.SafeCategory, IfUnused = "Remove if you don't use it";
    static readonly Regex SafeName = new(@"^[A-Za-z0-9._\-~]+$");

    // Package name (prefix match with *), friendly name, category, description. Core apps like the Store,
    // Photos, Calculator, Notepad, Terminal, Xbox sign-in components and Edge are deliberately not listed.
    static readonly (string Pattern, string Name, string Category, string Description)[] Known =
    [
        ("king.com.*", "King games (Candy Crush etc.)", Safe, "Preinstalled games and ads."),
        ("BytedancePte.Ltd.TikTok", "TikTok", Safe, "Preinstalled promo app."),
        ("Facebook.*", "Facebook", Safe, "Preinstalled promo app."),
        ("FACEBOOK.*", "Facebook", Safe, "Preinstalled promo app."),
        ("Instagram*", "Instagram", Safe, "Preinstalled promo app."),
        ("Disney.*", "Disney+", Safe, "Preinstalled promo app."),
        ("AmazonVideo.PrimeVideo", "Prime Video", Safe, "Preinstalled promo app."),
        ("4DF9E0F8.Netflix", "Netflix", IfUnused, "Streaming app."),
        ("7EE7776C.LinkedInforWindows", "LinkedIn", Safe, "Preinstalled promo app."),
        ("SpotifyAB.SpotifyMusic", "Spotify (Store version)", IfUnused, "Music streaming. Your desktop Spotify app is not affected."),
        ("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", Safe, "Games with ads."),
        ("Microsoft.BingNews", "Microsoft News", Safe, "News feed app."),
        ("Microsoft.BingWeather", "Weather", IfUnused, "Weather app."),
        ("Microsoft.BingSearch", "Bing Search", Safe, "Bing search app."),
        ("Microsoft.GetHelp", "Get Help", Safe, "Microsoft support chat app."),
        ("Microsoft.Getstarted", "Tips", Safe, "Windows tips app."),
        ("Microsoft.WindowsFeedbackHub", "Feedback Hub", Safe, "Sends feedback to Microsoft."),
        ("Microsoft.549981C3F5F10", "Cortana", Safe, "Discontinued voice assistant."),
        ("Microsoft.Windows.DevHome", "Dev Home", Safe, "Developer dashboard, discontinued by Microsoft."),
        ("Microsoft.MixedReality.Portal", "Mixed Reality Portal", Safe, "Only needed for VR headsets."),
        ("Microsoft.Microsoft3DViewer", "3D Viewer", Safe, "Discontinued 3D model viewer."),
        ("Microsoft.MSPaint", "Paint 3D", Safe, "Discontinued. Classic Paint is not affected."),
        ("Microsoft.Wallet", "Microsoft Pay", Safe, "Discontinued payment app."),
        ("Microsoft.People", "People", Safe, "Old contacts app."),
        ("Microsoft.SkypeApp", "Skype", Safe, "Discontinued by Microsoft."),
        ("Microsoft.ZuneVideo", "Movies & TV", Safe, "Old video store app."),
        ("Microsoft.ZuneMusic", "Media Player", IfUnused, "Music and video player."),
        ("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office) hub", IfUnused, "Launcher for Office web apps. Installed Office apps are not affected."),
        ("Microsoft.OutlookForWindows", "Outlook (new)", IfUnused, "Mail app."),
        ("Microsoft.Todos", "Microsoft To Do", IfUnused, "To-do list app."),
        ("Microsoft.PowerAutomateDesktop", "Power Automate", IfUnused, "Automation tool."),
        ("Microsoft.WindowsMaps", "Maps", IfUnused, "Offline maps app."),
        ("Microsoft.WindowsAlarms", "Clock", IfUnused, "Alarms, timers and focus sessions."),
        ("Microsoft.WindowsSoundRecorder", "Sound Recorder", IfUnused, "Voice recorder."),
        ("Microsoft.MicrosoftStickyNotes", "Sticky Notes", IfUnused, "Desktop notes."),
        ("Microsoft.YourPhone", "Phone Link", IfUnused, "Connects an Android phone or iPhone to the PC."),
        ("MicrosoftCorporationII.QuickAssist", "Quick Assist", IfUnused, "Remote help tool."),
        ("Microsoft.Copilot", "Copilot", IfUnused, "Microsoft's AI assistant app."),
        ("MSTeams", "Microsoft Teams", IfUnused, "Chat and meetings app."),
        ("MicrosoftTeams", "Microsoft Teams (personal)", IfUnused, "Chat and meetings app."),
        ("Clipchamp.Clipchamp", "Clipchamp", IfUnused, "Video editor."),
        ("Microsoft.GamingApp", "Xbox app", IfUnused, "Xbox games and Game Pass."),
        ("Microsoft.XboxGamingOverlay", "Xbox Game Bar", IfUnused, "In-game overlay and screen recording (Win+G)."),
        ("Microsoft.XboxSpeechToTextOverlay", "Xbox speech-to-text overlay", IfUnused, "Accessibility overlay for Xbox party chat."),
    ];

    static bool Matches(string name, string pattern) =>
        pattern.EndsWith('*') ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                              : name.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    public static async Task<List<StoreApp>> LoadAsync()
    {
        var json = await Shell.PowerShellJsonAsync(
            "ConvertTo-Json -Compress -InputObject @(Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable } | " +
            "ForEach-Object { [pscustomobject]@{ Name = $_.Name; Full = $_.PackageFullName; Family = $_.PackageFamilyName } })");
        var list = new List<StoreApp>();
        if (json is not JsonElement { ValueKind: JsonValueKind.Array } arr) return list;
        foreach (var e in arr.EnumerateArray())
        {
            var name = e.GetProperty("Name").GetString() ?? "";
            var known = Known.FirstOrDefault(k => Matches(name, k.Pattern));
            if (known.Pattern == null) continue;
            list.Add(new StoreApp
            {
                Name = known.Name, FullName = e.GetProperty("Full").GetString() ?? "", FamilyName = e.GetProperty("Family").GetString() ?? "",
                Category = known.Category, Description = known.Description,
            });
        }
        return list.GroupBy(a => a.FullName).Select(g => g.First())
                   .OrderBy(a => a.Recommended ? 0 : 1).ThenBy(a => a.Name).ToList();
    }

    public static async Task<bool> RemoveAsync(StoreApp app)
    {
        if (!SafeName.IsMatch(app.FullName)) return false;
        var r = await Shell.PowerShellAsync($"try {{ Remove-AppxPackage -Package '{app.FullName}' -ErrorAction Stop; 'REMOVED' }} catch {{ 'FAILED: ' + $_ }}");
        if (!r.StdOut.Contains("REMOVED")) return false;
        History.Add(ChangeKind.BloatwareRemoved, $"Removed preinstalled app \"{app.Name}\"", app.FullName,
            History.Data(("familyName", app.FamilyName)));
        return true;
    }
}
