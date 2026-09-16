using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CleanMaster.App;
using Xunit;

namespace CleanMaster.App.Tests;

/// <summary>GUI 冒烟测试：headless 模式下验证窗口渲染、按钮接线与核心交互。</summary>
public class GuiSmokeTests
{
    public GuiSmokeTests()
    {
        // 隔离数据目录，避免污染真实用户数据
        Environment.SetEnvironmentVariable("CC5_DATA_DIR", "/tmp/cc5-gui-test");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/tmp/cc5-gui-test-xdg");
    }

    private static TabControl FindTabControl(Window window)
        => window.GetVisualDescendants().OfType<TabControl>().First();

    private static ItemsControl? CatListOf(Window window)
        => window.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(i => i.Name == "CatList");

    private static TextBlock? StatusOf(Window window)
        => window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "StatusText");

    private static TextBlock? BackupStatusOf(Window window)
        => window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "BackupStatus");

    private static Button? FindButton(Window window, string content)
        => window.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Content?.ToString() == content);

    private static async Task PumpUntil(Func<bool> cond, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(120);
        }
    }

    [AvaloniaFact]
    public void Window_Renders_With_Six_Tabs()
    {
        var window = new MainWindow();
        window.Show();

        var tabs = FindTabControl(window);
        Assert.Equal(6, tabs.Items.Count);
        Assert.Equal("扫描清理", ((TabItem)tabs.Items[0]!).Header?.ToString());
        Assert.Equal("备份区", ((TabItem)tabs.Items[1]!).Header?.ToString());
        Assert.Equal("台账", ((TabItem)tabs.Items[2]!).Header?.ToString());
        Assert.Equal("规则", ((TabItem)tabs.Items[3]!).Header?.ToString());
        Assert.Equal("自检", ((TabItem)tabs.Items[4]!).Header?.ToString());
        Assert.Equal("关于", ((TabItem)tabs.Items[5]!).Header?.ToString());
        Assert.NotNull(FindButton(window, "开始扫描"));
        Assert.NotNull(FindButton(window, "清理选中…"));

        window.Close();
    }

    [AvaloniaFact]
    public async Task Scan_Click_Produces_Category_List()
    {
        var window = new MainWindow();
        window.Show();

        var btn = FindButton(window, "开始扫描");
        Assert.NotNull(btn);
        btn!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await PumpUntil(() =>
        {
            var catList = CatListOf(window);
            return catList?.ItemsSource is System.Collections.IEnumerable src
                   && src.Cast<object?>().Any();
        });

        await PumpUntil(() => (StatusOf(window)?.Text ?? "").Contains("扫描完成"));

        var items = CatListOf(window)!.ItemsSource!
            .Cast<CategoryItemVM>().ToList();
        Assert.NotEmpty(items);
        Assert.All(items, it => Assert.False(string.IsNullOrEmpty(it.Name)));
        Assert.Contains(items, it => !string.IsNullOrEmpty(it.SizeText));

        var status = StatusOf(window)!.Text ?? "";
        Assert.Contains("扫描完成", status);

        window.Close();
    }

    [AvaloniaFact]
    public async Task SelectRecommended_And_Select_All_Work()
    {
        var window = new MainWindow();
        window.Show();

        var scanBtn = FindButton(window, "开始扫描")!;
        scanBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await PumpUntil(() =>
        {
            var l = CatListOf(window);
            return l?.ItemsSource is System.Collections.IEnumerable s && s.Cast<object?>().Any();
        });

        var list = CatListOf(window)!;
        var cats = list.ItemsSource!.Cast<CategoryItemVM>().ToList();

        // 全选推荐
        FindButton(window, "全选推荐")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.All(cats.Where(c => c.Recommend), c => Assert.True(c.IsChecked));

        // 全不选
        FindButton(window, "全不选")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.All(cats, c => Assert.False(c.IsChecked));

        window.Close();
    }

    [AvaloniaFact]
    public async Task SelfTest_Click_Produces_Results()
    {
        var window = new MainWindow();
        window.Show();

        var tab = ((TabItem)FindTabControl(window).Items[4]!);
        tab.IsSelected = true;
        Dispatcher.UIThread.RunJobs();

        var btn = FindButton(window, "运行自检");
        Assert.NotNull(btn);
        btn!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await PumpUntil(() =>
        {
            var l = window.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(i => i.Name == "SelftestList");
            return l?.ItemsSource is System.Collections.IEnumerable s && s.Cast<object?>().Any();
        });

        var results = window.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "SelftestList").ItemsSource!
            .Cast<SelftestItemVM>().ToList();
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Passed);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Backup_And_Ledger_Tabs_Load()
    {
        var window = new MainWindow();
        window.Show();

        var tabs = FindTabControl(window);

        ((TabItem)tabs.Items[1]!).IsSelected = true;
        Dispatcher.UIThread.RunJobs();
        FindButton(window, "刷新")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        var backupStatus = BackupStatusOf(window)!.Text ?? "";
        Assert.Contains("备份区", backupStatus);

        ((TabItem)tabs.Items[2]!).IsSelected = true;
        Dispatcher.UIThread.RunJobs();
        FindButton(window, "刷新")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Assert.NotNull(FindButton(window, "导出 CSV…"));
        Assert.NotNull(FindButton(window, "导出 HTML 报告…"));

        window.Close();
    }
}
