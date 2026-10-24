using System.Windows;
using System.Windows.Controls;
using Kemptly.Core;

namespace Kemptly.Views;

public partial class CrashHistoryView : UserControl
{
    bool _loaded;

    public CrashHistoryView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible && !_loaded) { _loaded = true; Refresh(); } };
    }

    async void Refresh()
    {
        var list = await Task.Run(() => CrashHistory.Load(30));
        CrashGrid.ItemsSource = list;
        BsodCount.Text = list.Count(c => c.Kind == CrashKind.BlueScreen).ToString();
        ShutdownCount.Text = list.Count(c => c.Kind == CrashKind.UnexpectedShutdown).ToString();
        var apps = list.Where(c => c.Kind is CrashKind.AppCrash or CrashKind.AppHang).ToList();
        AppCount.Text = apps.Count.ToString();
        var top = apps.GroupBy(c => c.Title.Split(' ')[0]).OrderByDescending(g => g.Count()).FirstOrDefault();
        TopApp.Text = top != null && top.Count() > 1 ? $"Most often: {top.Key} ({top.Count()}x)" : "";
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    void Reliability_Click(object sender, RoutedEventArgs e) => Shell.Open("perfmon.exe", "/rel");
}
