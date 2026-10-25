using System.Windows;
using System.Windows.Controls;
using Kemptly.Core;

namespace Kemptly.Views;

public partial class SettingsPage : UserControl
{
    static AppSettings S => AppSettings.Current;

    public SettingsPage()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Version}";
        ThemeSystem.IsChecked = S.Theme == "System";
        ThemeLight.IsChecked = S.Theme == "Light";
        ThemeDark.IsChecked = S.Theme == "Dark";
        RecycleCheck.IsChecked = S.RecycleByDefault;
        ConfirmCheck.IsChecked = S.ConfirmChanges;
        AutoUpdateCheck.IsChecked = S.CheckForUpdatesOnStart;
    }

    /// <summary>Applies the saved theme to the whole app.</summary>
    public static void ApplyTheme(string theme)
    {
        Application.Current.ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        S.Theme = ThemeLight.IsChecked == true ? "Light" : ThemeDark.IsChecked == true ? "Dark" : "System";
        S.Save();
        ApplyTheme(S.Theme);
    }

    void Option_Click(object sender, RoutedEventArgs e)
    {
        S.RecycleByDefault = RecycleCheck.IsChecked == true;
        S.ConfirmChanges = ConfirmCheck.IsChecked == true;
        S.CheckForUpdatesOnStart = AutoUpdateCheck.IsChecked == true;
        S.Save();
    }

    async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowBtn.IsEnabled = false;
        UpdateText.Text = "Checking…";
        try
        {
            var info = await UpdateCheck.CheckAsync();
            if (info.IsNewer)
            {
                UpdateText.Text = $"Version {info.Latest} is available.";
                await OfferUpdateAsync(info, s => UpdateText.Text = s);
            }
            else UpdateText.Text = $"You have the latest version ({AppInfo.Version}).";
        }
        catch (Exception ex) { UpdateText.Text = "Couldn't check: " + ex.Message; }
        finally { CheckNowBtn.IsEnabled = true; }
    }

    /// <summary>Asks to install an available update; on yes downloads, verifies and installs it, then restarts Kemptly.</summary>
    public static async Task OfferUpdateAsync(UpdateInfo info, Action<string> report)
    {
        if (!UpdateCheck.CanInstall(info))
        {
            if (Ui.Ask($"Kemptly {info.Latest} is available (you have {AppInfo.Version}). Open the download page?", "Update available"))
                Shell.Open(info.Url);
            return;
        }
        if (!Ui.Ask($"Kemptly {info.Latest} is available (you have {AppInfo.Version}).\n\nInstall it now? Kemptly downloads it from GitHub, " +
                    "checks it, then closes, updates and starts again.", "Update available")) return;
        try
        {
            await UpdateCheck.InstallAsync(info, new Progress<string>(report));
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            report("Update failed.");
            if (Ui.Ask($"The update couldn't be installed:\n{ex.Message}\n\nOpen the download page instead?", "Update failed"))
                Shell.Open(info.Url);
        }
    }

    void Website_Click(object sender, RoutedEventArgs e) => Shell.Open("https://kemptly.app");
    void GitHub_Click(object sender, RoutedEventArgs e) => Shell.Open(AppInfo.RepoUrl);
    void Bug_Click(object sender, RoutedEventArgs e) => Shell.Open(AppInfo.RepoUrl + "/issues/new");

    void DataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.DataDir);
        Shell.ShowInExplorer(AppSettings.DataDir);
    }
}
