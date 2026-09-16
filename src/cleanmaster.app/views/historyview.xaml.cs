using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CleanMaster.App.ViewModels;
using CleanMaster.Core.Config;
using CleanMaster.Core.Ledger;
using CleanMaster.Core.Models;
using CleanMaster.Core.Util;

namespace CleanMaster.App.Views;

public partial class HistoryView : UserControl
{
    private readonly ObservableCollection<LedgerRow> _rows = new();

    public HistoryView()
    {
        InitializeComponent();
        LvEntries.ItemsSource = _rows;
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var entries = LedgerStore.LoadAll(500);
        _rows.Clear();
        foreach (var e in entries)
        {
            long files = 0, skipped = 0;
            foreach (var c in e.Categories)
            {
                files += c.Files;
                skipped += c.Skipped;
            }

            _rows.Add(new LedgerRow
            {
                Entry = e,
                TimeText = e.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                KindText = KindText(e.Kind),
                ModeText = e.Mode,
                FilesText = files.ToString("N0"),
                ReleasedText = HumanSize.Format(Math.Max(0, e.ReleasedBytes)),
                BackedText = HumanSize.Format(Math.Max(0, e.BackedUpBytes)),
                TrashedText = HumanSize.Format(Math.Max(0, e.TrashedBytes)),
                SkippedText = skipped.ToString("N0"),
                StatusText = e.Cancelled ? "已取消" : e.DryRun ? "试运行" : e.Errors.Count > 0 ? "有错误" : "成功",
                NotesText = e.Notes ?? "",
            });
        }

        TxtInfo.Text = $"最近 {entries.Count} 条记录（共 {LedgerStore.Count()} 条）";
        LvCategoryDetail.ItemsSource = null;
        TxtDetailNotes.Text = "";
        TxtDetailTitle.Text = "选中一条记录查看明细";
    }

    private static string KindText(string kind) => kind switch
    {
        "clean" => "清理",
        "restore" => "恢复",
        "purge" => "清空备份区",
        "tool" => "工具",
        _ => kind,
    };

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void LvEntries_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = LvEntries.SelectedItem as LedgerRow;
        if (row == null) return;
        var entry = row.Entry;

        TxtDetailTitle.Text = $"记录 {entry.Id} · {row.KindText} · {row.TimeText}";
        var cats = entry.Categories.Select(c => new CategoryDetailRow
        {
            NameText = c.Name,
            StrategyText = c.Strategy,
            FilesText = c.Files.ToString("N0"),
            SizeText = HumanSize.Format(c.Bytes),
            SkippedText = c.Skipped.ToString("N0"),
        }).ToList();
        LvCategoryDetail.ItemsSource = cats;

        var notes = new List<string>();
        if (!string.IsNullOrEmpty(entry.Notes)) notes.Add(entry.Notes!);
        notes.Add($"耗时 {(entry.FinishedAtUtc - entry.StartedAtUtc).TotalSeconds:0.0}s · 管理员={entry.Admin} · 试运行={entry.DryRun} · 取消={entry.Cancelled}");
        notes.Add($"C 盘可用：{HumanSize.Format(entry.FreeBeforeBytes)} → {HumanSize.Format(entry.FreeAfterBytes)}");
        if (entry.BackedUpBytes != 0) notes.Add($"备份区变动：{HumanSize.Format(entry.BackedUpBytes)}");
        if (entry.BackupSessionId != null) notes.Add($"备份会话：{entry.BackupSessionId}");
        if (entry.Errors.Count > 0)
        {
            notes.Add("");
            notes.Add("错误：");
            notes.AddRange(entry.Errors.Take(10).Select(x => "· " + x));
        }

        TxtDetailNotes.Text = string.Join("\n", notes);
    }

    private void BtnOpenData_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(AppPaths.LedgerDir);

    // ================= 导出 =================

    private void BtnExportCsv_Click(object sender, RoutedEventArgs e) =>
        Export("csv", "CSV 文件|*.csv");

    private void BtnExportDetail_Click(object sender, RoutedEventArgs e) =>
        Export("csv-detail", "CSV 文件|*.csv");

    private void BtnExportHtml_Click(object sender, RoutedEventArgs e) =>
        Export("html", "HTML 报告|*.html");

    private void Export(string format, string filter)
    {
        var ext = format == "html" ? "html" : "csv";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = filter,
            FileName = $"清理台账_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}",
            Title = "导出台账",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var entries = LedgerStore.LoadAll(10000);
            var content = format switch
            {
                "csv" => LedgerExporter.ToCsv(entries),
                "csv-detail" => LedgerExporter.ToCategoryCsv(entries),
                _ => LedgerExporter.ToHtml(entries),
            };
            File.WriteAllText(dlg.FileName, content, new System.Text.UTF8Encoding(true));
            AppLog.Info($"台账已导出：{dlg.FileName}（{entries.Count} 条）");

            if (Ui.Ask($"已导出 {entries.Count} 条记录：\n{dlg.FileName}\n\n是否立即打开？", "导出成功"))
            {
                try { Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); } catch { }
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("导出台账失败", ex);
            Ui.Warn("导出失败：" + ex.Message);
        }
    }
}

/// <summary>台账类别明细行。</summary>
public sealed class CategoryDetailRow
{
    public string NameText { get; init; } = "";
    public string StrategyText { get; init; } = "";
    public string FilesText { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string SkippedText { get; init; } = "";
}
