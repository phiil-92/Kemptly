using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Kemptly.Core;

public record BlockedApp(string RuleName, string Program)
{
    public string AppName => Path.GetFileName(Program);
}

/// <summary>Blocks individual programs from the internet with outbound Windows Firewall rules (reversible).</summary>
public static class Firewall
{
    const string Prefix = "Kemptly block - ";

    public static string RuleName(string exePath)
    {
        var name = Regex.Replace(Path.GetFileNameWithoutExtension(exePath), @"[^A-Za-z0-9 ._()-]", "_");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exePath.ToLowerInvariant())))[..6];
        return $"{Prefix}{name} ({hash})";
    }

    static bool SafeArg(string s) => !s.Contains('"') && !s.Contains('\n') && !s.Contains('\r');

    public static async Task<bool> BlockAsync(string exePath)
    {
        if (!File.Exists(exePath) || !SafeArg(exePath)) return false;
        var rule = RuleName(exePath);
        int code = await Shell.RunElevatedAsync("netsh.exe",
            $"advfirewall firewall add rule name=\"{rule}\" dir=out action=block program=\"{exePath}\" enable=yes profile=any", false);
        if (code != 0) return false;
        History.Add(ChangeKind.FirewallBlock, $"Blocked internet access for {Path.GetFileName(exePath)}", exePath,
            History.Data(("rule", rule), ("app", Path.GetFileName(exePath))));
        return true;
    }

    public static async Task<bool> UnblockAsync(string ruleName)
    {
        if (!ruleName.StartsWith(Prefix) || !SafeArg(ruleName)) return false;
        return await Shell.RunElevatedAsync("netsh.exe", $"advfirewall firewall delete rule name=\"{ruleName}\"", false) == 0;
    }

    /// <summary>Rules created by Kemptly, read from the firewall's registry store (readable without admin).</summary>
    public static List<BlockedApp> ListBlocked()
    {
        var list = new List<BlockedApp>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules");
            if (key == null) return list;
            foreach (var valueName in key.GetValueNames())
            {
                if (key.GetValue(valueName) is not string rule || !rule.Contains("|Name=" + Prefix)) continue;
                var parts = rule.Split('|').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                                .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
                if (parts.TryGetValue("Name", out var name) && parts.TryGetValue("App", out var app))
                    list.Add(new BlockedApp(name, app));
            }
        }
        catch { }
        return list.GroupBy(b => b.RuleName).Select(g => g.First()).OrderBy(b => b.AppName).ToList();
    }
}
