using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kemptly.Core;

public record UpdateAsset(string Url, string Sha256);

public record UpdateInfo(bool IsNewer, string Latest, string Url, UpdateAsset? Msi = null, UpdateAsset? Portable = null);

/// <summary>Asks GitHub for the latest Kemptly release. Only runs when the user clicks or has opted in.</summary>
public static class UpdateCheck
{
    const string DownloadPrefix = "https://github.com/phiil-92/Kemptly/releases/download/";

    static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Kemptly/{AppInfo.Version}");
        return http;
    }

    public static async Task<UpdateInfo> CheckAsync()
    {
        using var http = Client(TimeSpan.FromSeconds(15));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var json = await http.GetStringAsync("https://api.github.com/repos/phiil-92/Kemptly/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var url = doc.RootElement.GetProperty("html_url").GetString() ?? AppInfo.RepoUrl + "/releases/latest";
        var latest = tag.TrimStart('v', 'V');
        bool newer = Version.TryParse(latest, out var l) && Version.TryParse(AppInfo.Version, out var c) && l > c;

        UpdateAsset? msi = null, portable = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var dl = a.GetProperty("browser_download_url").GetString() ?? "";
                var digest = a.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
                // Only Kemptly's own release files, and only with a checksum to verify them against.
                if (!dl.StartsWith(DownloadPrefix, StringComparison.Ordinal) || !digest.StartsWith("sha256:")) continue;
                var asset = new UpdateAsset(dl, digest[7..].ToLowerInvariant());
                if (name == "Kemptly-Setup.msi") msi = asset;
                else if (name == "Kemptly-portable.exe") portable = asset;
            }
        return new UpdateInfo(newer, latest, url, msi, portable);
    }

    static string? InstalledExe
    {
        get
        {
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Kemptly", "Kemptly.exe");
            return string.Equals(Environment.ProcessPath, installed, StringComparison.OrdinalIgnoreCase) ? installed : null;
        }
    }

    /// <summary>True if this copy can update itself (installed via the MSI, or a portable exe in a folder it can write to).</summary>
    public static bool CanInstall(UpdateInfo info) => InstalledExe != null ? info.Msi != null : info.Portable != null;

    /// <summary>
    /// Downloads the update, checks its SHA-256 against the one GitHub published, then hands over to a small
    /// helper that waits for Kemptly to close, installs the update and starts Kemptly again. The caller should
    /// shut the app down right after this returns.
    /// </summary>
    public static async Task InstallAsync(UpdateInfo info, IProgress<string> progress)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell where Kemptly is running from.");
        bool viaMsi = InstalledExe != null;
        var asset = (viaMsi ? info.Msi : info.Portable) ?? throw new InvalidOperationException("This release has no file for this kind of install.");

        var dir = Path.Combine(Path.GetTempPath(), "Kemptly-update");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, viaMsi ? "Kemptly-Setup.msi" : "Kemptly-portable.exe");
        var part = file + ".part";

        using (var http = Client(TimeSpan.FromMinutes(10)))
        using (var resp = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(part);
            var buf = new byte[81920];
            long done = 0;
            int n, lastPct = -1;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                int pct = total > 0 ? (int)(done * 100 / total) : -1;
                if (pct != lastPct) { lastPct = pct; progress.Report(pct >= 0 ? $"Downloading… {pct}%" : $"Downloading… {Fmt.Bytes(done)}"); }
            }
        }

        progress.Report("Checking the download…");
        string hash;
        await using (var fs = File.OpenRead(part))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
        if (hash != asset.Sha256)
        {
            File.Delete(part);
            throw new InvalidOperationException("The downloaded file doesn't match the checksum GitHub published, so it was not installed.");
        }
        File.Move(part, file, true);

        // The helper runs after Kemptly has exited, so the exe can be replaced.
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        int pid = Environment.ProcessId;
        var log = Path.Combine(dir, "install.log");
        var msiexec = Shell.SystemTool("msiexec.exe");
        var script = viaMsi
            ? $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; " +
              $"$p = Start-Process -FilePath {Q(msiexec)} -ArgumentList @('/i', {Q("\"" + file + "\"")}, '/passive', '/norestart', '/l*', {Q("\"" + log + "\"")}) -Wait -PassThru; " +
              // Keep the log only if the install failed, for troubleshooting.
              $"if ($p.ExitCode -eq 0 -or $p.ExitCode -eq 3010) {{ Remove-Item -LiteralPath {Q(log)} -ErrorAction SilentlyContinue }}; " +
              $"Remove-Item -LiteralPath {Q(file)} -ErrorAction SilentlyContinue; " +
              $"Start-Process -FilePath {Q(exe)}"
            : $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500; " +
              $"Copy-Item -LiteralPath {Q(file)} -Destination {Q(exe)} -Force; " +
              $"Remove-Item -LiteralPath {Q(file)} -ErrorAction SilentlyContinue; " +
              $"Start-Process -FilePath {Q(exe)}";
        if (!viaMsi)
        {
            // Make sure the portable exe's folder is writable before closing, instead of failing silently afterwards.
            var test = Path.Combine(Path.GetDirectoryName(exe)!, $".kemptly-write-test-{Guid.NewGuid():N}");
            try { File.WriteAllText(test, ""); File.Delete(test); }
            catch { throw new InvalidOperationException("Kemptly can't write to the folder it's running from. Download the new version from GitHub instead."); }
        }
        Process.Start(new ProcessStartInfo(Shell.SystemTool("powershell.exe"),
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {Shell.EncodePowerShell(script)}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
        });
        progress.Report("Restarting…");
    }
}
