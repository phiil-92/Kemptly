using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Kemptly.Core;

public enum LeftoverKind { Folder, Shortcut, RegistryKey, UninstallEntry, StartupEntry, ScheduledTask }

public class Leftover : ObservableObject
{
    public required LeftoverKind Kind { get; init; }
    /// <summary>Folder/file path, or a registry path like HKCU\Software\Vendor\App.</summary>
    public required string Location { get; init; }
    public string Note { get; init; } = "";
    public bool NeedsAdmin { get; init; }

    public RegistryHive Hive { get; init; }
    public string SubKey { get; init; } = "";
    public StartupItem? Startup { get; init; }
    public TaskItem? Task { get; init; }

    long _size;
    public long Size { get => _size; set { if (Set(ref _size, value)) Raise(nameof(SizeText)); } }
    public string SizeText => Size > 0 ? Fmt.Bytes(Size) : "";

    bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    string _status = "";
    public string Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(CanSelect)); } }

    public bool CanSelect => Status != "Removed";

    public string KindText => Kind switch
    {
        LeftoverKind.Folder => "Folder",
        LeftoverKind.Shortcut => "Shortcut",
        LeftoverKind.RegistryKey => "Registry key",
        LeftoverKind.UninstallEntry => "Uninstall entry",
        LeftoverKind.StartupEntry => "Startup entry",
        _ => "Scheduled task",
    };
}

/// <summary>
/// Finds what an uninstaller left behind: folders, shortcuts, registry keys, startup entries and scheduled tasks
/// that clearly belong to the app. Matching is by exact (normalised) name only, and anything another installed
/// program still uses is never offered.
/// </summary>
public static class Leftovers
{
    // Names that are never treated as an app's own folder or key, even if an app happens to be called that.
    static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "microsoft", "windows", "commonfiles", "packages", "temp", "tmp", "programs", "programfiles", "microsoftnet",
        "windowsapps", "packagecache", "installer", "app", "apps", "tools", "tool", "update", "updater", "updates",
        "data", "common", "shared", "plugins", "cache", "logs", "log", "settings", "config", "bin", "users", "public",
        "default", "system", "system32", "drivers", "classes", "policies", "wow6432node", "clients", "software", "setup",
        "install", "uninstall", "desktop", "documents", "downloads", "startmenu", "startup", "local", "roaming",
        "locallow", "appdata", "registeredapplications", "intel", "nvidia", "amd", "realtek", "oem", "application",
        "client", "launcher", "vpn", "game", "games", "main", "current", "runtime", "service", "services", "driver",
        "helper", "agent", "core", "files", "program", "release", "latest", "home", "user", "x64", "x86", "win64",
    };

    static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(c) || c is '+' or '#') sb.Append(c);
        return sb.ToString();
    }

    /// <summary>"7-Zip 24.08 (x64)" → "7-Zip", "Mozilla Firefox (x64 en-US)" → "Mozilla Firefox".</summary>
    static string CleanName(string name)
    {
        var s = Regex.Replace(name, @"\([^)]*\)|\[[^\]]*\]", " ");
        s = Regex.Replace(s, @"\b(v(ersion)?\s*)?\d+(\.\d+)+\S*", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b(x64|x86|amd64|arm64|win64|win32|64-bit|32-bit|64 bit|32 bit|en-us|de-de)\b", " ", RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '_', ',');
    }

    static string CleanPublisher(string pub)
    {
        var s = Regex.Replace(pub, @"[,.]?\s*\b(inc|llc|ltd|limited|gmbh|ag|corp|corporation|co|company|s\.?a|b\.?v|pty|srl|oy|ab|software|technologies|technology)\b\.?", " ", RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"\s+", " ").Trim(' ', ',', '.', '-');
    }

    static string Full(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\'); } catch { return p.TrimEnd('\\'); }
    }

    static bool IsUnder(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);

    static string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    record Root(string Path, bool Admin, bool Settings);

    static List<Root> FolderRoots()
    {
        var local = Env(Environment.SpecialFolder.LocalApplicationData);
        var list = new List<Root>
        {
            new(Env(Environment.SpecialFolder.ProgramFiles), true, false),
            new(Env(Environment.SpecialFolder.ProgramFilesX86), true, false),
            new(Env(Environment.SpecialFolder.CommonApplicationData), true, false),
            new(Env(Environment.SpecialFolder.CommonPrograms), true, false),
            new(local, false, false),
            new(Path.Combine(local, "Programs"), false, false),
            new(Path.Combine(Env(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"), false, false),
            new(Env(Environment.SpecialFolder.Programs), false, false),
            // Roaming AppData usually holds your settings and profiles for the app: offered, but not ticked.
            new(Env(Environment.SpecialFolder.ApplicationData), false, true),
        };
        return list.Where(r => r.Path != "" && Directory.Exists(r.Path))
                   .GroupBy(r => Full(r.Path), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>Folders that must never be removed or contain a removed folder (system and personal folders).</summary>
    static List<string> ProtectedFolders()
    {
        var list = new List<string>();
        foreach (Environment.SpecialFolder f in Enum.GetValues<Environment.SpecialFolder>())
        {
            var p = Env(f);
            if (p != "") list.Add(Full(p));
        }
        list.Add(Full(Path.Combine(Env(Environment.SpecialFolder.LocalApplicationData), "Programs")));
        list.Add(Full(Path.Combine(Env(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow")));
        list.Add(Full(Path.GetTempPath()));
        list.Add(Full(AppSettings.DataDir));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string? UninstallerFolder(InstalledApp app)
    {
        var exe = Shell.ExtractExePath(app.UninstallString);
        if (exe == null || !Path.IsPathRooted(exe)) return null;
        var name = Path.GetFileName(exe).ToLowerInvariant();
        if (name is "msiexec.exe" or "rundll32.exe" or "cmd.exe" or "powershell.exe") return null;
        var dir = Path.GetDirectoryName(exe);
        if (dir == null || dir.Contains("Package Cache", StringComparison.OrdinalIgnoreCase)
            || IsUnder(Full(dir), Full(Env(Environment.SpecialFolder.Windows)))
            || IsUnder(Full(dir), Full(Path.GetTempPath()))) return null;
        return Full(dir);
    }

    /// <summary>Scans for leftovers of an app. With forced=true the app is still installed and its uninstall entry is included.</summary>
    public static List<Leftover> Scan(InstalledApp app, bool forced)
    {
        var result = new List<Leftover>();
        var others = InstalledApps.Load().Where(a => !(a.Hive == app.Hive && a.KeyPath.Equals(app.KeyPath, StringComparison.OrdinalIgnoreCase))).ToList();

        var protectedFolders = ProtectedFolders();
        // Folders other installed programs live in: never offered, never inside an offered folder.
        // (Ignoring bogus entries like a program that registered all of "Program Files" as its folder.)
        var otherFolders = others.SelectMany(a => new[] { a.InstallLocation, UninstallerFolder(a) ?? "" })
                                 .Where(p => p.Length > 3 && Path.IsPathRooted(p)).Select(Full)
                                 .Where(o => !protectedFolders.Any(p => IsUnder(p, o)))
                                 .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var otherNames = others.Select(a => Norm(CleanName(a.Name))).ToHashSet();
        var otherPublishers = others.Select(a => Norm(CleanPublisher(a.Publisher))).ToHashSet();
        var personalFolders = new[]
            {
                Env(Environment.SpecialFolder.Windows), Env(Environment.SpecialFolder.MyDocuments), Env(Environment.SpecialFolder.DesktopDirectory),
                Env(Environment.SpecialFolder.MyPictures), Env(Environment.SpecialFolder.MyMusic), Env(Environment.SpecialFolder.MyVideos),
                Native.GetDownloadsFolder(), Environment.GetEnvironmentVariable("OneDrive") ?? "",
            }
            .Where(p => p.Length > 3).Select(Full).ToList();
        var roots = FolderRoots();

        // A folder that may be removed at all: not a drive root, system/personal folder or another program's folder.
        bool SafePath(string f)
        {
            if (f.Length <= 3) return false;
            if (protectedFolders.Any(p => IsUnder(p, f))) return false;                  // is or contains a system/personal folder
            if (personalFolders.Any(p => IsUnder(f, p))) return false;                   // inside Windows, Documents, Desktop, ...
            if (otherFolders.Any(o => IsUnder(o, f) || IsUnder(f, o))) return false;     // used by another program
            return true;
        }

        // Names this app goes by.
        var display = CleanName(app.Name);
        var pub = CleanPublisher(app.Publisher);
        var names = new List<string> { display };
        if (pub.Length > 0 && display.StartsWith(pub + " ", StringComparison.OrdinalIgnoreCase))
            names.Add(display[(pub.Length + 1)..]); // "Mozilla Firefox" → "Firefox" (as in AppData\Mozilla\Firefox)
        // Some installers register a shared parent (even "Program Files") as their install folder: those are ignored.
        var installDir = app.InstallLocation.Length > 3 && Path.IsPathRooted(app.InstallLocation) ? Full(app.InstallLocation) : null;
        if (installDir != null && !SafePath(installDir)) installDir = null;
        var uninstallDir = UninstallerFolder(app);
        if (uninstallDir != null && !SafePath(uninstallDir)) uninstallDir = null;
        foreach (var d in new[] { installDir, uninstallDir })
            if (d != null) names.Add(Path.GetFileName(d));
        // "Python 3.14" -> "Python": a folder with that name may hold other versions too, so such matches aren't ticked.
        bool versioned = Regex.IsMatch(app.Name, @"\d+\.\d+");
        var keys = names.Select(Norm).Where(k => k.Length >= 3 && !Generic.Contains(k)).ToHashSet();
        // Don't use a name that is also another installed program's name.
        keys.ExceptWith(otherNames.Where(n => n != Norm(display)));
        var pubKey = Norm(pub);
        bool pubUsable = pubKey.Length >= 3 && !Generic.Contains(pubKey);
        // The publisher's whole folder/key is only offered when no other installed program comes from that publisher.
        bool pubWhole = pubUsable && !otherPublishers.Contains(pubKey);

        bool SafeFolder(string path)
        {
            var f = Full(path);
            if (!SafePath(f)) return false;
            if (result.Any(r => r.Kind == LeftoverKind.Folder && (IsUnder(f, r.Location) || IsUnder(r.Location, f)))) return false; // already listed
            try { if (new DirectoryInfo(f).Attributes.HasFlag(FileAttributes.ReparsePoint)) return false; } catch { return false; }
            return true;
        }

        void AddFolder(string path, Root? root, string note, bool byName = true)
        {
            if (!Directory.Exists(path) || !SafeFolder(path)) return;
            bool settings = root?.Settings == true;
            bool shared = false;
            if (byName && versioned)
                try { shared = HasVersionedChildren(new DirectoryInfo(path).GetDirectories().Select(d => d.Name)); } catch { }
            bool admin = root?.Admin ?? IsUnder(Full(path), Full(Env(Environment.SpecialFolder.ProgramFiles)))
                                     || IsUnder(Full(path), Full(Env(Environment.SpecialFolder.ProgramFilesX86)))
                                     || IsUnder(Full(path), Full(Env(Environment.SpecialFolder.CommonApplicationData)));
            result.Add(new Leftover
            {
                Kind = LeftoverKind.Folder, Location = Full(path), NeedsAdmin = admin,
                Note = settings ? "Your settings for this app" : shared ? "May hold other versions too" : note,
                Selected = !settings && !shared,
            });
        }

        // 1. The install folder and the uninstaller's folder.
        if (installDir != null) AddFolder(installDir, null, "Install folder", byName: false);
        if (uninstallDir != null) AddFolder(uninstallDir, null, "Install folder", byName: false);

        // 2. Folders named after the app, directly or under its publisher's folder.
        foreach (var root in roots)
        {
            DirectoryInfo[] subs;
            try { subs = new DirectoryInfo(root.Path).GetDirectories(); } catch { continue; }
            foreach (var d in subs)
            {
                var n = Norm(d.Name);
                if (keys.Contains(n)) AddFolder(d.FullName, root, "");
                else if (pubUsable && n == pubKey)
                {
                    DirectoryInfo[] inner;
                    try { inner = d.GetDirectories(); } catch { continue; }
                    var matches = inner.Where(i => keys.Contains(Norm(i.Name))).ToList();
                    if (matches.Count == 0) continue;
                    bool onlyThis = pubWhole && matches.Count == inner.Length && !SafeHasFiles(d);
                    if (onlyThis) AddFolder(d.FullName, root, "Publisher folder (nothing else in it)");
                    else foreach (var m in matches) AddFolder(m.FullName, root, "");
                }
            }
        }

        // 3. Shortcuts on the desktop and in the Start menu that point at nothing (or into a removed folder).
        var folders = result.Where(r => r.Kind == LeftoverKind.Folder).Select(r => r.Location).ToList();
        if (installDir != null) folders.Add(installDir); // passed SafePath; the uninstaller may have removed it already
        foreach (var dir in new[] { Env(Environment.SpecialFolder.DesktopDirectory), Env(Environment.SpecialFolder.CommonDesktopDirectory),
                                    Env(Environment.SpecialFolder.Programs), Env(Environment.SpecialFolder.CommonPrograms) })
        {
            if (dir == "" || !Directory.Exists(dir)) continue;
            IEnumerable<string> lnks;
            try { lnks = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.TopDirectoryOnly); } catch { continue; }
            foreach (var lnk in lnks)
            {
                if (folders.Any(f => IsUnder(Full(lnk), f))) continue;
                string target;
                try { target = StartupManager.ResolveShortcut(lnk).Target; } catch { continue; }
                bool intoRemoved = target != "" && folders.Any(f => IsUnder(Full(target), f));
                bool nameMatch = keys.Contains(Norm(Path.GetFileNameWithoutExtension(lnk))) && (target == "" || !File.Exists(target));
                if (!intoRemoved && !nameMatch) continue;
                bool admin = lnk.StartsWith(Env(Environment.SpecialFolder.CommonDesktopDirectory), StringComparison.OrdinalIgnoreCase)
                          || lnk.StartsWith(Env(Environment.SpecialFolder.CommonPrograms), StringComparison.OrdinalIgnoreCase);
                result.Add(new Leftover { Kind = LeftoverKind.Shortcut, Location = lnk, NeedsAdmin = admin, Selected = true });
            }
        }

        // 4. Registry keys named after the app (HKCU\Software, HKLM\SOFTWARE and its 32-bit view).
        foreach (var (hive, basePath, label) in new[]
                 {
                     (RegistryHive.CurrentUser, "Software", "HKCU\\Software"),
                     (RegistryHive.LocalMachine, "SOFTWARE", "HKLM\\SOFTWARE"),
                     (RegistryHive.LocalMachine, "SOFTWARE\\WOW6432Node", "HKLM\\SOFTWARE\\WOW6432Node"),
                 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var baseKey = root.OpenSubKey(basePath);
                if (baseKey == null) continue;
                bool admin = hive == RegistryHive.LocalMachine;
                void AddKey(string sub, string note)
                {
                    bool shared = false;
                    if (versioned)
                        try
                        {
                            using var k = baseKey.OpenSubKey(sub);
                            var children = k?.GetSubKeyNames() ?? [];
                            var grandChildren = children.SelectMany(c =>
                            {
                                using var ck = k!.OpenSubKey(c);
                                return ck?.GetSubKeyNames() ?? [];
                            }).ToList();
                            shared = HasVersionedChildren(children.Concat(grandChildren));
                        }
                        catch { }
                    result.Add(new Leftover
                    {
                        Kind = LeftoverKind.RegistryKey, Hive = hive, SubKey = basePath + "\\" + sub, Location = label + "\\" + sub,
                        NeedsAdmin = admin, Selected = !shared, Note = shared ? "May hold other versions too" : note,
                    });
                }
                foreach (var name in baseKey.GetSubKeyNames())
                {
                    if (name.Contains('"')) continue;
                    var n = Norm(name);
                    if (keys.Contains(n)) AddKey(name, "");
                    else if (pubUsable && n == pubKey)
                    {
                        using var pk = baseKey.OpenSubKey(name);
                        if (pk == null) continue;
                        var inner = pk.GetSubKeyNames();
                        var matches = inner.Where(i => keys.Contains(Norm(i)) && !i.Contains('"')).ToList();
                        if (matches.Count == 0) continue;
                        if (pubWhole && matches.Count == inner.Length && pk.ValueCount == 0) AddKey(name, "Publisher key (nothing else in it)");
                        else foreach (var m in matches) AddKey(name + "\\" + m, "");
                    }
                }
            }
            catch { }
        }

        // 5. The uninstall entry itself (forced uninstall only: a normal uninstaller removes it).
        if (forced)
        {
            bool admin = app.Hive == RegistryHive.LocalMachine;
            result.Add(new Leftover
            {
                Kind = LeftoverKind.UninstallEntry, Hive = app.Hive, SubKey = app.KeyPath,
                Location = (admin ? "HKLM\\" : "HKCU\\") + app.KeyPath, NeedsAdmin = admin,
                Selected = true, Note = "Removes it from this list",
            });
        }

        // 6. Startup entries and scheduled tasks that start something inside a removed folder, or a missing file with the app's name.
        try
        {
            foreach (var s in StartupManager.Load())
            {
                bool inFolder = s.FilePath != null && folders.Any(f => IsUnder(Full(s.FilePath), f));
                bool missing = !s.FileExists && keys.Any(k => Norm(s.DisplayName).Contains(k));
                if (inFolder || missing)
                    result.Add(new Leftover { Kind = LeftoverKind.StartupEntry, Location = $"{s.DisplayName} ({s.Location})", Startup = s, NeedsAdmin = s.NeedsAdmin, Selected = true });
            }
        }
        catch { }
        try
        {
            foreach (var t in TaskSchedulerReader.Load())
            {
                if (t.IsMicrosoft) continue;
                bool inFolder = t.ExePath != null && Path.IsPathRooted(t.ExePath) && folders.Any(f => IsUnder(Full(t.ExePath), f));
                bool missing = (t.ExePath == null || !File.Exists(t.ExePath)) && keys.Any(k => Norm(t.Name).Contains(k));
                if (inFolder || missing)
                    result.Add(new Leftover { Kind = LeftoverKind.ScheduledTask, Location = t.FullPath, Task = t, Selected = true });
            }
        }
        catch { }

        foreach (var l in result.Where(l => l.NeedsAdmin && !Admin.IsAdmin)) l.Status = "Asks for admin permission";
        return result;
    }

    static bool HasVersionedChildren(IEnumerable<string> names) =>
        names.Any(n => Regex.IsMatch(n, @"\d+\.\d+|\d{2,}$"));

    static bool SafeHasFiles(DirectoryInfo d)
    {
        try { return d.EnumerateFiles().Any(); } catch { return true; }
    }

    public static long FolderSize(string path)
    {
        try { return FileWalker.Walk(path).Sum(f => f.Length); } catch { return 0; }
    }

    /// <summary>
    /// Removes the given leftovers. Folders and shortcuts go to the Recycle Bin; registry keys and scheduled tasks are
    /// backed up first, so the whole removal can be undone from the History page.
    /// </summary>
    /// <summary>
    /// Removes the given leftovers. Folders and shortcuts go to the Recycle Bin; registry keys and scheduled tasks are
    /// backed up first, so the whole removal can be undone from the History page. Items that need administrator rights
    /// (while Kemptly isn't elevated) are done together in one elevated step, so Windows asks only once.
    /// </summary>
    public static async Task<(int Ok, int Failed)> RemoveAsync(IList<Leftover> items, string appName)
    {
        var backupDir = Path.Combine(AppSettings.DataDir, "backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var regBackups = new List<string>();
        var taskBackups = new List<string>();
        var removed = new List<string>();
        int ok = 0, failed = 0, recycled = 0;

        void Done(Leftover l)
        {
            l.Status = "Removed";
            l.Selected = false;
            removed.Add($"{l.KindText}: {l.Location}");
            ok++;
            if (l.Kind is LeftoverKind.Folder or LeftoverKind.Shortcut) recycled++;
        }
        void Fail(Leftover l, string why) { l.Status = why; failed++; }

        async Task<string> BackupKey(Leftover l)
        {
            Directory.CreateDirectory(backupDir);
            var file = Path.Combine(backupDir, $"key{regBackups.Count + 1}-{Guid.NewGuid():N}.reg");
            var r = await Shell.RunAsync("reg.exe", $"export \"{l.Location}\" \"{file}\" /y /reg:64");
            if (r.Code != 0) throw new IOException("Could not back up the key, so it was left alone");
            return (l.Hive == RegistryHive.LocalMachine ? "M:" : "U:") + file;
        }

        var elevated = items.Where(l => l.NeedsAdmin && !Admin.IsAdmin && l.Kind != LeftoverKind.ScheduledTask).ToList();
        foreach (var l in items.Except(elevated))
        {
            l.Status = "Removing…";
            try
            {
                switch (l.Kind)
                {
                    case LeftoverKind.Folder:
                    case LeftoverKind.Shortcut:
                        await Task.Run(() =>
                        {
                            if (!Directory.Exists(l.Location) && !File.Exists(l.Location)) return;
                            if (new FileInfo(l.Location).Attributes.HasFlag(FileAttributes.ReparsePoint))
                                throw new IOException("It is a link to another location, so it was left alone");
                            if (!Shell.Recycle(l.Location)) throw new IOException("In use or access denied");
                        });
                        break;

                    case LeftoverKind.RegistryKey:
                    case LeftoverKind.UninstallEntry:
                    {
                        var backup = await BackupKey(l);
                        await Task.Run(() =>
                        {
                            using var root = RegistryKey.OpenBaseKey(l.Hive, RegistryView.Registry64);
                            root.DeleteSubKeyTree(l.SubKey, false);
                        });
                        regBackups.Add(backup);
                        break;
                    }

                    case LeftoverKind.StartupEntry:
                        StartupManager.Remove(l.Startup!); // logs its own History entry with undo
                        break;

                    case LeftoverKind.ScheduledTask:
                    {
                        var xml = await Task.Run(() => TaskSchedulerReader.Delete(l.Task!));
                        Directory.CreateDirectory(backupDir);
                        var file = Path.Combine(backupDir, $"task{taskBackups.Count + 1}.xml");
                        File.WriteAllText(file, xml);
                        taskBackups.Add($"{l.Task!.FolderPath}\t{l.Task.Name}\t{file}");
                        break;
                    }
                }
                Done(l);
            }
            catch (Exception ex) { Fail(l, Admin.IsAccessDenied(ex) ? "Access denied (needs admin)" : ex.Message); }
        }

        if (elevated.Count > 0)
            await RemoveElevatedAsync(elevated, backupDir, BackupKey, regBackups, Done, Fail);

        if (ok > 0)
            History.Add(ChangeKind.LeftoversRemoved, $"Removed {ok} leftover(s) of {appName}", string.Join("\n", removed),
                History.Data(("reg", string.Join("\n", regBackups)), ("tasks", string.Join("\n", taskBackups)), ("recycled", recycled > 0)));
        return (ok, failed);
    }

    static string Ps(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Runs one elevated PowerShell that removes all admin-only items and reports a result per item.</summary>
    static async Task RemoveElevatedAsync(List<Leftover> items, string backupDir, Func<Leftover, Task<string>> backupKey,
        List<string> regBackups, Action<Leftover> done, Action<Leftover, string> fail)
    {
        var script = new StringBuilder("$ErrorActionPreference='Stop'; Add-Type -AssemblyName Microsoft.VisualBasic; $r=@()\n");
        var pending = new Dictionary<int, (Leftover Item, string? Backup)>();
        for (int i = 0; i < items.Count; i++)
        {
            var l = items[i];
            l.Status = "Waiting for permission…";
            string? op = null, backup = null;
            try
            {
                switch (l.Kind)
                {
                    case LeftoverKind.Folder:
                        if (new DirectoryInfo(l.Location).Attributes.HasFlag(FileAttributes.ReparsePoint))
                            throw new IOException("It is a link to another location, so it was left alone");
                        op = $"[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory({Ps(l.Location)},'OnlyErrorDialogs','SendToRecycleBin')";
                        break;
                    case LeftoverKind.Shortcut:
                        op = $"[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile({Ps(l.Location)},'OnlyErrorDialogs','SendToRecycleBin')";
                        break;
                    case LeftoverKind.RegistryKey:
                    case LeftoverKind.UninstallEntry:
                        backup = await backupKey(l);
                        op = $"Remove-Item -LiteralPath {Ps("Registry::HKEY_LOCAL_MACHINE\\" + l.SubKey)} -Recurse -Force";
                        break;
                    case LeftoverKind.StartupEntry when l.Startup!.ShortcutFile != null:
                        op = $"[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile({Ps(l.Startup.ShortcutFile)},'OnlyErrorDialogs','SendToRecycleBin')";
                        break;
                    case LeftoverKind.StartupEntry:
                        var run = l.Startup.Source == StartupSource.MachineRun32
                            ? @"Registry::HKEY_LOCAL_MACHINE\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"
                            : @"Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Run";
                        op = $"Remove-ItemProperty -LiteralPath {Ps(run)} -Name {Ps(l.Startup.Name)}";
                        break;
                }
            }
            catch (Exception ex) { fail(l, ex.Message); continue; }
            if (op == null) continue;
            pending[i] = (l, backup);
            script.Append($"try {{ {op}; $r += \"{i}`tOK\" }} catch {{ $r += (\"{i}`t\" + ($_.Exception.Message -replace '\\s+',' ')) }}\n");
        }
        if (pending.Count == 0) return;

        Directory.CreateDirectory(backupDir);
        var resultFile = Path.Combine(backupDir, $"elevated-{Guid.NewGuid():N}.txt");
        script.Append($"$r | Out-File -LiteralPath {Ps(resultFile)} -Encoding utf8\n");
        int code = await Shell.RunElevatedAsync("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Shell.EncodePowerShell(script.ToString())}", false);

        var results = new Dictionary<int, string>();
        try
        {
            foreach (var line in File.ReadAllLines(resultFile))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length == 2 && int.TryParse(parts[0], out var idx)) results[idx] = parts[1];
            }
            File.Delete(resultFile);
        }
        catch { }

        foreach (var (idx, (l, backup)) in pending)
        {
            if (results.TryGetValue(idx, out var res) && res == "OK")
            {
                if (backup != null) regBackups.Add(backup);
                if (l.Kind == LeftoverKind.StartupEntry)
                    History.Add(ChangeKind.StartupRemoved, $"Removed startup entry \"{l.Startup!.DisplayName}\"", l.Startup.Command,
                        History.Data(("name", l.Startup.Name), ("source", l.Startup.Source), ("command", l.Startup.Command), ("shortcut", l.Startup.ShortcutFile)));
                done(l);
            }
            else
            {
                if (backup != null) try { File.Delete(backup[2..]); } catch { }
                fail(l, code == -2 ? "Administrator permission was declined" : res ?? "Not removed");
            }
        }
    }

    /// <summary>Restores registry keys and tasks from a LeftoversRemoved History entry. Files come back via the Recycle Bin.</summary>
    public static async Task<string> UndoAsync(Dictionary<string, string> data)
    {
        var problems = new List<string>();
        var reg = data.GetValueOrDefault("reg", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in reg)
        {
            var file = entry[2..];
            if (!File.Exists(file)) { problems.Add($"Backup missing: {file}"); continue; }
            int code = entry.StartsWith("M:")
                ? await Shell.RunElevatedAsync("reg.exe", $"import \"{file}\" /reg:64", false)
                : (await Shell.RunAsync("reg.exe", $"import \"{file}\" /reg:64")).Code;
            if (code != 0) problems.Add(code == -2 ? "Administrator permission was declined" : $"Could not restore {Path.GetFileName(file)}");
        }
        foreach (var entry in data.GetValueOrDefault("tasks", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = entry.Split('\t');
            if (p.Length != 3 || !File.Exists(p[2])) { problems.Add("Task backup missing"); continue; }
            try { await Task.Run(() => TaskSchedulerReader.Restore(p[0], p[1], File.ReadAllText(p[2]))); }
            catch (Exception ex) { problems.Add($"Task {p[1]}: {(Admin.IsAccessDenied(ex) ? "needs admin" : ex.Message)}"); }
        }
        if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems.Distinct()));

        if (data.GetValueOrDefault("recycled") == "True")
        {
            Shell.Open("shell:RecycleBinFolder");
            return "Registry keys and tasks are restored. The files are in the Recycle Bin: select them, right-click and choose Restore.";
        }
        return "Registry keys and tasks are restored.";
    }
}
