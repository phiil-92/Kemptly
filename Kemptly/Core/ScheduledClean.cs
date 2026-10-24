namespace Kemptly.Core;

/// <summary>
/// Optional weekly junk clean via Windows Task Scheduler. Off by default; runs "Kemptly.exe --clean-silent"
/// as the current user (no admin), only for the safe, default-selected categories.
/// </summary>
public static class ScheduledClean
{
    const string TaskName = "Kemptly weekly clean";

    static dynamic Connect()
    {
        var t = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler not available.");
        dynamic svc = Activator.CreateInstance(t)!;
        svc.Connect();
        return svc;
    }

    public static bool IsEnabled
    {
        get
        {
            try
            {
                dynamic svc = Connect();
                dynamic task = svc.GetFolder("\\").GetTask(TaskName);
                return (bool)task.Enabled;
            }
            catch { return false; }
        }
    }

    public static void Enable()
    {
        dynamic svc = Connect();
        dynamic def = svc.NewTask(0);
        def.RegistrationInfo.Description = "Weekly junk file clean by Kemptly (turn off in Kemptly > Junk Cleaner).";
        def.Settings.StartWhenAvailable = true;
        def.Settings.DisallowStartIfOnBatteries = true;
        def.Settings.ExecutionTimeLimit = "PT30M";
        def.Principal.LogonType = 3;   // interactive token: runs as you, only while signed in
        def.Principal.RunLevel = 0;    // no admin rights
        dynamic trigger = def.Triggers.Create(3); // weekly
        trigger.StartBoundary = "2026-01-04T12:00:00";
        trigger.DaysOfWeek = 1;        // Sunday
        trigger.WeeksInterval = 1;
        dynamic action = def.Actions.Create(0);
        action.Path = Environment.ProcessPath;
        action.Arguments = "--clean-silent";
        svc.GetFolder("\\").RegisterTaskDefinition(TaskName, def, 6, null, null, 3, null);
    }

    public static void Disable()
    {
        dynamic svc = Connect();
        try { svc.GetFolder("\\").DeleteTask(TaskName, 0); } catch { }
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled) Enable(); else Disable();
        History.Add(ChangeKind.ScheduledClean, enabled ? "Turned on weekly automatic cleaning" : "Turned off weekly automatic cleaning",
            enabled ? "Every Sunday at 12:00 (or the next time you're signed in)" : "", History.Data(("enabled", enabled)));
    }

    /// <summary>The scheduled run: cleans the default, no-admin categories and logs the result.</summary>
    public static void RunSilent()
    {
        long freed = 0;
        int deleted = 0;
        foreach (var c in Cleaner.CreateCategories().Where(c => c.IsSelected && !c.RequiresAdmin && !c.IsRecycleBin))
        {
            try
            {
                var (f, d, _) = Cleaner.Clean(c, CancellationToken.None);
                freed += f;
                deleted += d;
            }
            catch { }
        }
        History.Add(ChangeKind.FilesCleaned, $"Weekly automatic clean freed {Fmt.Bytes(freed)}", $"{deleted} files removed");
    }
}
