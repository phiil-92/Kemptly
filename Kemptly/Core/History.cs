using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kemptly.Core;

public enum ChangeKind
{
    StartupToggle, StartupRemoved, TaskToggle, ServiceStartType, ServiceStartStop,
    FilesCleaned, FilesDeleted, AppUninstalled, BloatwareRemoved, ProcessEnded,
    PrivacyTweak, FirewallBlock, ScheduledClean, Hibernation, Other, LeftoversRemoved,
}

public class ChangeEntry : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Time { get; set; } = DateTime.Now;
    public ChangeKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public Dictionary<string, string> Data { get; set; } = new();

    bool _undone;
    public bool Undone { get => _undone; set { if (Set(ref _undone, value)) { Raise(nameof(CanUndo)); Raise(nameof(StateText)); } } }

    [JsonIgnore] public string TimeText => Time.ToString("g");
    [JsonIgnore] public bool CanUndo => !Undone && History.UndoLabel(this) != null;
    [JsonIgnore] public string UndoText => History.UndoLabel(this) ?? "";
    [JsonIgnore] public string StateText => Undone ? "Undone" : "";
}

/// <summary>Log of every change Kemptly makes, with undo where possible. Stored in %LOCALAPPDATA%\Kemptly\history.json.</summary>
public static class History
{
    const int MaxEntries = 500;
    static readonly object Lock = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    static string FilePath => Path.Combine(AppSettings.DataDir, "history.json");
    static List<ChangeEntry>? _entries;

    public static event Action? Changed;

    static List<ChangeEntry> Entries
    {
        get
        {
            if (_entries != null) return _entries;
            try
            {
                if (File.Exists(FilePath))
                    _entries = JsonSerializer.Deserialize<List<ChangeEntry>>(File.ReadAllText(FilePath), Json);
            }
            catch { }
            return _entries ??= [];
        }
    }

    public static IReadOnlyList<ChangeEntry> All
    {
        get { lock (Lock) return Entries.OrderByDescending(e => e.Time).ToList(); }
    }

    public static ChangeEntry Add(ChangeKind kind, string title, string detail = "", Dictionary<string, string>? data = null)
    {
        var e = new ChangeEntry { Kind = kind, Title = title, Detail = detail, Data = data ?? new() };
        lock (Lock)
        {
            Entries.Add(e);
            if (Entries.Count > MaxEntries) Entries.RemoveRange(0, Entries.Count - MaxEntries);
            Save();
        }
        Changed?.Invoke();
        return e;
    }

    public static void Clear()
    {
        lock (Lock) { Entries.Clear(); Save(); }
        Changed?.Invoke();
    }

    public static void Save()
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Entries, Json));
            }
            catch { }
        }
    }

    static string D(ChangeEntry e, string key) => e.Data.GetValueOrDefault(key, "");

    /// <summary>Button text for undo, or null if this change can't be undone.</summary>
    public static string? UndoLabel(ChangeEntry e) => e.Kind switch
    {
        ChangeKind.StartupToggle or ChangeKind.TaskToggle or ChangeKind.ServiceStartType
            or ChangeKind.PrivacyTweak or ChangeKind.FirewallBlock or ChangeKind.ScheduledClean => "Undo",
        ChangeKind.StartupRemoved => D(e, "shortcut") != "" ? "Open Recycle Bin" : "Undo",
        ChangeKind.Hibernation => D(e, "enabled") == "False" ? "Turn back on" : null,
        ChangeKind.BloatwareRemoved => D(e, "familyName") != "" ? "Reinstall from Store" : null,
        ChangeKind.FilesDeleted => D(e, "recycled") == "True" ? "Open Recycle Bin" : null,
        ChangeKind.LeftoversRemoved => D(e, "reg") != "" || D(e, "tasks") != "" ? "Undo"
                                     : D(e, "recycled") == "True" ? "Open Recycle Bin" : null,
        _ => null,
    };

    /// <summary>Reverts a change. Throws on failure (e.g. access denied).</summary>
    public static async Task<string> UndoAsync(ChangeEntry e)
    {
        string result;
        switch (e.Kind)
        {
            case ChangeKind.StartupToggle:
            {
                bool wasEnabled = D(e, "enabled") == "True";
                var item = StartupManager.Load().FirstOrDefault(i => i.Name == D(e, "name") && i.Source.ToString() == D(e, "source"))
                           ?? throw new InvalidOperationException("That startup entry no longer exists.");
                StartupManager.SetEnabled(item, !wasEnabled, log: false);
                result = $"\"{item.DisplayName}\" is {(wasEnabled ? "disabled" : "enabled")} again.";
                break;
            }
            case ChangeKind.StartupRemoved when D(e, "shortcut") != "":
                Shell.Open("shell:RecycleBinFolder");
                return "The shortcut is in the Recycle Bin: right-click it and choose Restore.";
            case ChangeKind.StartupRemoved:
                StartupManager.RestoreRunValue(Enum.Parse<StartupSource>(D(e, "source")), D(e, "name"), D(e, "command"));
                result = $"Startup entry \"{D(e, "name")}\" restored.";
                break;
            case ChangeKind.TaskToggle:
            {
                bool wasEnabled = D(e, "enabled") == "True";
                await Task.Run(() => TaskSchedulerReader.SetEnabled(new TaskItem { Name = D(e, "name"), FolderPath = D(e, "folder") }, !wasEnabled, log: false));
                result = $"Task \"{D(e, "name")}\" is {(wasEnabled ? "disabled" : "enabled")} again.";
                break;
            }
            case ChangeKind.ServiceStartType:
                ServiceManager.SetStartType(new ServiceItem { Name = D(e, "name"), DisplayName = D(e, "name") }, D(e, "previous"), log: false);
                result = $"Service \"{D(e, "name")}\" is set back to {D(e, "previous")}.";
                break;
            case ChangeKind.PrivacyTweak:
                PrivacyTweaks.Restore(e.Data);
                result = $"\"{e.Title}\" reverted.";
                break;
            case ChangeKind.FirewallBlock:
                if (!await Firewall.UnblockAsync(D(e, "rule"))) throw new InvalidOperationException("The firewall rule could not be removed (administrator permission needed).");
                result = $"{D(e, "app")} can use the internet again.";
                break;
            case ChangeKind.ScheduledClean:
                if (D(e, "enabled") == "True") ScheduledClean.Disable(); else ScheduledClean.Enable();
                result = "Weekly cleaning setting reverted.";
                break;
            case ChangeKind.Hibernation:
                if (await Shell.RunElevatedAsync("powercfg.exe", "/h on", false) != 0) throw new InvalidOperationException("Hibernation could not be turned on (administrator permission needed).");
                result = "Hibernation is turned on again.";
                break;
            case ChangeKind.BloatwareRemoved:
                Shell.Open($"ms-windows-store://pdp/?PFN={D(e, "familyName")}");
                return "The Microsoft Store opened on that app. Click Install to get it back.";
            case ChangeKind.FilesDeleted:
                Shell.Open("shell:RecycleBinFolder");
                return "The files are in the Recycle Bin: select them, right-click and choose Restore.";
            case ChangeKind.LeftoversRemoved when D(e, "reg") == "" && D(e, "tasks") == "":
                Shell.Open("shell:RecycleBinFolder");
                return "The files are in the Recycle Bin: select them, right-click and choose Restore.";
            case ChangeKind.LeftoversRemoved:
                result = await Leftovers.UndoAsync(e.Data);
                break;
            default:
                throw new InvalidOperationException("This change can't be undone.");
        }
        e.Undone = true;
        Save();
        Changed?.Invoke();
        return result;
    }

    // Small helpers so call sites stay short.
    public static Dictionary<string, string> Data(params (string Key, object? Value)[] items) =>
        items.ToDictionary(i => i.Key, i => i.Value?.ToString() ?? "");
}
