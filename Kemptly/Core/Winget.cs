using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kemptly.Core;

public class WingetUpdate : ObservableObject
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public string Current { get; init; } = "";
    public string Available { get; init; } = "";
    public string Source { get; init; } = "";

    string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    bool _busy;
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) Raise(nameof(CanUpdate)); } }
    public bool CanUpdate => !Busy && Status != "Updated";
}

/// <summary>Finds and installs app updates with winget (built into Windows 10/11). Only runs when the user asks.</summary>
public static class Winget
{
    static readonly Regex SafeId = new(@"^[A-Za-z0-9._+\-]+$");

    /// <summary>
    /// Path to winget.exe. Prefers the real file inside the App Installer package (an admin-protected folder);
    /// the per-user alias in AppData is only used when Kemptly isn't elevated.
    /// </summary>
    public static async Task<string?> FindAsync()
    {
        var json = await Shell.PowerShellJsonAsync(
            "ConvertTo-Json -Compress -InputObject @(Get-AppxPackage Microsoft.DesktopAppInstaller | ForEach-Object { $_.InstallLocation })");
        if (json is JsonElement { ValueKind: JsonValueKind.Array } arr)
            foreach (var e in arr.EnumerateArray())
            {
                var dir = e.GetString();
                if (dir != null && Signature.IsStoreAppPath(dir + "\\x"))
                {
                    var exe = Path.Combine(dir, "winget.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        if (!Admin.IsAdmin)
        {
            var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
            if (File.Exists(alias)) return alias;
        }
        return null;
    }

    const string Common = "--accept-source-agreements --disable-interactivity";

    public static async Task<List<WingetUpdate>> ListAsync(string exe)
    {
        var r = await Shell.RunAsync(exe, $"upgrade {Common}", Encoding.UTF8);
        return Parse(r.StdOut);
    }

    /// <summary>Parses winget's table output. Column headers are translated, so positions come from the "----" line.</summary>
    public static List<WingetUpdate> Parse(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l).ToList();
        int dash = lines.FindIndex(l => l.Length > 10 && l.Trim().All(c => c == '-'));
        var list = new List<WingetUpdate>();
        if (dash < 1) return list;

        var header = lines[dash - 1];
        var starts = new List<int>();
        for (int i = 0; i < header.Length; i++)
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' ')) starts.Add(i);
        if (starts.Count < 4) return list;

        string Col(string line, int c)
        {
            int s = starts[c];
            if (s >= line.Length) return "";
            int e = c + 1 < starts.Count ? Math.Min(starts[c + 1], line.Length) : line.Length;
            return line[s..e].Trim();
        }

        for (int i = dash + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.Length < starts[3]) break; // end of table ("3 upgrades available.")
            var id = Col(line, 1);
            if (!SafeId.IsMatch(id)) continue;
            list.Add(new WingetUpdate
            {
                Name = Col(line, 0), Id = id, Current = Col(line, 2), Available = Col(line, 3),
                Source = starts.Count > 4 ? Col(line, 4) : "",
            });
        }
        return list;
    }

    public static async Task<RunResult> UpgradeAsync(string exe, WingetUpdate u)
    {
        if (!SafeId.IsMatch(u.Id)) throw new ArgumentException("Invalid package id.");
        // Pin the source the update was listed from. Otherwise winget also queries the Microsoft Store catalog,
        // and the whole update fails if that one is unreachable (e.g. blocked by a DNS filter like Pi-hole).
        var source = SafeId.IsMatch(u.Source) ? $" --source \"{u.Source}\"" : "";
        var r = await Shell.RunAsync(exe, $"upgrade --id \"{u.Id}\" --exact{source} --silent --accept-package-agreements {Common}", Encoding.UTF8);
        if (r.Code == 0)
            History.Add(ChangeKind.Other, $"Updated {u.Name}", $"{u.Current} to {u.Available}");
        return r;
    }

    /// <summary>Plain-language text for common winget / installer exit codes.</summary>
    public static string Explain(int code) => unchecked((uint)code) switch
    {
        0x80072EE7 => "Couldn't reach the download server (no internet, or blocked by a DNS filter)",
        0x80072EFD or 0x80072EE2 => "The download server didn't respond, try again later",
        0x8A15002B => "No applicable update for this installation",
        0x8A150101 or 0x8A150111 => "The app (or a part of it, like a plugin or virtual camera) is in use: close it and apps using it, then try again",
        0x8A150102 => "Another installation is in progress, try again in a moment",
        0x8A150103 => "A file is in use: close the app and try again",
        0x8A150104 => "A required component is missing",
        0x8A150105 => "Not enough disk space",
        0x8A150106 => "Not enough memory",
        0x8A150107 => "The installer needs an internet connection",
        0x8A150109 => "Restart Windows to finish the update",
        0x8A15010A => "Restart Windows first, then update",
        0x8A15010C => "Cancelled",
        0x8A15010D => "Already up to date",
        0x8A15010F => "Blocked by a system policy",
        0x8A150010 => "The installer reported an error",
        0x8A150011 => "The installer's checksum didn't match, so it was not run",
        0x8A150014 => "Package not found in the catalog anymore",
        1602 or 0x800704C7 => "Cancelled",
        _ => $"Failed (code {code})",
    };
}
