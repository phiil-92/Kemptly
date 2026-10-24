using System.Windows;
using System.Windows.Controls;
using Kemptly.Core;

namespace Kemptly.Views;

public partial class PrivacyPage : UserControl
{
    List<PrivacyTweak> _tweaks = [];

    public PrivacyPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    void Refresh()
    {
        _tweaks = PrivacyTweaks.Create();
        foreach (var t in _tweaks) t.Applied = PrivacyTweaks.IsApplied(t);
        TweakList.ItemsSource = _tweaks;
        StatusText.Text = $"{_tweaks.Count(t => t.Applied)} of {_tweaks.Count} privacy settings are on.";
    }

    bool TrySet(PrivacyTweak t, bool on)
    {
        try
        {
            PrivacyTweaks.Set(t, on);
            return true;
        }
        catch (Exception ex)
        {
            t.Raise(nameof(PrivacyTweak.Applied));
            if (Admin.IsAccessDenied(ex)) Admin.Require($"\"{t.Title}\"");
            else Ui.Error(ex.Message);
            return false;
        }
    }

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        var t = Ui.Item<PrivacyTweak>(sender);
        if (t == null) return;
        bool on = ((CheckBox)sender).IsChecked == true;
        if (TrySet(t, on))
            StatusText.Text = on ? $"\"{t.Title}\" is on. {t.Note}" : $"\"{t.Title}\" is back to the Windows default.";
    }

    void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        var todo = _tweaks.Where(t => !t.Applied && (!t.NeedsAdmin || Admin.IsAdmin)).ToList();
        if (todo.Count == 0)
        {
            StatusText.Text = Admin.IsAdmin ? "All privacy settings are already on." : "All settings that don't need admin rights are already on.";
            return;
        }
        if (AppSettings.Current.ConfirmChanges &&
            !Ui.Ask($"Turn on these {todo.Count} privacy settings?\n\n" + string.Join("\n", todo.Select(t => "  - " + t.Title)) +
                    "\n\nEach one can be undone on the History page.", "Privacy")) return;
        int ok = todo.Count(t => TrySet(t, true));
        StatusText.Text = $"Turned on {ok} setting(s)." + (Admin.IsAdmin ? "" : " Settings marked \"needs admin\" were skipped.") +
                          " Some take effect after restarting Explorer or signing out.";
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
}
