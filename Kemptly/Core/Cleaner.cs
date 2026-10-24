using System.Runtime.InteropServices;

namespace Kemptly.Core;

public record CleanTarget(string Path, string Pattern = "*", bool Recursive = true, int MinAgeDays = 0,
    bool RemoveEmptyDirs = true, bool IsFile = false);

public class CleanCategory : ObservableObject
{
    public CleanCategory(string name, string description, bool requiresAdmin, bool selected,
        Func<IEnumerable<CleanTarget>> targets, bool isRecycleBin = false)
    {
        Name = name;
        Description = description;
        RequiresAdmin = requiresAdmin;
        _selected = selected;
        Targets = targets;
        IsRecycleBin = isRecycleBin;
    }

    public string Name { get; }
    public string Description { get; }
    public bool RequiresAdmin { get; }
    public Func<IEnumerable<CleanTarget>> Targets { get; }
    public bool IsRecycleBin { get; }
    public bool ShowAdminNote => RequiresAdmin && !Admin.IsAdmin;

    bool _selected;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

    long _size;
    public long Size { get => _size; set { if (Set(ref _size, value)) Raise(nameof(SizeText)); } }

    bool _scanned;
    public bool Scanned { get => _scanned; set { if (Set(ref _scanned, value)) Raise(nameof(SizeText)); } }

    public string SizeText => Scanned ? Fmt.Bytes(Size) : "–";

    int _files;
    public int Files { get => _files; set => Set(ref _files, value); }

    string _status = "Not scanned";
    public string Status { get => _status; set => Set(ref _status, value); }

    public string? FirstExistingFolder =>
        Targets().Select(t => t.IsFile ? System.IO.Path.GetDirectoryName(t.Path) : t.Path)
                 .FirstOrDefault(p => p != null && Directory.Exists(p));
}

public static class Cleaner
{
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static string ProgData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static CleanTarget Dir(params string[] parts) => new(Path.Combine(parts));

    public static List<CleanCategory> CreateCategories() =>
    [
        new("Temporary files (your account)",
            "Leftovers apps write to your Temp folder. Only files older than 24 hours are removed.",
            false, true, () => [new CleanTarget(Path.GetTempPath(), MinAgeDays: 1)]),

        new("Temporary files (Windows)",
            "System-wide temp folder. Only files older than 24 hours are removed.",
            true, true, () => [new CleanTarget(Path.Combine(Win, "Temp"), MinAgeDays: 1)]),

        new("Windows Update leftovers",
            "Downloaded update packages that are already installed. Windows re-downloads anything it still needs.",
            true, true, () => [Dir(Win, "SoftwareDistribution", "Download")]),

        new("Delivery Optimization cache",
            "Update pieces Windows keeps to share with other PCs.",
            true, true, () =>
            [
                Dir(Win, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
                Dir(Win, "SoftwareDistribution", "DeliveryOptimization"),
            ]),

        new("Crash dumps & error reports",
            "Memory dumps and Windows Error Reporting files from past crashes.",
            false, true, () =>
            [
                Dir(Local, "CrashDumps"),
                Dir(Local, "Microsoft", "Windows", "WER"),
                Dir(ProgData, "Microsoft", "Windows", "WER", "ReportArchive"),
                Dir(ProgData, "Microsoft", "Windows", "WER", "ReportQueue"),
                Dir(Win, "Minidump"),
                Dir(Win, "LiveKernelReports"),
                new CleanTarget(Path.Combine(Win, "MEMORY.DMP"), IsFile: true),
            ]),

        new("Thumbnail & icon cache",
            "Explorer picture previews. Rebuilt automatically (some files stay locked while Explorer runs).",
            false, false, () =>
            [
                new CleanTarget(Path.Combine(Local, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db", false),
                new CleanTarget(Path.Combine(Local, "Microsoft", "Windows", "Explorer"), "iconcache_*.db", false),
            ]),

        new("GPU shader caches",
            "DirectX / NVIDIA / AMD / Intel shader caches. Games may stutter briefly while they rebuild.",
            false, true, () =>
            [
                Dir(Local, "D3DSCache"),
                Dir(Local, "NVIDIA", "DXCache"),
                Dir(Local, "NVIDIA", "GLCache"),
                Dir(Local, "AMD", "DxCache"),
                Dir(Local, "AMD", "DxcCache"),
                Dir(Local, "AMD", "VkCache"),
                Dir(Local, "Intel", "ShaderCache"),
            ]),

        new("Windows log files",
            "Old setup and servicing logs (CBS, DISM).",
            true, true, () =>
            [
                new CleanTarget(Path.Combine(Win, "Logs", "CBS"), MinAgeDays: 7),
                new CleanTarget(Path.Combine(Win, "Logs", "DISM"), MinAgeDays: 7),
                new CleanTarget(Path.Combine(Win, "Logs", "MoSetup"), MinAgeDays: 7),
            ]),

        new("Browser caches",
            "Cached web content for Chrome, Edge, Brave, Opera, Vivaldi and Firefox. Close browsers first. Logins, history and bookmarks are NOT touched.",
            false, true, BrowserTargets),

        new("App caches",
            "Web caches of Discord, Teams, Slack, Steam and VS Code.",
            false, true, AppCacheTargets),

        new("Spotify cache",
            "Spotify's music cache. Off by default: it can also hold songs you downloaded for offline listening, which would need re-downloading. Close Spotify first.",
            false, false, () => [Dir(Local, "Spotify", "Data")]),

        new("Developer caches",
            "npm, pip, NuGet, Yarn and Go build caches. Packages are re-downloaded when needed.",
            false, false, () =>
            [
                Dir(Local, "npm-cache"),
                Dir(Local, "pip", "cache"),
                Dir(Local, "NuGet", "v3-cache"),
                Dir(Local, "NuGet", "http-cache"),
                Dir(Local, "NuGet", "plugins-cache"),
                Dir(Local, "Yarn", "Cache"),
                Dir(Local, "go-build"),
            ]),

        new("Recycle Bin",
            "Files you already deleted. Emptying it frees their space for good.",
            false, true, () => [], isRecycleBin: true),

        new("Old downloads (90+ days)",
            "Files in Downloads you haven't touched in 90 days. Off by default - open the folder and check before cleaning!",
            false, false, () => [new CleanTarget(Native.GetDownloadsFolder(), MinAgeDays: 90, RemoveEmptyDirs: false)]),
    ];

    static IEnumerable<CleanTarget> BrowserTargets()
    {
        foreach (var t in Chromium(Path.Combine(Local, "Google", "Chrome", "User Data"))) yield return t;
        foreach (var t in Chromium(Path.Combine(Local, "Microsoft", "Edge", "User Data"))) yield return t;
        foreach (var t in Chromium(Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data"))) yield return t;
        foreach (var t in Chromium(Path.Combine(Local, "Vivaldi", "User Data"))) yield return t;
        yield return Dir(Local, "Opera Software", "Opera Stable", "Cache");
        yield return Dir(Local, "Opera Software", "Opera GX Stable", "Cache");

        var ff = Path.Combine(Local, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(ff))
            foreach (var p in SafeDirs(ff))
            {
                yield return Dir(p, "cache2");
                yield return Dir(p, "startupCache");
                yield return Dir(p, "thumbnails");
            }
    }

    static IEnumerable<CleanTarget> Chromium(string userData)
    {
        if (!Directory.Exists(userData)) yield break;
        foreach (var n in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache", @"Crashpad\reports" })
            yield return Dir(userData, n);
        foreach (var p in SafeDirs(userData))
        {
            var name = Path.GetFileName(p);
            if (name != "Default" && !name.StartsWith("Profile ") && !name.StartsWith("Guest Profile")) continue;
            foreach (var c in new[] { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache" })
                yield return Dir(p, c);
        }
    }

    static IEnumerable<CleanTarget> AppCacheTargets()
    {
        string[] electronCaches = ["Cache", "Code Cache", "GPUCache"];
        foreach (var app in new[] { "discord", "Slack", @"Microsoft\Teams", "Code" })
            foreach (var c in electronCaches)
                yield return Dir(Roaming, app, c);
        yield return Dir(Roaming, "Code", "CachedData");
        yield return Dir(Roaming, "Code", "CachedExtensionVSIXs");
        yield return Dir(Local, "Steam", "htmlcache");
        yield return Dir(Local, "Packages", "MSTeams_8wekyb3d8bbwe", "LocalCache", "Microsoft", "MSTeams", "EBWebView", "Default", "Cache");
    }

    static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return []; }
    }

    static IEnumerable<FileInfo> TargetFiles(CleanTarget t, CancellationToken ct)
    {
        if (t.IsFile)
        {
            var fi = new FileInfo(t.Path);
            if (fi.Exists) yield return fi;
            yield break;
        }
        if (!Directory.Exists(t.Path)) yield break;
        var cutoff = DateTime.Now.AddDays(-t.MinAgeDays);
        foreach (var f in FileWalker.Walk(t.Path, t.Pattern, t.Recursive, ct: ct))
        {
            if (t.MinAgeDays > 0 && (f.LastWriteTime > cutoff || f.CreationTime > cutoff)) continue;
            yield return f;
        }
    }

    public static (long Size, int Files) Scan(CleanCategory c, CancellationToken ct)
    {
        if (c.IsRecycleBin)
        {
            var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Native.SHQUERYRBINFO>() };
            Native.SHQueryRecycleBin(null, ref info);
            return (info.i64Size, (int)info.i64NumItems);
        }

        long size = 0;
        int files = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in c.Targets())
            foreach (var f in TargetFiles(t, ct))
                if (seen.Add(f.FullName)) { size += f.Length; files++; }
        return (size, files);
    }

    public static (long Freed, int Deleted, int Failed) Clean(CleanCategory c, CancellationToken ct)
    {
        if (c.IsRecycleBin)
        {
            var (size, count) = Scan(c, ct);
            const uint SHERB_NOCONFIRMATION = 1, SHERB_NOPROGRESSUI = 2, SHERB_NOSOUND = 4;
            int hr = Native.SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            return hr == 0 || count == 0 ? (size, count, 0) : (0, 0, count);
        }

        long freed = 0;
        int deleted = 0, failed = 0;
        var targets = c.Targets().ToList();
        foreach (var t in targets)
        {
            foreach (var f in TargetFiles(t, ct).ToList())
            {
                try
                {
                    long len = f.Length;
                    if (f.IsReadOnly) f.IsReadOnly = false;
                    f.Delete();
                    freed += len;
                    deleted++;
                }
                catch { failed++; } // in use or access denied
            }

            if (!t.IsFile && t.Recursive && t.RemoveEmptyDirs && Directory.Exists(t.Path))
                RemoveEmptyDirs(t.Path);
        }
        return (freed, deleted, failed);
    }

    static void RemoveEmptyDirs(string root)
    {
        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }).OrderByDescending(d => d.Length).ToList();
        }
        catch { return; }

        foreach (var d in dirs)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            }
            catch { }
        }
    }
}
