using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CleanMaster.App.Dialogs;
using CleanMaster.App.ViewModels;
using CleanMaster.Core.Engine;
using CleanMaster.Core.Ledger;
using CleanMaster.Core.Models;
using CleanMaster.Core.Util;

namespace CleanMaster.App.Views;

public partial class CleanView : UserControl
{
    private readonly ObservableCollection<CategoryRow> _rows = new();
    private readonly Dictionary<string, List<DetailFileRow>> _detailCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _hasScanned;

    public CleanView()
    {
        InitializeComponent();
        LvCategories.ItemsSource = _rows;
        AppLog.Line += OnLogLine;
        Unloaded += (_, _) => AppLog.Line -= OnLogLine;
        Loaded += (_, _) =>
        {
            if (AppServices.Current.Library == null)
                Log("规则库未加载，请到「工具」页检查规则库后重试。");
        };
    }

    private void OnLogLine(string line)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) Log("! " + line);
        });
    }

    private void Log(string msg)
    {
        var text = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        var current = TxtLog.Text;
        if (current.Length > 7000) current = current.Substring(current.Length - 5000);
        TxtLog.Text = current.Length > 0 ? current + "\n" + text : text;
        LogScroll.ScrollToEnd();
    }

    // ================= 扫描 =================

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var s = AppServices.Current;
        if (s.Library == null)
        {
            Ui.Warn("规则库未加载。\n\n" + (s.RulesError ?? "请到「工具」页检查。"));
            return;
        }

        _busy = true;
        _cts = new CancellationTokenSource();
        try
        {
            BtnScan.IsEnabled = false;
            BtnClean.IsEnabled = false;
            TxtScanInfo.Text = "扫描中…";
            UiShell.SetBusy?.Invoke(true);
            UiShell.SetCancel?.Invoke(() => _cts?.Cancel());
            UiShell.SetStatus?.Invoke("正在扫描可清理项…");

            s.ExclusionMap.Clear();
            _detailCache.Clear();
            _rows.Clear();
            LvDetailFiles.ItemsSource = null;
            TxtDetailTitle.Text = "扫描中…";
            TxtDetailNote.Text = "";
            TxtDetailMeta.Text = "";

            var scanner = new Scanner(s.Library, s.Safety, s.Config);
            var progress = new Progress<ScanProgress>(p =>
            {
                UiShell.SetStatus?.Invoke($"扫描中 [{p.CategoriesDone}/{p.CategoriesTotal}] {HumanSize.Format(p.Bytes)} · {p.CurrentCategory}");
                if (p.CategoriesTotal > 0)
                    UiShell.SetProgress?.Invoke(p.CategoriesDone * 100.0 / p.CategoriesTotal, false);
            });

            var sw = Stopwatch.StartNew();
            var summary = await scanner.ScanAsync(null, progress, _cts.Token);
            sw.Stop();

            s.LastScan = summary;
            PopulateRows(summary);
            _hasScanned = true;

            if (summary.Cancelled)
            {
                TxtScanInfo.Text = "扫描已取消";
                UiShell.SetStatus?.Invoke("扫描已取消");
                Log("扫描已取消。");
            }
            else
            {
                TxtScanInfo.Text = $"上次扫描 {DateTime.Now:HH:mm:ss} · 耗时 {sw.Elapsed.TotalSeconds:0.0}s";
                UiShell.SetStatus?.Invoke("扫描完成");
                Log($"扫描完成：{summary.Categories.Count} 个类别 · {summary.TotalFiles:N0} 个文件 · {HumanSize.Format(summary.TotalBytes)} · {sw.Elapsed.TotalSeconds:0.0}s");
                var issues = summary.Categories.Sum(c => c.SkippedAccessDirs);
                if (issues > 0) Log($"提示：{issues} 个目录无权限访问被跳过（可用管理员身份运行以获得完整结果）。");
            }

            UpdateSummary();
        }
        catch (Exception ex)
        {
            AppLog.Exception("扫描失败", ex);
            Ui.Warn("扫描失败：" + ex.Message);
            TxtScanInfo.Text = "扫描失败";
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            BtnScan.IsEnabled = true;
            UiShell.SetBusy?.Invoke(false);
            UiShell.SetProgress?.Invoke(-1, false);
            UpdateSummary();
        }
    }

    private void PopulateRows(ScanSummary summary)
    {
        foreach (var r in summary.Categories)
        {
            var row = CategoryRow.From(r, checkedState: r.DefaultChecked && r.TotalFiles > 0);
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(CategoryRow.IsChecked)) UpdateSummary();
            };
            _rows.Add(row);
        }
    }

    private void UpdateSummary()
    {
        var checkedRows = _rows.Where(r => r.IsChecked).ToList();
        var bytes = checkedRows.Sum(r => r.SizeBytes);
        var files = checkedRows.Sum(r => r.FileCount);
        UiShell.SetSummary?.Invoke(checkedRows.Count > 0
            ? $"已选 {checkedRows.Count} 项 · {files:N0} 个文件 · {HumanSize.Format(bytes)}"
            : "未选择任何项目");
        BtnClean.IsEnabled = checkedRows.Count > 0 && !_busy;
    }

    private void BtnRecommend_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows)
            r.IsChecked = r.Recommend && r.FileCount > 0;
        UpdateSummary();
        Log("已应用推荐组合（安全且效果明显的类别）。");
    }

    private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsChecked = false;
        UpdateSummary();
    }

    // ================= 详情 =================

    private void LvCategories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LvCategories.SelectedItem is not CategoryRow row || row.Result == null) return;
        ShowDetail(row);
    }

    private void ShowDetail(CategoryRow row)
    {
        var r = row.Result!;
        TxtDetailTitle.Text = $"{row.Name} · {row.RiskText} · {row.StrategyText}";
        TxtDetailNote.Text = row.NoteText;

        if (!_detailCache.TryGetValue(row.CategoryId, out var list))
        {
            list = new List<DetailFileRow>();
            foreach (var f in r.TopItems.Take(1000))
            {
                list.Add(new DetailFileRow
                {
                    FullPath = f.Path,
                    Size = f.Size,
                    SizeText = HumanSize.Format(f.Size),
                    DateText = f.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    LastWriteUtc = f.LastWriteUtc,
                });
            }

            _detailCache[row.CategoryId] = list;
        }

        LvDetailFiles.ItemsSource = list;

        var meta = new List<string>();
        if (r.TopItems.Count > 1000) meta.Add($"文件列表按大小显示前 1000 项（共 {r.TotalFiles:N0} 项，全部匹配文件都会参与清理）。");
        else if (r.TotalFiles > r.TopItems.Count) meta.Add($"展示 {r.TopItems.Count} 项（共 {r.TotalFiles:N0} 项）。");
        meta.Add("取消勾选可排除个别文件。");
        if (r.Roots.Count > 0) meta.Add("扫描位置：" + string.Join("；", r.Roots.Take(2).Select(p => PathUtil.Truncate(p, 60))));
        if (r.SkippedReparse > 0) meta.Add($"已跳过 {r.SkippedReparse} 个符号链接/联接点（避免重复计算）。");
        if (r.SkippedAccessDirs > 0) meta.Add($"{r.SkippedAccessDirs} 个目录无法访问（可能需要管理员）。");
        if (r.NeedsAdminHint) meta.Add("⚠ 此类别需要管理员权限才能完整清理。");
        meta.AddRange(r.WarnNotes);
        TxtDetailMeta.Text = string.Join("\n", meta);
    }

    // ================= 清理 =================

    private async void BtnClean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var s = AppServices.Current;
        if (s.Library == null) { Ui.Warn("规则库未加载。"); return; }

        var selected = _rows.Where(r => r.IsChecked).ToList();
        if (selected.Count == 0)
        {
            Ui.Info("请先勾选要清理的类别。");
            return;
        }

        var dialog = new ConfirmCleanDialog(selected) { Owner = Window.GetWindow(this) };
        AppLog.Info($"准备显示清理确认对话框：{selected.Count} 个类别");
        if (dialog.ShowDialog() != true || !dialog.Confirmed) return;

        var req = new CleanRequest
        {
            Mode = dialog.SelectedMode,
            CreateRestorePoint = dialog.CreateRestorePoint,
        };
        foreach (var row in selected)
        {
            var item = new CleanSelectionItem { CategoryId = row.CategoryId };
            if (_detailCache.TryGetValue(row.CategoryId, out var list))
                item.ExcludePaths.AddRange(list.Where(x => !x.IsIncluded).Select(x => x.FullPath));
            req.Items.Add(item);
        }

        _busy = true;
        _cts = new CancellationTokenSource();
        try
        {
            BtnScan.IsEnabled = false;
            BtnClean.IsEnabled = false;
            UiShell.SetBusy?.Invoke(true);
            UiShell.SetCancel?.Invoke(() => _cts?.Cancel());
            UiShell.SetStatus?.Invoke("正在清理…");
            UiShell.SetProgress?.Invoke(0, false);
            Log("开始清理：" + string.Join("、", selected.Select(r => r.Name)));

            var executor = new CleanExecutor(s.Library, s.Safety, s.Config);
            var progress = new Progress<CleanProgress>(p =>
            {
                UiShell.SetStatus?.Invoke($"清理中 [{p.CategoriesDone}/{p.CategoriesTotal}] 释放 {HumanSize.Format(p.FreedBytes)} · 跳过 {p.Skipped:N0} · {p.CurrentCategory}");
                if (p.CategoriesTotal > 0)
                    UiShell.SetProgress?.Invoke(p.CategoriesDone * 100.0 / p.CategoriesTotal, false);
            });

            var report = await Task.Run(() => executor.Execute(req, progress, _cts.Token));

            var entry = LedgerFactory.FromClean(report);
            LedgerStore.Append(entry);
            s.NotifyDiskStats();

            Log($"清理完成：删除 {HumanSize.Format(report.DeletedBytes)} · 备份 {HumanSize.Format(report.BackedUpBytes)} · 回收站 {HumanSize.Format(report.TrashedBytes)} · 跳过 {report.TotalSkippedFiles:N0}"
                + $"\nC 盘可用：{HumanSize.Format(report.FreeBeforeBytes)} → {HumanSize.Format(report.FreeAfterBytes)}（Δ {HumanSize.Format(report.FreeAfterBytes - report.FreeBeforeBytes)}）· 耗时 {report.ElapsedSeconds:0.0}s");

            var lines = new List<string>
            {
                report.Cancelled ? "清理已停止（已完成部分保留）。" : "清理完成。",
                "",
                $"永久删除：{HumanSize.Format(report.DeletedBytes)}（已立即释放）",
                $"移入备份区：{HumanSize.Format(report.BackedUpBytes)}（可在「备份与恢复」中恢复）",
                $"移至回收站：{HumanSize.Format(report.TrashedBytes)}",
                $"跳过：{report.TotalSkippedFiles:N0} 个文件（被占用/权限不足等）",
                $"C 盘可用：{HumanSize.Format(report.FreeBeforeBytes)} → {HumanSize.Format(report.FreeAfterBytes)}",
            };
            if (report.RestorePointResult != null) lines.Add("还原点：" + report.RestorePointResult);
            lines.Add("");
            lines.Add($"台账已记录：{entry.Id}");

            var skippedSample = report.Categories.SelectMany(c => c.Skipped).Take(6).ToList();
            if (skippedSample.Count > 0)
            {
                lines.Add("");
                lines.Add("跳过示例：");
                foreach (var sk in skippedSample) lines.Add("  · " + PathUtil.Truncate(sk.Path, 70) + " — " + sk.Reason);
            }

            var dlg = new ConfirmDialog("清理报告", lines[0], lines.Skip(1).Where(x => x != "").ToList(),
                okText: "知道了", danger: false) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();

            if (report.BackedUpBytes > 0 && Ui.Ask("有文件已移入备份区。\n是否切换到「备份与恢复」查看或恢复？", "备份区"))
            {
                (Window.GetWindow(this) as MainWindow)?.SelectTab(2);
            }

            _hasScanned = false;
            TxtScanInfo.Text = "清理完成 · 建议重新扫描核对";
            BtnClean.IsEnabled = false;
        }
        catch (Exception ex)
        {
            AppLog.Exception("清理失败", ex);
            Ui.Warn("清理失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            BtnScan.IsEnabled = true;
            UiShell.SetBusy?.Invoke(false);
            UiShell.SetProgress?.Invoke(-1, false);
            UpdateSummary();
        }
    }
}
