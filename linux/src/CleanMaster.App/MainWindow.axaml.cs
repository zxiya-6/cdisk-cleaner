using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CleanMaster.Core;
using CleanMaster.Core.Config;
using CleanMaster.Core.Engine;
using CleanMaster.Core.Ledger;
using CleanMaster.Core.Models;
using CleanMaster.Core.Rules;
using CleanMaster.Core.Selftest;
using CleanMaster.Core.Util;

namespace CleanMaster.App;

// ================= 轻量视图模型基类 =================

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

// ================= 页面条目视图模型 =================

public sealed class CategoryItemVM : ObservableObject
{
    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    public string Name { get; init; } = "";
    public string Note { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string RiskText { get; init; } = "";
    public IBrush RiskBrush { get; init; } = Brushes.Gray;
    public string StrategyText { get; init; } = "";
    public bool RequiresAdmin { get; init; }
    public string CategoryId { get; init; } = "";
    public bool Recommend { get; init; }
    public CleanStrategy Strategy { get; init; }
    public RiskLevel Risk { get; init; }
}

public sealed class BackupItemVM : ObservableObject
{
    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string StateText { get; init; } = "";
    public long FilesRemaining { get; init; }
}

public sealed class LedgerItemVM
{
    public string TimeText { get; init; } = "";
    public string KindText { get; init; } = "";
    public string Detail { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string Id { get; init; } = "";
}

public sealed class SelftestItemVM
{
    public string PassText { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool Passed { get; init; }
}

// ================= 确认对话框 =================

public sealed class ConfirmDialog : Window
{
    public bool Confirmed { get; private set; }
    public CleanMode Mode { get; private set; } = CleanMode.Standard;

    private ComboBox? _modeBox;

    public ConfirmDialog(string title, string message, bool showMode)
    {
        Title = title;
        Width = 460;
        Height = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Noto Sans CJK SC, Inter, sans-serif");

        var ok = new Button { Content = "确定", Width = 90, Classes = { "accent" } };
        var cancel = new Button { Content = "取消", Width = 90, Margin = new Avalonia.Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => { Confirmed = true; Close(); };
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel },
        };

        var root = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12 };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

        if (showMode)
        {
            _modeBox = new ComboBox { SelectedIndex = 0 };
            _modeBox.Items.Add("标准：按各类别默认策略（缓存删除/隐私移回收站）");
            _modeBox.Items.Add("全部备份：所有内容移入备份区（最安全，可一键恢复）");
            _modeBox.Items.Add("全部永久删除：立即释放空间（不可恢复）");
            _modeBox.SelectionChanged += (_, _) =>
                Mode = _modeBox.SelectedIndex switch { 1 => CleanMode.AllToBackup, 2 => CleanMode.AllPermanent, _ => CleanMode.Standard };
            root.Children.Add(_modeBox);
        }

        root.Children.Add(buttons);
        Content = root;
    }
}

// ================= 主窗口 =================

public partial class MainWindow : Window
{
    private RuleLibrary _lib = new();
    private SafetyFilter _safety = SafetyFilter.CreateDefault();
    private AppConfig _cfg = new();
    private CancellationTokenSource? _cts;
    private ScanSummary? _scanSummary;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            _cfg = ConfigStore.InitRuntime();
            _lib = RuleLibraryLoader.Load();
            _safety = RuleLibraryLoader.LoadSafetyFilter();
        }
        catch (Exception ex)
        {
            StatusText.Text = "初始化失败：" + ex.Message;
        }

        VersionText.Text = $"v{BuildInfo.Version}（{BuildInfo.Platform}）";
        AboutVersion.Text = $"版本：{BuildInfo.FullName}（{BuildInfo.Stage}）";
        AboutOs.Text = $"系统：{SystemInfo.OsDescription}";
        AboutDataDir.Text = $"数据目录：{AppPaths.DataRoot}";
        AboutRulesDir.Text = $"规则库：{AppPaths.RulesFile}";
        AdminText.Text = SystemInfo.IsAdmin() ? "管理员" : "普通用户";
        RefreshFreeSpace();
        RefreshBackupList();
        RefreshLedger();
        LoadRulesInfo();
    }

    private void RefreshFreeSpace()
    {
        FreeSpaceText.Text = $"可用空间：{HumanSize.Format(SystemInfo.GetFreeBytes())}";
    }

    // ================= 扫描清理 =================

    private async void BtnScan_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _lib = RuleLibraryLoader.Load();
            _safety = RuleLibraryLoader.LoadSafetyFilter();
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载规则失败：" + ex.Message;
            return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        CatList.ItemsSource = null;
        _scanSummary = null;
        SelectedSizeText.Text = "";
        BtnClean.IsEnabled = false;
        Progress.IsVisible = true;

        try
        {
            var scanner = new Scanner(_lib, _safety, _cfg);
            var summary = await scanner.ScanAsync(
                null,
                new Progress<ScanProgress>(p => Dispatcher.UIThread.Post(() =>
                {
                    var pct = p.CategoriesTotal > 0 ? (int)(p.CategoriesDone * 100.0 / p.CategoriesTotal) : 0;
                    Progress.Value = pct;
                    StatusText.Text = $"扫描中 {p.CategoriesDone}/{p.CategoriesTotal} · {p.CurrentCategory} · 已发现 {HumanSize.Format(p.Bytes)}（{p.Files:N0} 个文件）";
                })),
                _cts.Token);

            _scanSummary = summary;
            var items = summary.Categories
                .Where(c => c.TotalFiles > 0 || IsRecycleBinCategory(c.CategoryId))
                .Select(c => new CategoryItemVM
                {
                    IsChecked = c.DefaultChecked,
                    Name = c.Name,
                    Note = c.Note,
                    SizeText = HumanSize.Format(c.TotalBytes),
                    RiskText = RiskTextOf(c.Risk),
                    RiskBrush = RiskBrushOf(c.Risk),
                    StrategyText = StrategyTextOf(c.Strategy),
                    RequiresAdmin = c.RequiresAdmin,
                    CategoryId = c.CategoryId,
                    Recommend = c.Recommend,
                    Strategy = c.Strategy,
                    Risk = c.Risk,
                }).ToList();

            CatList.ItemsSource = items;
            UpdateSelectedSize();
            // 用 Post 排队，确保晚于最后的进度回调执行，避免完成文案被覆盖
            Dispatcher.UIThread.Post(() => StatusText.Text = summary.Cancelled
                ? "扫描已取消。"
                : $"扫描完成：{summary.Categories.Count} 个类别 · {summary.TotalFiles:N0} 个文件 · {HumanSize.Format(summary.TotalBytes)} · 跳过无权限 {summary.Categories.Sum(x => x.SkippedAccessDirs)} / 符号链接 {summary.Categories.Sum(x => x.SkippedReparse)}");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "扫描已取消。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false);
            Progress.IsVisible = false;
        }
    }

    private void BtnSelectRecommended_Click(object? sender, RoutedEventArgs e)
    {
        if (CatList.ItemsSource is IEnumerable<CategoryItemVM> list)
            foreach (var it in list) it.IsChecked = it.Recommend;
        UpdateSelectedSize();
    }

    private void BtnUnselectAll_Click(object? sender, RoutedEventArgs e)
    {
        if (CatList.ItemsSource is IEnumerable<CategoryItemVM> list)
            foreach (var it in list) it.IsChecked = false;
        UpdateSelectedSize();
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => _cts?.Cancel();

    private bool IsRecycleBinCategory(string categoryId)
        => _lib.Categories.Any(r => string.Equals(r.Id, categoryId, StringComparison.OrdinalIgnoreCase)
                                    && r.Kind == RuleKind.EmptyRecycleBin);

    private void UpdateSelectedSize()
    {
        if (CatList.ItemsSource is not IEnumerable<CategoryItemVM> list) return;
        long bytes = 0;
        var count = 0;
        foreach (var it in list)
        {
            if (it.IsChecked && _scanSummary != null)
            {
                var c = _scanSummary.Categories.FirstOrDefault(x => x.CategoryId == it.CategoryId);
                if (c != null) bytes += c.TotalBytes;
                count++;
            }
        }

        SelectedSizeText.Text = count > 0 ? $"已选 {count} 项 · {HumanSize.Format(bytes)}" : "";
        BtnClean.IsEnabled = count > 0;
    }

    private async void BtnClean_Click(object? sender, RoutedEventArgs e)
    {
        if (CatList.ItemsSource is not IEnumerable<CategoryItemVM> list) return;
        var selected = list.Where(x => x.IsChecked).ToList();
        if (selected.Count == 0) return;

        var total = _scanSummary?.Categories
            .Where(c => selected.Any(s => s.CategoryId == c.CategoryId))
            .Sum(c => c.TotalBytes) ?? 0;

        var dlg = new ConfirmDialog("确认清理", $"将清理 {selected.Count} 个类别，共 {HumanSize.Format(total)}。\n请选择清理模式：", showMode: true);
        await dlg.ShowDialog(this);
        if (!dlg.Confirmed) return;

        var req = new CleanRequest
        {
            Mode = dlg.Mode,
            Items = selected.Select(s => new CleanSelectionItem { CategoryId = s.CategoryId }).ToList(),
        };

        _cts = new CancellationTokenSource();
        SetBusy(true);
        Progress.IsVisible = true;
        try
        {
            var executor = new CleanExecutor(_lib, _safety, _cfg);
            var report = await Task.Run(() => executor.Execute(req,
                new Progress<CleanProgress>(p => Dispatcher.UIThread.Post(() =>
                {
                    var pct = p.CategoriesTotal > 0 ? (int)(p.CategoriesDone * 100.0 / p.CategoriesTotal) : 0;
                    Progress.Value = pct;
                    StatusText.Text = $"清理中 {p.CategoriesDone}/{p.CategoriesTotal} · {p.CurrentCategory} · 已释放 {HumanSize.Format(p.FreedBytes)}";
                })),
                _cts.Token));

            var skipped = report.Categories.Sum(c => c.SkippedFiles);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"清理完成：删除 {HumanSize.Format(report.DeletedBytes)} · 备份 {HumanSize.Format(report.BackedUpBytes)} · 回收站 {HumanSize.Format(report.TrashedBytes)} · 跳过 {skipped} 个文件");
            foreach (var err in report.Errors.Take(5)) sb.AppendLine("  ! " + err);
            if (report.RestorePointResult != null) sb.AppendLine("  · " + report.RestorePointResult);
            Dispatcher.UIThread.Post(() => StatusText.Text = sb.ToString().TrimEnd());

            RefreshFreeSpace();
            RefreshBackupList();
            RefreshLedger();
            BtnScan_Click(null, null!); // 自动重新扫描刷新结果
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "清理已取消。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "清理失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false);
            Progress.IsVisible = false;
        }
    }

    // ================= 备份区 =================

    private void BtnRefreshBackup_Click(object? sender, RoutedEventArgs e) => RefreshBackupList();

    private void RefreshBackupList()
    {
        try
        {
            var zone = new BackupZone(_cfg.BackupRoot);
            var sessions = zone.ListSessions();
            var items = sessions.Select(s =>
            {
                var time = s.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                return new BackupItemVM
                {
                    Id = s.Id,
                    Name = time,
                    Detail = $"{s.Categories.FirstOrDefault() ?? "—"} · 已恢复 {s.RestoredFiles} · 已清空 {s.PurgedFiles}",
                    SizeText = HumanSize.Format(s.BytesRemaining),
                    StateText = s.FilesRemaining > 0 ? "可恢复" : "已处理完",
                    FilesRemaining = s.FilesRemaining,
                };
            }).ToList();
            BackupList.ItemsSource = items;
            BackupStatus.Text = $"备份区：{zone.Root} · {sessions.Count} 个会话";
        }
        catch (Exception ex)
        {
            BackupStatus.Text = "读取备份区失败：" + ex.Message;
        }
    }

    private async void BtnRestore_Click(object? sender, RoutedEventArgs e)
    {
        if (BackupList.ItemsSource is not IEnumerable<BackupItemVM> list) return;
        var selected = list.Where(x => x.IsChecked).ToList();
        if (selected.Count == 0) { BackupStatus.Text = "请先勾选要恢复的会话。"; return; }

        var dlg = new ConfirmDialog("确认恢复", $"将恢复 {selected.Count} 个会话中的文件（冲突文件自动改名保留）。", showMode: false);
        await dlg.ShowDialog(this);
        if (!dlg.Confirmed) return;

        var zone = new BackupZone(_cfg.BackupRoot);
        foreach (var s in selected)
        {
            var report = await Task.Run(() => zone.Restore(s.Id, new RestoreOptions { Conflict = ConflictPolicy.RenameAndRestore }, CancellationToken.None));
            BackupStatus.Text = $"恢复 {s.Id}：成功 {report.RestoredFiles} · 跳过 {report.SkippedFiles} · 失败 {report.FailedFiles}";
            LedgerStore.Append(LedgerFactory.FromRestore(s.Id, report, SystemInfo.GetFreeBytes(), SystemInfo.GetFreeBytes()));
        }

        RefreshBackupList();
        RefreshLedger();
        RefreshFreeSpace();
    }

    private async void BtnPurge_Click(object? sender, RoutedEventArgs e)
    {
        if (BackupList.ItemsSource is not IEnumerable<BackupItemVM> list) return;
        var selected = list.Where(x => x.IsChecked).ToList();
        if (selected.Count == 0) { BackupStatus.Text = "请先勾选要清空的会话。"; return; }

        var dlg = new ConfirmDialog("确认清空", $"将永久删除 {selected.Count} 个会话的备份内容，不可恢复。确定？", showMode: false);
        await dlg.ShowDialog(this);
        if (!dlg.Confirmed) return;

        var zone = new BackupZone(_cfg.BackupRoot);
        foreach (var s in selected)
        {
            var report = await Task.Run(() => zone.Purge(s.Id, CancellationToken.None));
            BackupStatus.Text = $"已清空 {s.Id}：{report.PurgedFiles} 个文件（{HumanSize.Format(report.PurgedBytes)}）";
            LedgerStore.Append(LedgerFactory.FromPurge(report, SystemInfo.GetFreeBytes(), SystemInfo.GetFreeBytes()));
        }

        RefreshBackupList();
        RefreshLedger();
        RefreshFreeSpace();
    }

    private async void BtnPurgeAll_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = new ConfirmDialog("确认清空全部", "将永久删除备份区中全部内容，不可恢复。确定？", showMode: false);
        await dlg.ShowDialog(this);
        if (!dlg.Confirmed) return;

        var zone = new BackupZone(_cfg.BackupRoot);
        foreach (var s in zone.ListSessions())
        {
            var report = await Task.Run(() => zone.Purge(s.Id, CancellationToken.None));
            LedgerStore.Append(LedgerFactory.FromPurge(report, SystemInfo.GetFreeBytes(), SystemInfo.GetFreeBytes()));
        }

        BackupStatus.Text = "备份区已全部清空。";
        RefreshBackupList();
        RefreshLedger();
        RefreshFreeSpace();
    }

    // ================= 台账 =================

    private void BtnRefreshLedger_Click(object? sender, RoutedEventArgs e) => RefreshLedger();

    private void RefreshLedger()
    {
        try
        {
            var entries = LedgerStore.LoadAll(50);
            var items = entries.Select(en => new LedgerItemVM
            {
                TimeText = en.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                KindText = KindTextOf(en.Kind),
                Detail = BuildLedgerDetail(en),
                SizeText = HumanSize.Format(en.ReleasedBytes + en.BackedUpBytes + en.TrashedBytes),
                Id = en.Id,
            }).ToList();
            LedgerList.ItemsSource = items;
        }
        catch (Exception ex)
        {
            LedgerList.ItemsSource = null;
            StatusText.Text = "读取台账失败：" + ex.Message;
        }
    }

    private async void BtnExportCsv_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出台账 CSV",
            SuggestedFileName = $"cleanmaster-ledger-{DateTime.Now:yyyyMMdd}.csv",
            DefaultExtension = "csv",
        });
        if (file == null) return;

        try
        {
            var entries = LedgerStore.LoadAll(1000);
            var csv = LedgerExporter.ToCsv(entries);
            await using var fs = File.Create(file.Path.LocalPath);
            var bytes = new System.Text.UTF8Encoding(true).GetBytes(csv);
            await fs.WriteAsync(bytes);
            StatusText.Text = $"台账已导出：{file.Path.LocalPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }

    private async void BtnExportHtml_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 HTML 报告",
            SuggestedFileName = $"cleanmaster-report-{DateTime.Now:yyyyMMdd}.html",
            DefaultExtension = "html",
        });
        if (file == null) return;

        try
        {
            var entries = LedgerStore.LoadAll(1000);
            var html = LedgerExporter.ToHtml(entries, "磁盘清理助手（Linux 版）台账报告");
            await File.WriteAllTextAsync(file.Path.LocalPath, html, new System.Text.UTF8Encoding(true));
            StatusText.Text = $"报告已导出：{file.Path.LocalPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }

    // ================= 规则 =================

    private void LoadRulesInfo()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"规则文件：{AppPaths.RulesFile}");
        sb.AppendLine($"用户规则目录：{AppPaths.UserRulesDir}");
        sb.AppendLine($"在线更新地址：{(string.IsNullOrEmpty(_cfg.RuleUpdateUrl) ? "（未配置）" : _cfg.RuleUpdateUrl)}");
        try
        {
            var lib = RuleLibraryLoader.Load();
            sb.AppendLine($"类别总数：{lib.Categories.Count}（启用 {lib.Categories.Count(c => c.Enabled)}）");
            sb.AppendLine($"安全 {lib.Categories.Count(c => c.Risk == RiskLevel.Safe)} · 谨慎 {lib.Categories.Count(c => c.Risk == RiskLevel.Moderate)} · 高风险 {lib.Categories.Count(c => c.Risk == RiskLevel.High)}");
            var problems = RuleLibraryLoader.Validate(lib);
            sb.AppendLine(problems.Count == 0 ? "校验结果：通过" : "校验问题：" + string.Join("；", problems));
        }
        catch (Exception ex)
        {
            sb.AppendLine("加载失败：" + ex.Message);
        }

        RulesLog.Text = sb.ToString();
    }

    private void BtnRulesCheck_Click(object? sender, RoutedEventArgs e) => LoadRulesInfo();

    private async void BtnRulesUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_cfg.RuleUpdateUrl))
        {
            RulesLog.Text = "未配置在线更新地址。可在 " + AppPaths.ConfigFile + " 中设置 ruleUpdateUrl。";
            return;
        }

        RulesLog.Text = "正在检查更新…";
        try
        {
            var current = RuleLibraryLoader.Load();
            var check = await Task.Run(() => RuleUpdater.Fetch(_cfg.RuleUpdateUrl, current));
            if (!check.Success)
            {
                RulesLog.Text = "获取失败：" + check.Message;
                return;
            }

            var diff = RuleUpdater.Diff(current, check.Library!);
            var dlg = new ConfirmDialog("应用规则更新", $"获取到更新：\n{diff}\n\n更新会先备份旧规则到用户规则目录，可手动回退。是否应用？", showMode: false);
            await dlg.ShowDialog(this);
            if (!dlg.Confirmed)
            {
                RulesLog.Text = "已取消应用更新。\n" + diff;
                return;
            }

            var result = RuleUpdater.Apply(check);
            RulesLog.Text = result.Success
                ? "更新已应用：" + result.Message + "\n" + diff
                : "应用失败：" + result.Message;
        }
        catch (Exception ex)
        {
            RulesLog.Text = "更新失败：" + ex.Message;
        }
    }

    // ================= 自检 =================

    private async void BtnSelfTest_Click(object? sender, RoutedEventArgs e)
    {
        BtnSelfTest.IsEnabled = false;
        SelftestList.ItemsSource = null;
        try
        {
            var results = await Task.Run(() => SelfTestRunner.Run(null, msg => Dispatcher.UIThread.Post(() => StatusText.Text = msg)));
            var items = results.Select(r => new SelftestItemVM
            {
                Passed = r.Passed,
                PassText = r.Passed ? "通过" : "失败",
                Name = r.Name,
                Detail = $"{r.Ms:0}ms · {r.Detail}",
            }).ToList();
            SelftestList.ItemsSource = items;
            StatusText.Text = $"自检完成：通过 {results.Count(r => r.Passed)} / 共 {results.Count}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "自检失败：" + ex.Message;
        }
        finally
        {
            BtnSelfTest.IsEnabled = true;
        }
    }

    // ================= 工具 =================

    private void SetBusy(bool busy)
    {
        BtnScan.IsEnabled = !busy;
        BtnClean.IsEnabled = !busy;
        BtnCancel.IsEnabled = busy;
        Progress.IsVisible = busy;
        if (busy) Progress.Value = 0;
    }

    private static string RiskTextOf(RiskLevel r) => r switch
    {
        RiskLevel.Safe => "安全",
        RiskLevel.Moderate => "谨慎",
        _ => "高风险",
    };

    private static IBrush RiskBrushOf(RiskLevel r) => r switch
    {
        RiskLevel.Safe => new SolidColorBrush(Color.Parse("#2E7D32")),
        RiskLevel.Moderate => new SolidColorBrush(Color.Parse("#EF6C00")),
        _ => new SolidColorBrush(Color.Parse("#C62828")),
    };

    private static string StrategyTextOf(CleanStrategy s) => s switch
    {
        CleanStrategy.Delete => "永久删除",
        CleanStrategy.Backup => "移入备份区",
        CleanStrategy.Trash => "移入回收站",
        _ => "—",
    };

    private static string KindTextOf(string kind) => kind switch
    {
        "clean" => "清理",
        "restore" => "恢复",
        "purge" => "清空备份区",
        "tool" => "工具",
        _ => kind,
    };

    private static string BuildLedgerDetail(LedgerEntry en)
    {
        var mode = en.Mode switch
        {
            "standard" => "标准",
            "allToBackup" => "全部备份",
            "allPermanent" => "永久删除",
            _ => en.Mode,
        };
        var cats = string.Join("、", en.Categories.Select(c => c.Name).Take(3));
        var detail = string.IsNullOrEmpty(cats) ? mode : $"{mode} · {cats}";
        if (!string.IsNullOrEmpty(en.Notes)) detail += $" · {en.Notes}";
        if (en.BackupSessionId != null) detail += $" · 会话 {en.BackupSessionId}";
        return detail;
    }
}
