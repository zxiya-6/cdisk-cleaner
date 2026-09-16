using System.Diagnostics;
using System.Windows;
using CleanMaster.Core;
using CleanMaster.Core.Config;
using CleanMaster.Core.Util;

namespace CleanMaster.App;

public partial class MainWindow : Window
{
    private Action? _cancelHandler;

    public MainWindow()
    {
        AppServices.Initialize();
        InitializeComponent();

        WireShell();
        RefreshDisk();
        RefreshRulesState();
        AppServices.Current.DiskStatsChanged += () => Dispatcher.Invoke(RefreshDisk);

        Ui.Every(TimeSpan.FromSeconds(30), RefreshDisk);

        AppLog.Info("主窗口已打开");
    }

    private void WireShell()
    {
        UiShell.SetStatus = s => TxtStatus.Text = s;
        UiShell.SetSummary = s => TxtSummary.Text = s;

        UiShell.SetProgress = (percent, indeterminate) =>
        {
            if (percent < 0 && !indeterminate)
            {
                BarProgress.Visibility = Visibility.Collapsed;
                return;
            }

            BarProgress.Visibility = Visibility.Visible;
            BarProgress.IsIndeterminate = indeterminate;
            if (!indeterminate) BarProgress.Value = Math.Clamp(percent, 0, 100);
        };

        UiShell.SetBusy = busy =>
        {
            BtnCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!busy)
            {
                BarProgress.Visibility = Visibility.Collapsed;
                _cancelHandler = null;
            }
        };

        UiShell.SetCancel = action => _cancelHandler = action;
    }

    private void RefreshRulesState()
    {
        var s = AppServices.Current;
        if (s.RulesError == null && s.Library != null)
        {
            var cats = s.Library.Categories.Count(c => c.Enabled);
            TxtRulesState.Text = $"规则库：已加载 {cats} 个清理类别";
            TxtRulesState.Foreground = (System.Windows.Media.Brush)FindResource("Text3Brush");
        }
        else
        {
            TxtRulesState.Text = "规则库加载失败：" + (s.RulesError ?? "未知错误") + "（请到「工具」页检查）";
            TxtRulesState.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }

        TxtDataDir.Text = "数据目录：" + AppPaths.DataRoot;
    }

    private void RefreshDisk()
    {
        var free = SystemInfo.GetFreeBytes("C");
        var total = SystemInfo.GetTotalBytes("C");
        if (total <= 0)
        {
            TxtDisk.Text = "C: 读取失败";
            return;
        }

        var used = total - free;
        var usedPct = used * 100.0 / total;
        TxtDisk.Text = $"C: 总 {HumanSize.Format(total)} · 可用 {HumanSize.Format(free)}";
        BarDisk.Value = usedPct;
        BarDisk.Foreground = usedPct switch
        {
            >= 90 => (System.Windows.Media.Brush)FindResource("DangerBrush"),
            >= 75 => (System.Windows.Media.Brush)FindResource("WarnBrush"),
            _ => (System.Windows.Media.Brush)FindResource("SuccessBrush"),
        };
    }

    private void BtnRefreshDisk_Click(object sender, RoutedEventArgs e) => RefreshDisk();

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _cancelHandler?.Invoke(); } catch { }
    }

    private void BtnElevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
            });
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AppLog.Exception("提权重启失败", ex);
            Ui.Warn("未能以管理员身份重启（可能被取消）。\n\n" + ex.Message);
        }
    }

    public void ShowRulesState() => RefreshRulesState();

    public void SelectTab(int index) => MainTabs.SelectedIndex = index;

    private void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;
        switch (MainTabs.SelectedIndex)
        {
            case 2:
                BackupViewCtl.Refresh();
                break;
            case 3:
                HistoryViewCtl.Refresh();
                break;
        }
    }
}
