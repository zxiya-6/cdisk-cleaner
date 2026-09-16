using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CleanMaster.App.Dialogs;
using CleanMaster.App.ViewModels;
using CleanMaster.Core.Engine;
using CleanMaster.Core.Ledger;
using CleanMaster.Core.Models;
using CleanMaster.Core.Util;

namespace CleanMaster.App.Views;

public partial class BackupView : UserControl
{
    private readonly ObservableCollection<SessionRow> _rows = new();
    private bool _busy;

    public BackupView()
    {
        InitializeComponent();
        LvSessions.ItemsSource = _rows;
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var zone = AppServices.Current.CreateBackupZone();
        TxtRoot.Text = zone.Root;

        var sessions = zone.ListSessions();
        _rows.Clear();
        foreach (var s in sessions)
        {
            _rows.Add(new SessionRow
            {
                Info = s,
                CreatedText = s.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            });
        }

        var remainFiles = sessions.Sum(s => s.FilesRemaining);
        var remainBytes = sessions.Sum(s => s.BytesRemaining);
        TxtStats.Text = $"{sessions.Count} 个会话 · 备份区中 {remainFiles:N0} 个文件 · {HumanSize.Format(remainBytes)}";
        LvItems.ItemsSource = null;
        TxtDetailTitle.Text = "选中会话后可查看其中的文件（前 500 条）";
    }

    private void BtnOpenRoot_Click(object sender, RoutedEventArgs e)
    {
        var zone = AppServices.Current.CreateBackupZone();
        Ui.OpenFolder(zone.Root);
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void LvSessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = LvSessions.SelectedItem as SessionRow;
        if (row == null)
        {
            LvItems.ItemsSource = null;
            return;
        }

        TxtDetailTitle.Text = $"会话 {row.Id} 中的文件（前 500 条，共 {row.Info.FilesRemaining:N0} 个）";
        var zone = AppServices.Current.CreateBackupZone();
        var manifest = BackupZone.ManifestPath(BackupZone.SessionDir(zone.Root, row.Id));
        var items = BackupZone.FoldManifest(manifest);
        var list = items.Values
            .OrderByDescending(x => x.S)
            .Take(500)
            .Select(x => new DetailItemRow { SizeText = HumanSize.Format(x.S), PathText = x.P })
            .ToList();
        LvItems.ItemsSource = list;
    }

    private async void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = _rows.Where(r => r.IsChecked).ToList();
        if (selected.Count == 0)
        {
            var cur = LvSessions.SelectedItem as SessionRow;
            if (cur != null) selected.Add(cur);
        }

        selected = selected.Where(s => s.Info.FilesRemaining > 0).ToList();
        if (selected.Count == 0)
        {
            Ui.Info("请先在列表中勾选要恢复的会话（或选中某行）。");
            return;
        }

        var fileCount = selected.Sum(s => s.Info.FilesRemaining);
        var bytes = selected.Sum(s => s.Info.BytesRemaining);
        var conflict = (CmbConflict.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "rename";
        var policy = conflict switch
        {
            "skip" => ConflictPolicy.Skip,
            "overwrite" => ConflictPolicy.Overwrite,
            _ => ConflictPolicy.RenameAndRestore,
        };

        var details = selected.Take(8).Select(s => $"{s.Id} · {s.Info.FilesRemaining:N0} 个文件 · {HumanSize.Format(s.Info.BytesRemaining)}").ToList();
        var dlg = new ConfirmDialog("恢复备份",
            $"将把 {selected.Count} 个会话中的 {fileCount:N0} 个文件（{HumanSize.Format(bytes)}）恢复到原始位置。",
            details, okText: "开始恢复", danger: false) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        _busy = true;
        try
        {
            UiShell.SetBusy?.Invoke(true);
            UiShell.SetStatus?.Invoke("正在恢复…");
            UiShell.SetProgress?.Invoke(0, true);
            var zone = AppServices.Current.CreateBackupZone();
            var before = SystemInfo.GetFreeBytes("C");

            long restored = 0, skipped = 0, failed = 0, restoredBytes = 0;
            var errors = new List<string>();

            foreach (var s in selected)
            {
                var sid = s.Id;
                var report = await Task.Run(() => zone.Restore(sid, new RestoreOptions { Conflict = policy }, CancellationToken.None));
                restored += report.RestoredFiles;
                skipped += report.SkippedFiles;
                failed += report.FailedFiles;
                restoredBytes += report.RestoredBytes;
                errors.AddRange(report.Errors.Take(5));
                LedgerStore.Append(LedgerFactory.FromRestore(sid, report, before, SystemInfo.GetFreeBytes("C")));
            }

            UiShell.SetStatus?.Invoke("恢复完成");
            var msg = $"恢复完成：成功 {restored:N0} 个（{HumanSize.Format(restoredBytes)}）· 跳过 {skipped:N0} · 失败 {failed:N0}";
            if (errors.Count > 0) msg += "\n\n部分问题：\n" + string.Join("\n", errors.Take(6));
            Ui.Info(msg, "恢复备份");
            AppLog.Info("备份恢复：" + msg.Replace("\n", " "));
            Refresh();
        }
        catch (Exception ex)
        {
            AppLog.Exception("恢复失败", ex);
            Ui.Warn("恢复失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            UiShell.SetBusy?.Invoke(false);
            UiShell.SetProgress?.Invoke(-1, false);
        }
    }

    private async void BtnPurgeSel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = _rows.Where(r => r.IsChecked).ToList();
        if (selected.Count == 0)
        {
            Ui.Info("请先勾选要清空的会话。");
            return;
        }

        await PurgeAsync(selected, all: false);
    }

    private async void BtnPurgeAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var all = _rows.ToList();
        if (all.Count == 0)
        {
            Ui.Info("备份区当前为空。");
            return;
        }

        await PurgeAsync(all, all: true);
    }

    private async Task PurgeAsync(List<SessionRow> sessions, bool all)
    {
        var files = sessions.Sum(s => s.Info.FilesRemaining);
        var bytes = sessions.Sum(s => s.Info.BytesRemaining);
        var dlg = new ConfirmDialog("清空备份区",
            $"将永久删除 {(all ? "全部" : "选中的")} {sessions.Count} 个会话中的 {files:N0} 个文件（{HumanSize.Format(bytes)}）。\n\n删除后无法恢复！清空后这部分空间才会真正释放。",
            sessions.Take(8).Select(s => s.Id + " · " + HumanSize.Format(s.Info.BytesRemaining)).ToList(),
            ackText: "我已了解：清空后这些文件不可恢复", okText: "永久清空", danger: true) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        _busy = true;
        try
        {
            UiShell.SetBusy?.Invoke(true);
            UiShell.SetStatus?.Invoke("正在清空备份区…");
            var zone = AppServices.Current.CreateBackupZone();
            var before = SystemInfo.GetFreeBytes("C");

            long purged = 0, purgedBytes = 0, failed = 0;
            if (all)
            {
                var r = await Task.Run(() => zone.PurgeAll(CancellationToken.None));
                purged = r.PurgedFiles;
                purgedBytes = r.PurgedBytes;
                failed = r.FailedFiles;
                LedgerStore.Append(LedgerFactory.FromPurge(r, before, SystemInfo.GetFreeBytes("C")));
            }
            else
            {
                foreach (var s in sessions)
                {
                    var sid = s.Id;
                    var r = await Task.Run(() => zone.Purge(sid, CancellationToken.None));
                    purged += r.PurgedFiles;
                    purgedBytes += r.PurgedBytes;
                    failed += r.FailedFiles;
                    LedgerStore.Append(LedgerFactory.FromPurge(r, before, SystemInfo.GetFreeBytes("C")));
                }
            }

            AppServices.Current.NotifyDiskStats();
            UiShell.SetStatus?.Invoke("备份区已清空");
            Ui.Info($"已清空 {sessions.Count} 个会话：{purged:N0} 个文件，释放 {HumanSize.Format(purgedBytes)}"
                    + (failed > 0 ? $"\n失败 {failed:N0} 个（可能被占用）" : ""), "清空备份区");
            Refresh();
        }
        catch (Exception ex)
        {
            AppLog.Exception("清空备份区失败", ex);
            Ui.Warn("清空失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            UiShell.SetBusy?.Invoke(false);
            UiShell.SetProgress?.Invoke(-1, false);
        }
    }
}

/// <summary>备份区条目预览。</summary>
public sealed class DetailItemRow
{
    public string SizeText { get; init; } = "";
    public string PathText { get; init; } = "";
}
