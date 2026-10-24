using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace Kemptly.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    public void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class AppInfo
{
    public const string RepoUrl = "https://github.com/phiil-92/Kemptly";

    /// <summary>"1.2.0" (from the assembly's informational version, without the "+commit" suffix).</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];
}

public static class Fmt
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {Units[i]}";
    }

    public static string Bytes(ulong bytes) => Bytes((long)bytes);
}

public static class Admin
{
    public static bool IsAdmin { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Win32Exception) { /* UAC prompt cancelled */ }
    }

    /// <summary>Returns true if elevated; otherwise offers to restart elevated and returns false.</summary>
    public static bool Require(string what)
    {
        if (IsAdmin) return true;
        if (Ui.Ask($"{what} needs administrator rights.\n\nRestart Kemptly as administrator now?", "Administrator required"))
            RestartElevated();
        return false;
    }

    public static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException or System.Security.SecurityException
        || ex.HResult == unchecked((int)0x80070005)
        || ex.InnerException is { } inner && IsAccessDenied(inner);
}

public static class Ui
{
    static Window? Owner => Application.Current?.MainWindow;

    public static bool Ask(string msg, string title = "Please confirm") =>
        MessageBox.Show(Owner!, msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static bool Confirm(string msg, string title = "Please confirm") =>
        MessageBox.Show(Owner!, msg, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public static void Error(string msg) =>
        MessageBox.Show(Owner!, msg, "Kemptly", MessageBoxButton.OK, MessageBoxImage.Error);

    public static void Info(string msg) =>
        MessageBox.Show(Owner!, msg, "Kemptly", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>Gets the row item behind a button inside a DataGrid/ItemsControl template.</summary>
    public static T? Item<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;
}

public record RunResult(int Code, string StdOut, string StdErr)
{
    public string All => (StdOut + "\n" + StdErr).Trim();
}

public static class Shell
{
    public static Encoding OemEncoding
    {
        get
        {
            try { return Encoding.GetEncoding((int)Native.GetOEMCP()); }
            catch { return Encoding.UTF8; }
        }
    }

    static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// Resolves a Windows tool ("cmd.exe", "devmgmt.msc", ...) to its full path in the Windows folder.
    /// Bare names would otherwise be searched for in the app's own (user-writable) folder first,
    /// letting a planted fake run with this app's rights. URIs like "ms-settings:" pass through.
    /// </summary>
    public static string SystemTool(string name)
    {
        if (Path.IsPathRooted(name) || name.Contains(':')) return name;
        if (!Path.HasExtension(name)) name += ".exe";
        foreach (var dir in new[] { Environment.SystemDirectory, WinDir, Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0") })
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }
        throw new FileNotFoundException($"Windows tool not found: {name}");
    }

    public static void ShowInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var explorer = SystemTool("explorer.exe");
            if (File.Exists(path)) Process.Start(explorer, $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start(explorer, $"\"{path}\"");
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null && Directory.Exists(dir)) Process.Start(explorer, $"\"{dir}\"");
                else Ui.Info("That location no longer exists.");
            }
        }
        catch (Exception ex) { Ui.Error(ex.Message); }
    }

    public static void Open(string target, string? args = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(SystemTool(target))
            {
                UseShellExecute = true,
                Arguments = args ?? "",
                WorkingDirectory = Environment.SystemDirectory,
            });
        }
        catch (Exception ex) { Ui.Error($"Could not open {target}:\n{ex.Message}"); }
    }

    /// <summary>Sends a file or folder to the Recycle Bin.</summary>
    public static bool Recycle(string path)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
        var op = new Native.SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0", // marshaller adds the second terminating null
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return Native.SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path) && !Directory.Exists(path);
    }

    public static async Task<RunResult> RunAsync(string file, string args, Encoding? encoding = null)
    {
        var enc = encoding ?? OemEncoding;
        var psi = new ProcessStartInfo(SystemTool(file), args)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.SystemDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = enc,
            StandardErrorEncoding = enc,
        };
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return new RunResult(p.ExitCode, (await outTask).Trim(), (await errTask).Trim());
    }

    /// <summary>Runs a program elevated (UAC prompt if needed). Returns exit code, 0 for visible windows, -2 if cancelled.</summary>
    public static async Task<int> RunElevatedAsync(string file, string args, bool visible)
    {
        // Working directory System32: cmd.exe looks up commands like "sfc" in the current folder first.
        var psi = new ProcessStartInfo(SystemTool(file), args)
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.SystemDirectory,
            Verb = Admin.IsAdmin ? "" : "runas",
            WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return -1;
            if (visible) return 0;
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (Win32Exception) { return -2; }
    }

    public static string EncodePowerShell(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>Runs a PowerShell script and returns its output.</summary>
    public static Task<RunResult> PowerShellAsync(string script)
    {
        var full = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + script;
        return RunAsync("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {EncodePowerShell(full)}", Encoding.UTF8);
    }

    /// <summary>Runs a PowerShell script that writes JSON to stdout and returns the parsed root.</summary>
    public static async Task<JsonElement?> PowerShellJsonAsync(string script)
    {
        var full = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + script;
        var r = await RunAsync("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {EncodePowerShell(full)}", Encoding.UTF8);
        int i = r.StdOut.IndexOfAny(['{', '[']);
        if (i < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.StdOut[i..]);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    /// <summary>Best-effort extraction of the executable path from a command line.</summary>
    public static string? ExtractExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (c.StartsWith(@"\??\")) c = c[4..];
        if (c.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) c = win + c[11..];
        if (c.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) c = Path.Combine(win, c);

        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : c.Trim('"');
        }

        var parts = c.Split(' ');
        var acc = "";
        for (int i = 0; i < parts.Length; i++)
        {
            acc = i == 0 ? parts[0] : acc + " " + parts[i];
            if (File.Exists(acc)) return acc;
            if (File.Exists(acc + ".exe")) return acc + ".exe";
        }

        var first = parts[0];
        if (!first.Contains('\\'))
        {
            var name = first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? first : first + ".exe";
            var sys = Path.Combine(Environment.SystemDirectory, name);
            if (File.Exists(sys)) return sys;
        }
        return first;
    }
}

public static class FileWalker
{
    static readonly EnumerationOptions DirOpts = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // never follow junctions/symlinks
        RecurseSubdirectories = false,
    };

    // Skip cloud placeholders (OneDrive "online-only" files) whose size isn't actually on disk.
    static readonly EnumerationOptions FileOpts = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Offline | (FileAttributes)0x400000 | (FileAttributes)0x40000,
        RecurseSubdirectories = false,
    };

    /// <summary>Walks a directory tree, skipping anything inaccessible instead of aborting.</summary>
    public static IEnumerable<FileInfo> Walk(string root, string pattern = "*", bool recursive = true,
        int maxDepth = int.MaxValue, CancellationToken ct = default)
    {
        var stack = new Stack<(DirectoryInfo Dir, int Depth)>();
        stack.Push((new DirectoryInfo(root), 0));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            FileInfo[] files;
            DirectoryInfo[] subs = [];
            try
            {
                files = dir.GetFiles(pattern, FileOpts);
                if (recursive && depth < maxDepth) subs = dir.GetDirectories("*", DirOpts);
            }
            catch { continue; }
            foreach (var s in subs) stack.Push((s, depth + 1));
            foreach (var f in files) yield return f;
        }
    }
}
