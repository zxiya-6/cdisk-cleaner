using System.Windows.Media;
using System.Windows.Threading;
using CleanMaster.Core.Config;
using CleanMaster.Core.Engine;
using CleanMaster.Core.Models;
using CleanMaster.Core.Rules;
using CleanMaster.Core.Util;

namespace CleanMaster.App;

/// <summary>应用级共享服务：配置、规则库、安全过滤器、磁盘状态等。</summary>
public sealed class AppServices
{
    public static AppServices Current { get; private set; } = new();

    public static void Initialize()
    {
        var s = new AppServices();
        s.Config = ConfigStore.InitRuntime();
        s.ReloadRules();
        Current = s;
    }

    public AppConfig Config { get; private set; } = new();
    public RuleLibrary? Library { get; private set; }
    public SafetyFilter Safety { get; private set; } = SafetyFilter.CreateDefault();
    public string? RulesError { get; private set; }

    public ScanSummary? LastScan { get; set; }

    /// <summary>扫描结果 → 行对象缓存（保持勾选状态与逐项排除）。</summary>
    public Dictionary<string, List<string>> ExclusionMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    public event Action? DiskStatsChanged;

    public BackupZone CreateBackupZone() => new(Config.BackupRoot);

    public void ReloadRules()
    {
        try
        {
            Library = RuleLibraryLoader.Load();
            Safety = RuleLibraryLoader.LoadSafetyFilter();
            RulesError = null;
            AppLog.Info($"规则库已加载：{Library.Categories.Count} 个类别");
        }
        catch (Exception ex)
        {
            Library = null;
            RulesError = ex.Message;
            AppLog.Exception("规则库加载失败", ex);
        }
    }

    public void NotifyDiskStats() => DiskStatsChanged?.Invoke();

    public static Brush RiskBrush(RiskLevel level) => level switch
    {
        RiskLevel.Safe => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x7B, 0x34)),
        RiskLevel.Moderate => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA1, 0x5C, 0x00)),
        _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB3, 0x26, 0x1E)),
    };

    public static string RiskText(RiskLevel level) => level switch
    {
        RiskLevel.Safe => "安全",
        RiskLevel.Moderate => "谨慎",
        _ => "高风险",
    };

    public static string StrategyText(CleanStrategy s) => s switch
    {
        CleanStrategy.Delete => "永久删除",
        CleanStrategy.Backup => "备份区",
        _ => "回收站",
    };
}

/// <summary>界面常用操作。</summary>
public static class Ui
{
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Exception("打开目录失败", ex);
        }
    }

    public static void SelectInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Exception("定位文件失败", ex);
        }
    }

    public static void Info(string message, string title = "提示") =>
        System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Warn(string message, string title = "注意") =>
        System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static bool Ask(string message, string title = "确认") =>
        System.Windows.MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public static void RunOnUi(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;
        if (app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.Invoke(action);
    }

    public static DispatcherTimer Every(TimeSpan interval, Action action)
    {
        var t = new DispatcherTimer { Interval = interval };
        t.Tick += (_, _) => { try { action(); } catch { } };
        t.Start();
        return t;
    }
}
