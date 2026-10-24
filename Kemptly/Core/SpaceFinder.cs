using System.Text.Json;

namespace Kemptly.Core;

public enum SpaceAction { None, ShowFile, OpenFolder, DiskCleanup, SystemProtection, ToolsPage, DeleteFolder }

public class SpaceItem : ObservableObject
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public long Size { get; init; }
    public bool SizeUnknown { get; init; }
    public string? Path { get; init; }
    public SpaceAction Action { get; init; }
    public string ActionText => Action switch
    {
        SpaceAction.ShowFile => "Show file",
        SpaceAction.OpenFolder => "Open folder",
        SpaceAction.DiskCleanup => "Open Disk Cleanup",
        SpaceAction.SystemProtection => "Manage restore points",
        SpaceAction.ToolsPage => "Go to Tools",
        SpaceAction.DeleteFolder => "Delete",
        _ => "",
    };
    public bool HasAction => Action != SpaceAction.None;
    public string SizeText => SizeUnknown ? "needs admin" : Fmt.Bytes(Size);
}

/// <summary>Finds big space users the junk cleaner doesn't touch: system files, old Windows installs, VM disks, node_modules.</summary>
public static class SpaceFinder
{
    static readonly string SysDrive = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static long FileSize(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }
    static long DirSize(string dir, CancellationToken ct) =>
        Directory.Exists(dir) ? FileWalker.Walk(dir, ct: ct).Sum(f => { try { return f.Length; } catch { return 0L; } }) : 0;

    public static async Task<List<SpaceItem>> FindAsync(IProgress<string> progress, CancellationToken ct)
    {
        var items = new List<SpaceItem>();

        progress.Report("Checking system files…");
        var hiber = System.IO.Path.Combine(SysDrive, "hiberfil.sys");
        if (File.Exists(hiber))
            items.Add(new SpaceItem { Title = "Hibernation file", Size = FileSize(hiber), Action = SpaceAction.ToolsPage,
                Description = "Stores memory for hibernate and Fast Startup. You can turn hibernation off on the Tools page to delete it." });
        var page = System.IO.Path.Combine(SysDrive, "pagefile.sys");
        if (File.Exists(page))
            items.Add(new SpaceItem { Title = "Page file", Size = FileSize(page),
                Description = "Virtual memory managed by Windows. Leave it alone; Windows sizes it automatically." });

        progress.Report("Checking restore points…");
        var shadow = await Shell.PowerShellJsonAsync(
            "ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_ShadowStorage -ErrorAction Stop | ForEach-Object { [int64]$_.UsedSpace })");
        long restore = shadow is JsonElement { ValueKind: JsonValueKind.Array } sa ? sa.EnumerateArray().Sum(e => e.GetInt64()) : -1;
        items.Add(new SpaceItem { Title = "System restore points", Size = Math.Max(restore, 0), SizeUnknown = restore < 0,
            Action = SpaceAction.SystemProtection,
            Description = "Snapshots for rolling back Windows. You can limit their maximum size under System Protection > Configure." });

        progress.Report("Looking for old Windows installations…");
        foreach (var (dir, title) in new[] { ("Windows.old", "Previous Windows installation"), ("$WINDOWS.~BT", "Windows upgrade leftovers"),
                                             ("$Windows.~WS", "Windows upgrade leftovers"), ("ESD", "Windows setup files") })
        {
            var full = System.IO.Path.Combine(SysDrive, dir);
            if (!Directory.Exists(full)) continue;
            items.Add(new SpaceItem { Title = title, Path = full, Size = await Task.Run(() => DirSize(full, ct)), Action = SpaceAction.DiskCleanup,
                Description = $"{full}. Remove it with Disk Cleanup > \"Clean up system files\" (size shown may be lower than actual without admin rights)." });
        }

        progress.Report("Looking for virtual machine disks…");
        var vhdx = new List<string>();
        void AddMatches(string dir, string pattern, int depth)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                vhdx.AddRange(Directory.EnumerateFiles(dir, pattern, new EnumerationOptions
                    { RecurseSubdirectories = true, MaxRecursionDepth = depth, IgnoreInaccessible = true }));
            }
            catch { }
        }
        AddMatches(System.IO.Path.Combine(Local, "Packages"), "ext4.vhdx", 3);
        AddMatches(System.IO.Path.Combine(Local, "wsl"), "*.vhdx", 2);
        AddMatches(System.IO.Path.Combine(Local, "Docker"), "*.vhdx", 4);
        foreach (var f in vhdx.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool docker = f.Contains(@"\Docker\", StringComparison.OrdinalIgnoreCase);
            items.Add(new SpaceItem { Title = docker ? "Docker Desktop disk" : "WSL Linux disk", Path = f, Size = FileSize(f), Action = SpaceAction.ShowFile,
                Description = docker ? "Grows with images and containers and doesn't shrink on its own. Free space with \"docker system prune\" in a terminal."
                                     : "A Linux distro's virtual disk. It doesn't shrink on its own; delete files inside Linux, or remove distros you don't use (wsl --unregister)." });
        }

        progress.Report("Looking for node_modules folders…");
        var modules = await Task.Run(() => FindNodeModules(ct));
        foreach (var (dir, size) in modules.OrderByDescending(m => m.Size).Take(15))
            items.Add(new SpaceItem { Title = "node_modules", Path = dir, Size = size, Action = SpaceAction.DeleteFolder,
                Description = $"{dir}. JavaScript dependencies; \"npm install\" downloads them again when needed." });

        var downloads = Native.GetDownloadsFolder();
        progress.Report("Measuring Downloads…");
        items.Add(new SpaceItem { Title = "Downloads folder", Path = downloads, Size = await Task.Run(() => DirSize(downloads, ct)),
            Action = SpaceAction.OpenFolder, Description = "Old installers and downloads tend to pile up here." });

        return items.Where(i => i.Size > 0 || i.SizeUnknown).OrderByDescending(i => i.Size).ToList();
    }

    static List<(string Dir, long Size)> FindNodeModules(CancellationToken ct)
    {
        var found = new List<(string, long)>();
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((Profile, 0));
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            string[] subs;
            try { subs = Directory.GetDirectories(dir, "*", opts); } catch { continue; }
            foreach (var s in subs)
            {
                var name = System.IO.Path.GetFileName(s);
                if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) found.Add((s, DirSize(s, ct)));
                else if (depth < 6 && !name.StartsWith('.') && !name.Equals("AppData", StringComparison.OrdinalIgnoreCase)) stack.Push((s, depth + 1));
            }
        }
        return found;
    }
}
