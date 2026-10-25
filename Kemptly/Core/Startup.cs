using Microsoft.Win32;

namespace Kemptly.Core;

public enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

public class StartupItem : ObservableObject
{
    public required string Name { get; init; }      // registry value name or shortcut file name
    public required string Command { get; init; }
    public required StartupSource Source { get; init; }
    public string? FilePath { get; init; }
    public string? ShortcutFile { get; init; }
    public string Publisher { get; set; } = "";

    public bool FileExists => FilePath != null && File.Exists(FilePath);
    public string DisplayName => ShortcutFile != null ? Path.GetFileNameWithoutExtension(Name) : Name;
    public bool NeedsAdmin => Source is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonFolder;

    public string Location => Source switch
    {
        StartupSource.UserRun => "Registry · current user",
        StartupSource.MachineRun => "Registry · all users",
        StartupSource.MachineRun32 => "Registry · all users (32-bit)",
        StartupSource.UserFolder => "Startup folder · current user",
        _ => "Startup folder · all users",
    };

    bool _enabled;
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) Raise(nameof(StateText)); } }
    public string StateText => Enabled ? "Enabled" : "Disabled";
}

/// <summary>
/// Reads and toggles startup entries the same way Task Manager does (via the StartupApproved keys),
/// so disabling is reversible and doesn't delete anything.
/// </summary>
public static class StartupManager
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    static RegistryKey Hklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public static List<StartupItem> Load()
    {
        var list = new List<StartupItem>();
        using (var hklm = Hklm)
        {
            ReadRun(list, Registry.CurrentUser, RunKey, StartupSource.UserRun);
            ReadRun(list, hklm, RunKey, StartupSource.MachineRun);
            ReadRun(list, hklm, Run32Key, StartupSource.MachineRun32);
        }
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.UserFolder);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.CommonFolder);

        foreach (var item in list)
        {
            item.Enabled = ReadApproved(item);
            if (!item.FileExists) item.Publisher = "File not found";
            else if (Signature.IsWindowsPath(item.FilePath!)) item.Publisher = "Windows component";
            else item.Publisher = Signature.Check(item.FilePath!).Publisher ?? "Unsigned";
        }
        return list;
    }

    static void ReadRun(List<StartupItem> list, RegistryKey root, string path, StartupSource source)
    {
        using var key = root.OpenSubKey(path);
        if (key == null) return;
        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) continue;
            var cmd = key.GetValue(name)?.ToString() ?? "";
            list.Add(new StartupItem { Name = name, Command = cmd, Source = source, FilePath = Shell.ExtractExePath(cmd) });
        }
    }

    static void ReadFolder(List<StartupItem> list, string folder, StartupSource source)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.GetFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            string target = file, args = "";
            if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                (target, args) = ResolveShortcut(file);
            list.Add(new StartupItem
            {
                Name = name,
                Command = (target + " " + args).Trim(),
                Source = source,
                FilePath = target,
                ShortcutFile = file,
            });
        }
    }

    public static (string Target, string Args) ResolveShortcut(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return (lnk, "");
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(lnk);
            string target = sc.TargetPath ?? "";
            string args = sc.Arguments ?? "";
            return (string.IsNullOrEmpty(target) ? lnk : target, args);
        }
        catch { return (lnk, ""); }
    }

    static (RegistryKey Root, string Sub) ApprovedLocation(StartupSource s) => s switch
    {
        StartupSource.UserRun => (Registry.CurrentUser, "Run"),
        StartupSource.MachineRun => (Hklm, "Run"),
        StartupSource.MachineRun32 => (Hklm, "Run32"),
        StartupSource.UserFolder => (Registry.CurrentUser, "StartupFolder"),
        _ => (Hklm, "StartupFolder"),
    };

    static bool ReadApproved(StartupItem item)
    {
        var (root, sub) = ApprovedLocation(item.Source);
        using var key = root.OpenSubKey(ApprovedBase + sub);
        // Even first byte = enabled (02/06), odd = disabled (03/07). Missing = enabled.
        return key?.GetValue(item.Name) is not byte[] { Length: > 0 } data || data[0] % 2 == 0;
    }

    /// <summary>Throws UnauthorizedAccessException / SecurityException when admin rights are needed.</summary>
    public static void SetEnabled(StartupItem item, bool enabled, bool log = true)
    {
        var (root, sub) = ApprovedLocation(item.Source);
        using var key = root.CreateSubKey(ApprovedBase + sub, true);
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTimeUtc()).CopyTo(data, 4);
        key.SetValue(item.Name, data, RegistryValueKind.Binary);
        item.Enabled = enabled;
        if (log)
            History.Add(ChangeKind.StartupToggle, $"{(enabled ? "Enabled" : "Disabled")} startup app \"{item.DisplayName}\"",
                item.Location, History.Data(("name", item.Name), ("source", item.Source), ("enabled", enabled)));
    }

    /// <summary>Re-creates a Run registry entry that was removed (used by undo).</summary>
    public static void RestoreRunValue(StartupSource source, string name, string command)
    {
        var root = source == StartupSource.UserRun ? Registry.CurrentUser : Hklm;
        var path = source == StartupSource.MachineRun32 ? Run32Key : RunKey;
        using var key = root.CreateSubKey(path, true);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public static void Remove(StartupItem item)
    {
        if (item.ShortcutFile != null)
        {
            if (!Shell.Recycle(item.ShortcutFile)) throw new UnauthorizedAccessException("Could not remove the shortcut.");
        }
        else
        {
            var root = item.Source == StartupSource.UserRun ? Registry.CurrentUser : Hklm;
            var path = item.Source == StartupSource.MachineRun32 ? Run32Key : RunKey;
            using var key = root.OpenSubKey(path, true) ?? throw new InvalidOperationException("Run key not found.");
            key.DeleteValue(item.Name, false);
        }
        try
        {
            var (aroot, sub) = ApprovedLocation(item.Source);
            using var ak = aroot.OpenSubKey(ApprovedBase + sub, true);
            ak?.DeleteValue(item.Name, false);
        }
        catch { }
        History.Add(ChangeKind.StartupRemoved, $"Removed startup entry \"{item.DisplayName}\"", item.Command,
            History.Data(("name", item.Name), ("source", item.Source), ("command", item.Command), ("shortcut", item.ShortcutFile)));
    }
}

public class TaskItem : ObservableObject
{
    public required string Name { get; init; }
    public required string FolderPath { get; init; }
    public string FullPath => FolderPath.TrimEnd('\\') + "\\" + Name;
    public string Author { get; init; } = "";
    public string Triggers { get; init; } = "";
    public string Action { get; init; } = "";
    public string? ExePath { get; init; }
    public string Arguments { get; init; } = "";
    public string LastRun { get; init; } = "";
    public bool IsMicrosoft { get; init; }
    public bool RunsAtLogonOrBoot { get; init; }

    bool _enabled;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
}

/// <summary>Reads Windows Task Scheduler via its COM API.</summary>
public static class TaskSchedulerReader
{
    static dynamic Connect()
    {
        var t = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler not available.");
        dynamic svc = Activator.CreateInstance(t)!;
        svc.Connect();
        return svc;
    }

    public static List<TaskItem> Load()
    {
        var list = new List<TaskItem>();
        dynamic svc = Connect();
        Walk(svc.GetFolder("\\"), list);
        return list;
    }

    static void Walk(dynamic folder, List<TaskItem> list)
    {
        string path = folder.Path;
        try
        {
            dynamic tasks = folder.GetTasks(1); // include hidden
            int n = tasks.Count;
            for (int i = 1; i <= n; i++)
            {
                try { list.Add(Read(tasks[i], path)); } catch { }
            }
        }
        catch { }
        try
        {
            dynamic subs = folder.GetFolders(0);
            int n = subs.Count;
            for (int i = 1; i <= n; i++) Walk(subs[i], list);
        }
        catch { }
    }

    static TaskItem Read(dynamic task, string folderPath)
    {
        dynamic def = task.Definition;
        string exe = "", args = "";
        var actions = new List<string>();
        dynamic acts = def.Actions;
        int ac = acts.Count;
        for (int i = 1; i <= ac; i++)
        {
            dynamic a = acts[i];
            int type = a.Type;
            if (type == 0)
            {
                string p = a.Path ?? "";
                string ar = a.Arguments ?? "";
                if (exe == "") { exe = p; args = ar; }
                actions.Add((p + " " + ar).Trim());
            }
            else actions.Add(type == 5 ? "(COM handler)" : "(other action)");
        }

        var triggers = new List<string>();
        bool logonBoot = false;
        dynamic trs = def.Triggers;
        int tc = trs.Count;
        for (int i = 1; i <= tc; i++)
        {
            int type = trs[i].Type;
            if (type is 8 or 9) logonBoot = true;
            triggers.Add(type switch
            {
                0 => "On event", 1 => "One time", 2 => "Daily", 3 => "Weekly", 4 => "Monthly", 5 => "Monthly",
                6 => "On idle", 7 => "On registration", 8 => "At startup", 9 => "At logon", 11 => "On session change",
                _ => "Other",
            });
        }

        string author = "";
        try { author = def.RegistrationInfo.Author ?? ""; } catch { }
        DateTime last = default;
        try { last = task.LastRunTime; } catch { }

        string expandedExe = Environment.ExpandEnvironmentVariables(exe.Trim('"'));
        return new TaskItem
        {
            Name = task.Name,
            FolderPath = folderPath,
            Author = author.StartsWith("$(") ? "" : author,
            Triggers = string.Join(", ", triggers.Distinct()),
            Action = string.Join(" | ", actions),
            ExePath = string.IsNullOrEmpty(expandedExe) ? null : (Path.IsPathRooted(expandedExe) ? expandedExe : Shell.ExtractExePath(expandedExe)),
            Arguments = args,
            LastRun = last.Year < 2000 ? "Never" : last.ToString("g"),
            IsMicrosoft = folderPath.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase),
            RunsAtLogonOrBoot = logonBoot,
            Enabled = task.Enabled,
        };
    }

    /// <summary>Deletes a task and returns its XML definition, so it can be registered again (undo).</summary>
    public static string Delete(TaskItem item)
    {
        dynamic svc = Connect();
        dynamic folder = svc.GetFolder(item.FolderPath);
        string xml = folder.GetTask(item.Name).Xml;
        folder.DeleteTask(item.Name, 0);
        return xml;
    }

    /// <summary>Registers a task again from a saved XML definition.</summary>
    public static void Restore(string folderPath, string name, string xml)
    {
        dynamic svc = Connect();
        dynamic folder;
        try { folder = svc.GetFolder(folderPath); }
        catch { folder = svc.GetFolder("\\").CreateFolder(folderPath, null); }
        // 6 = create or update; logon type 3 = run as the user/principal stored in the XML (5 = service account like SYSTEM)
        int logon = xml.Contains("S-1-5-18") || xml.Contains("<UserId>SYSTEM</UserId>") ? 5 : 3;
        folder.RegisterTask(name, xml, 6, null, null, logon, null);
    }

    public static void SetEnabled(TaskItem item, bool enabled, bool log = true)
    {
        dynamic svc = Connect();
        dynamic folder = svc.GetFolder(item.FolderPath);
        dynamic task = folder.GetTask(item.Name);
        task.Enabled = enabled;
        item.Enabled = enabled;
        if (log)
            History.Add(ChangeKind.TaskToggle, $"{(enabled ? "Enabled" : "Disabled")} scheduled task \"{item.Name}\"",
                item.FullPath, History.Data(("name", item.Name), ("folder", item.FolderPath), ("enabled", enabled)));
    }
}
