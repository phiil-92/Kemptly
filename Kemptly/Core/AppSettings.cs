using System.Text.Json;

namespace Kemptly.Core;

/// <summary>User preferences, stored in %LOCALAPPDATA%\Kemptly\settings.json.</summary>
public class AppSettings
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kemptly");

    static string FilePath => Path.Combine(DataDir, "settings.json");

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Files deleted in the Disk Analyzer / Duplicates go to the Recycle Bin by default.</summary>
    public bool RecycleByDefault { get; set; } = true;

    /// <summary>Ask before reversible changes (toggles). Deleting files always asks.</summary>
    public bool ConfirmChanges { get; set; } = true;

    /// <summary>Opt-in: ask GitHub for a newer Kemptly version when the app starts.</summary>
    public bool CheckForUpdatesOnStart { get; set; }

    public static AppSettings Current { get; } = Load();

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
