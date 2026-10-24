using System.Net.Http;
using System.Text.Json;

namespace Kemptly.Core;

public record UpdateInfo(bool IsNewer, string Latest, string Url);

/// <summary>Asks GitHub for the latest Kemptly release. Only runs when the user clicks or has opted in.</summary>
public static class UpdateCheck
{
    public static async Task<UpdateInfo> CheckAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Kemptly/{AppInfo.Version}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var json = await http.GetStringAsync("https://api.github.com/repos/phiil-92/Kemptly/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var url = doc.RootElement.GetProperty("html_url").GetString() ?? AppInfo.RepoUrl + "/releases/latest";
        var latest = tag.TrimStart('v', 'V');
        bool newer = Version.TryParse(latest, out var l) && Version.TryParse(AppInfo.Version, out var c) && l > c;
        return new UpdateInfo(newer, latest, url);
    }
}
