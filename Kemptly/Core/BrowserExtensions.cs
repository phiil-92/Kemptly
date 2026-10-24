using System.Text.Json;

namespace Kemptly.Core;

public class BrowserExtension
{
    public required string Browser { get; init; }
    public required string Profile { get; init; }
    public required string Name { get; init; }
    public required string Id { get; init; }
    public string Version { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool FromStore { get; init; } = true;
    public string Folder { get; init; } = "";
    public Severity Risk { get; init; }
    public string RiskText => Risk switch { Severity.High => "High", Severity.Medium => "Medium", _ => "Low" };
    public string SeverityText => RiskText; // used by the shared severity pill style
    public string Reasons { get; init; } = "";
    public string StateText => Enabled ? "On" : "Off";
}

/// <summary>Reads installed extensions of Chromium browsers and Firefox from disk and rates their permissions.</summary>
public static class BrowserExtensions
{
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    static readonly string[] AllSitesPatterns = ["<all_urls>", "*://*/*", "http://*/*", "https://*/*"];
    static readonly string[] OfficialUpdateUrls =
        ["https://clients2.google.com/service/update2/crx", "https://edge.microsoft.com/extensionwebstorebase/v1/crx"];

    // Sensitive permission -> plain-language reason
    static readonly Dictionary<string, string> Sensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cookies"] = "can read your cookies (logins)",
        ["webRequest"] = "can watch your web traffic",
        ["webRequestBlocking"] = "can change your web traffic",
        ["debugger"] = "can control the browser like a debugger",
        ["nativeMessaging"] = "can talk to programs on your PC",
        ["proxy"] = "can route your traffic through a proxy",
        ["history"] = "can read your browsing history",
        ["management"] = "can manage your other extensions",
        ["clipboardRead"] = "can read your clipboard",
        ["desktopCapture"] = "can record your screen",
        ["downloads"] = "can manage your downloads",
    };

    public static List<BrowserExtension> Load()
    {
        var list = new List<BrowserExtension>();
        var chromium = new (string Browser, string Dir, bool ProfileIsRoot)[]
        {
            ("Chrome", Path.Combine(Local, @"Google\Chrome\User Data"), false),
            ("Edge", Path.Combine(Local, @"Microsoft\Edge\User Data"), false),
            ("Brave", Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"), false),
            ("Vivaldi", Path.Combine(Local, @"Vivaldi\User Data"), false),
            ("Helium", Path.Combine(Local, @"imput\Helium\User Data"), false),
            ("Opera", Path.Combine(Roaming, @"Opera Software\Opera Stable"), true),
            ("Opera GX", Path.Combine(Roaming, @"Opera Software\Opera GX Stable"), true),
        };
        foreach (var (browser, dir, root) in chromium)
        {
            if (!Directory.Exists(dir)) continue;
            var profiles = root ? [dir] : SafeDirs(dir).Where(p =>
            {
                var n = Path.GetFileName(p);
                return n == "Default" || n.StartsWith("Profile ");
            });
            foreach (var p in profiles)
                try { list.AddRange(ReadChromiumProfile(browser, p)); } catch { }
        }
        try { list.AddRange(ReadFirefox()); } catch { }
        return list.OrderBy(e => e.Risk).ThenBy(e => e.Browser).ThenBy(e => e.Name).ToList();
    }

    static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.GetDirectories(path); } catch { return []; }
    }

    static JsonElement? ReadJson(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    static IEnumerable<string> Strings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) yield break;
        foreach (var x in v.EnumerateArray())
            if (x.ValueKind == JsonValueKind.String) yield return x.GetString()!;
    }

    static IEnumerable<BrowserExtension> ReadChromiumProfile(string browser, string profileDir)
    {
        var extDir = Path.Combine(profileDir, "Extensions");
        if (!Directory.Exists(extDir)) yield break;

        // Enabled state and install source live in (Secure) Preferences.
        var settings = new Dictionary<string, JsonElement>();
        foreach (var prefFile in new[] { "Secure Preferences", "Preferences" })
            if (ReadJson(Path.Combine(profileDir, prefFile)) is JsonElement pref
                && pref.TryGetProperty("extensions", out var ex) && ex.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object)
                foreach (var kv in s.EnumerateObject()) settings.TryAdd(kv.Name, kv.Value);

        foreach (var idDir in SafeDirs(extDir))
        {
            var id = Path.GetFileName(idDir);
            if (id == "Temp") continue;
            var verDir = SafeDirs(idDir).OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
            if (verDir == null || ReadJson(Path.Combine(verDir, "manifest.json")) is not JsonElement m) continue;

            settings.TryGetValue(id, out var st);
            bool enabled = st.ValueKind != JsonValueKind.Object || !st.TryGetProperty("state", out var state) || state.GetInt32() != 0;
            bool fromStorePref = st.ValueKind == JsonValueKind.Object && st.TryGetProperty("from_webstore", out var fw) && fw.ValueKind == JsonValueKind.True;
            var updateUrl = m.TryGetProperty("update_url", out var uu) ? uu.GetString() ?? "" : "";
            bool fromStore = fromStorePref || OfficialUpdateUrls.Any(u => updateUrl.StartsWith(u, StringComparison.OrdinalIgnoreCase));

            var perms = Strings(m, "permissions").Concat(Strings(m, "host_permissions")).ToList();
            if (m.TryGetProperty("content_scripts", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray()) perms.AddRange(Strings(c, "matches"));

            var name = LocalizedName(m, verDir);
            var (risk, reasons) = Rate(perms, fromStore);
            yield return new BrowserExtension
            {
                Browser = browser, Profile = Path.GetFileName(profileDir), Name = name, Id = id,
                Version = m.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                Enabled = enabled, FromStore = fromStore, Folder = verDir, Risk = risk, Reasons = reasons,
            };
        }
    }

    static string LocalizedName(JsonElement m, string dir)
    {
        var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (!name.StartsWith("__MSG_") || !name.EndsWith("__")) return name;
        var key = name[6..^2];
        var locale = m.TryGetProperty("default_locale", out var dl) ? dl.GetString() ?? "en" : "en";
        foreach (var loc in new[] { locale, "en", "en_US" })
            if (ReadJson(Path.Combine(dir, "_locales", loc, "messages.json")) is JsonElement msgs)
                foreach (var kv in msgs.EnumerateObject())
                    if (kv.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value.TryGetProperty("message", out var msg))
                        return msg.GetString() ?? name;
        return name;
    }

    static (Severity, string) Rate(List<string> perms, bool fromStore)
    {
        bool allSites = perms.Any(p => AllSitesPatterns.Contains(p));
        var sensitive = perms.Where(Sensitive.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var reasons = new List<string>();
        if (allSites) reasons.Add("can read and change all websites you visit");
        reasons.AddRange(sensitive.Select(s => Sensitive[s]));
        if (!fromStore) reasons.Add("not installed from an official store");

        bool dangerous = sensitive.Any(s => s is "debugger" or "nativeMessaging" or "proxy" or "cookies" or "webRequestBlocking");
        var risk = (allSites && dangerous) || (!fromStore && allSites) ? Severity.High
                 : allSites || !fromStore || sensitive.Count >= 2 ? Severity.Medium
                 : Severity.Low;
        var text = reasons.Count == 0 ? "No sensitive permissions" : char.ToUpper(reasons[0][0]) + string.Join("; ", reasons)[1..];
        return (risk, text);
    }

    static IEnumerable<BrowserExtension> ReadFirefox()
    {
        var profiles = Path.Combine(Roaming, @"Mozilla\Firefox\Profiles");
        foreach (var p in SafeDirs(profiles))
        {
            if (ReadJson(Path.Combine(p, "extensions.json")) is not JsonElement doc || !doc.TryGetProperty("addons", out var addons)) continue;
            foreach (var a in addons.EnumerateArray())
            {
                if (a.TryGetProperty("type", out var t) && t.GetString() != "extension") continue;
                if (a.TryGetProperty("location", out var loc) && loc.GetString() != "app-profile") continue; // skip Firefox's built-ins
                var perms = new List<string>();
                if (a.TryGetProperty("userPermissions", out var up) && up.ValueKind == JsonValueKind.Object)
                {
                    perms.AddRange(Strings(up, "permissions"));
                    perms.AddRange(Strings(up, "origins"));
                }
                var name = a.TryGetProperty("defaultLocale", out var dl) && dl.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                var (risk, reasons) = Rate(perms, fromStore: true); // Firefox only runs Mozilla-signed extensions
                yield return new BrowserExtension
                {
                    Browser = "Firefox", Profile = Path.GetFileName(p), Name = name, Id = a.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Version = a.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                    Enabled = !a.TryGetProperty("active", out var act) || act.ValueKind == JsonValueKind.True,
                    Folder = Path.Combine(p, "extensions"), Risk = risk, Reasons = reasons,
                };
            }
        }
    }
}
