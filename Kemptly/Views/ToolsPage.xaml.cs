using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Kemptly.Core;

namespace Kemptly.Views;

public record ToolDef(string Icon, string Title, string Description, string ButtonText, bool NeedsAdmin, Func<Task> Run);

public partial class ToolsPage : UserControl
{
    public ToolsPage()
    {
        InitializeComponent();
        ToolList.ItemsSource = CreateTools();

        (string Label, string Target)[] links =
        [
            ("Task Manager", "taskmgr.exe"), ("Device Manager", "devmgmt.msc"), ("Event Viewer", "eventvwr.msc"),
            ("Services", "services.msc"), ("Task Scheduler", "taskschd.msc"), ("Reliability history", "perfmon.exe /rel"),
            ("Resource Monitor", "resmon.exe"), ("System Information", "msinfo32.exe"), ("Windows Update", "ms-settings:windowsupdate"),
            ("Windows Security", "windowsdefender:"), ("Storage settings", "ms-settings:storagesense"), ("Disk Management", "diskmgmt.msc"),
        ];
        foreach (var (label, target) in links)
        {
            var btn = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(14, 6, 14, 6) };
            btn.Click += (_, _) =>
            {
                var parts = target.Split(' ', 2);
                Shell.Open(parts[0], parts.Length > 1 ? parts[1] : null);
            };
            LinkPanel.Children.Add(btn);
        }
    }

    void Log(string text)
    {
        LogBox.AppendText($"[{DateTime.Now:T}] {text.Trim()}\n");
        LogBox.ScrollToEnd();
    }

    static string HiberfilInfo()
    {
        try
        {
            var f = new FileInfo(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "hiberfil.sys"));
            return f.Exists ? $"hiberfil.sys currently uses {Fmt.Bytes(f.Length)}." : "Hibernation appears to be off already.";
        }
        catch { return ""; }
    }

    async Task Elevated(string title, string file, string args, bool visible)
    {
        Log($"{title}: starting…");
        int code = await Shell.RunElevatedAsync(file, args, visible);
        Log(code switch
        {
            -2 => $"{title}: cancelled (administrator permission was not given).",
            0 when visible => $"{title}: opened in a separate window - follow its progress there.",
            0 => $"{title}: done.",
            _ => $"{title}: finished with exit code {code}.",
        });
    }

    List<ToolDef> CreateTools() =>
    [
        new("", "Flush DNS cache", "Fixes websites that won't load or point to an old address after a network change.", "Flush", false, async () =>
        {
            var r = await Shell.RunAsync("ipconfig", "/flushdns");
            Log(r.All);
        }),

        new("", "Restart Explorer", "Fixes a frozen taskbar, Start menu or desktop without rebooting.", "Restart", false, async () =>
        {
            foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); } catch { } p.Dispose(); }
            // Windows restarts the shell by itself, with normal rights. Starting it from here instead
            // would give the desktop this app's rights, so when elevated we only wait for Windows.
            for (int i = 0; i < 10 && Process.GetProcessesByName("explorer").Length == 0; i++) await Task.Delay(500);
            if (Process.GetProcessesByName("explorer").Length > 0) Log("Windows Explorer restarted.");
            else if (!Admin.IsAdmin) { Process.Start(Shell.SystemTool("explorer.exe")); Log("Windows Explorer restarted."); }
            else Log("Explorer didn't come back on its own. Press Ctrl+Shift+Esc → Run new task → explorer.exe (leave 'administrative privileges' unticked).");
        }),

        new("", "Empty Recycle Bin", "Permanently removes everything in the Recycle Bin on all drives.", "Empty", false, () =>
        {
            if (!Ui.Confirm("Permanently delete everything in the Recycle Bin?")) return Task.CompletedTask;
            var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Native.SHQUERYRBINFO>() };
            Native.SHQueryRecycleBin(null, ref info);
            Native.SHEmptyRecycleBin(IntPtr.Zero, null, 1 | 2 | 4);
            Log($"Recycle Bin emptied ({Fmt.Bytes(info.i64Size)} freed).");
            return Task.CompletedTask;
        }),

        new("", "Turn off hibernation", $"Deletes hiberfil.sys, which can use many GB. Also disables Fast Startup. {HiberfilInfo()}", "Turn off", true, async () =>
        {
            if (!Ui.Ask("Turn off hibernation? (You can turn it back on with: powercfg /h on)")) return;
            await Elevated("Turn off hibernation", "powercfg.exe", "/h off", false);
        }),

        new("", "Clean component store", "Removes superseded Windows update files from WinSxS with DISM. Can free several GB; takes a few minutes.", "Clean up", true,
            () => Elevated("Component store cleanup", "cmd.exe", "/k DISM /Online /Cleanup-Image /StartComponentCleanup", true)),

        new("", "Repair system files", "Runs System File Checker (sfc /scannow) to find and repair corrupted Windows files.", "Run SFC", true,
            () => Elevated("System File Checker", "cmd.exe", "/k sfc /scannow", true)),

        new("", "Repair Windows image", "Runs DISM RestoreHealth to repair the Windows image. Run this if SFC can't fix something.", "Run DISM", true,
            () => Elevated("DISM RestoreHealth", "cmd.exe", "/k DISM /Online /Cleanup-Image /RestoreHealth", true)),

        new("", "Create restore point", "Saves a System Restore point so you can roll back if a change goes wrong. (Windows allows one per 24 hours.)", "Create", true, () =>
        {
            const string ps = """
                try { Checkpoint-Computer -Description 'Kemptly restore point' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop; Write-Host 'Restore point created.' -ForegroundColor Green }
                catch { Write-Host "Could not create a restore point: $_" -ForegroundColor Red; Write-Host 'Make sure System Protection is turned on for drive C: (search "Create a restore point").' }
                Read-Host 'Press Enter to close'
                """;
            return Elevated("Restore point", "powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {Shell.EncodePowerShell(ps)}", true);
        }),

        new("", "Reset network", "Resets Winsock and TCP/IP settings. Fixes stubborn connection problems. Requires a restart afterwards.", "Reset", true, async () =>
        {
            if (!Ui.Confirm("Reset network settings? You will need to restart your PC afterwards, and VPN adapters may need reinstalling.")) return;
            await Elevated("Network reset", "cmd.exe", "/k netsh winsock reset && netsh int ip reset && ipconfig /flushdns && echo. && echo Done. Please restart your PC.", true);
        }),

        new("", "Battery report", "Creates a detailed report of your laptop battery's health and capacity history.", "Create report", false, async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "battery-report.html");
            var r = await Shell.RunAsync("powercfg", $"/batteryreport /output \"{path}\"");
            Log(r.All);
            if (File.Exists(path) && r.Code == 0) Shell.Open(path);
        }),

        new("", "Energy report", "Analyses your system for 60 seconds and reports power-efficiency problems.", "Analyse", true, () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "energy-report.html");
            return Elevated("Energy report", "cmd.exe", $"/c powercfg /energy /output \"{path}\" && start \"\" \"{path}\"", true);
        }),

        new("", "Classic Disk Cleanup", "Windows' own cleaner - useful for removing an old Windows installation (Windows.old) after an upgrade.", "Open", false, () =>
        {
            Shell.Open("cleanmgr.exe");
            Log("Disk Cleanup opened. Tip: click 'Clean up system files' to see Windows.old and update leftovers.");
            return Task.CompletedTask;
        }),
    ];

    async void Run_Click(object sender, RoutedEventArgs e)
    {
        var tool = Ui.Item<ToolDef>(sender);
        if (tool == null) return;
        var btn = (Button)sender;
        btn.IsEnabled = false;
        try { await tool.Run(); }
        catch (Exception ex) { Log($"{tool.Title} failed: {ex.Message}"); }
        finally { btn.IsEnabled = true; }
    }
}
