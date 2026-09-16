using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CleanMaster.App.Dialogs;
using CleanMaster.App.ViewModels;
using CleanMaster.Core.Engine;
using CleanMaster.Core.Util;

namespace CleanMaster.App.Views;

public partial class BigFileView : UserControl
{
    private readonly ObservableCollection<BigFileRow> _rows = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    public BigFileView()
    {
        InitializeComponent();
        LvFiles.ItemsSource = _rows;
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = @"选择要扫描的文件夹（例如 C:\）",
            SelectedPath = TxtRoot.Text,
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TxtRoot.Text = dlg.SelectedPath;
    }

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var root = PathUtil.Normalize(TxtRoot.Text);
        if (!Directory.Exists(root))
        {
            Ui.Warn("目录不存在：" + root);
            return;
        }

        var minMb = 100;
        if (CmbMinSize.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var mb)) minMb = mb;

        _busy = true;
        _cts = new CancellationTokenSource();
        try
        {
            _rows.Clear();
            BtnScan.IsEnabled = false;
            TxtInfo.Text = "扫描中…";
            UiShell.SetBusy?.Invoke(true);
            UiShell.SetCancel?.Invoke(() => _cts?.Cancel());
            UiShell.SetStatus?.Invoke("正在扫描大文件…");
            UiShell.SetProgress?.Invoke(0, true);

            var progress = new Progress<BigFileProgress>(p =>
                UiShell.SetStatus?.Invoke($"大文件扫描：{p.Files:N0} 个文件 · {HumanSize.Format(p.Bytes)} · {p.Seconds:0.0}s"));

            var token = _cts.Token;
            var result = await Task.Run(() => BigFileScanner.Scan(root, minMb * 1024L * 1024L, 1000, progress, token));

            foreach (var f in result.Items)
            {
                var row = new BigFileRow
                {
                    FullPath = f.Path,
                    Size = f.Size,
                    SizeText = HumanSize.Format(f.Size),
                    DateText = f.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                };
                row.PropertyChanged += (_, _) => UpdateButtons();
                _rows.Add(row);
            }

            TxtInfo.Text = result.Cancelled
                ? $"扫描已取消（已完成部分）· 命中 {_rows.Count} 个"
                : $"共 {result.Items.Count} 个 ≥{minMb} MB 的文件 · 扫描 {result.ScannedFiles:N0} 个文件 · {result.ElapsedSeconds:0.0}s";
            UiShell.SetStatus?.Invoke(result.Cancelled ? "大文件扫描已取消" : "大文件扫描完成");
            AppLog.Info($"大文件扫描 {root}：命中 {result.Items.Count}，用时 {result.ElapsedSeconds:0.0}s，取消={result.Cancelled}");
            if (result.SkippedAccessDirs > 0) TxtInfo.Text += $"（{result.SkippedAccessDirs} 个目录无权限被跳过）";
        }
        catch (Exception ex)
        {
            AppLog.Exception("大文件扫描失败", ex);
            Ui.Warn("扫描失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            BtnScan.IsEnabled = true;
            UiShell.SetBusy?.Invoke(false);
            UiShell.SetProgress?.Invoke(-1, false);
            UpdateButtons();
        }
    }

    private void LvFiles_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var selected = LvFiles.SelectedItems.OfType<BigFileRow>().ToList();
        var checkedRows = _rows.Where(r => r.IsChecked).ToList();
        BtnOpen.IsEnabled = selected.Count > 0 || checkedRows.Count > 0;
        BtnCopy.IsEnabled = selected.Count > 0 || checkedRows.Count > 0;
        BtnRecycle.IsEnabled = checkedRows.Count > 0 && !_busy;
    }

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        var target = LvFiles.SelectedItems.OfType<BigFileRow>().FirstOrDefault() ?? _rows.FirstOrDefault(r => r.IsChecked);
        if (target != null) Ui.SelectInExplorer(target.FullPath);
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        var targets = LvFiles.SelectedItems.OfType<BigFileRow>().ToList();
        if (targets.Count == 0) targets = _rows.Where(r => r.IsChecked).ToList();
        if (targets.Count == 0) return;
        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, targets.Select(t => t.FullPath)));
            UiShell.SetStatus?.Invoke($"已复制 {targets.Count} 个路径到剪贴板");
        }
        catch (Exception ex)
        {
            AppLog.Exception("复制路径失败", ex);
        }
    }

    private void BtnRecycle_Click(object sender, RoutedEventArgs e)
    {
        var targets = _rows.Where(r => r.IsChecked).ToList();
        if (targets.Count == 0) return;

        var details = targets.Take(12).Select(t => HumanSize.Format(t.Size) + "  " + t.FullPath).ToList();
        if (targets.Count > 12) details.Add($"…… 以及另外 {targets.Count - 12} 个文件");
        var dlg = new ConfirmDialog(
            "移到回收站",
            $"将把选中的 {targets.Count} 个文件（{HumanSize.Format(targets.Sum(t => t.Size))}）移到回收站。\n可从回收站恢复。",
            details, okText: "移到回收站", danger: true) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        var ok = 0;
        var failed = new List<string>();
        foreach (var t in targets)
        {
            if (RecycleBin.TryRecycle(t.FullPath, out var err)) ok++;
            else failed.Add(PathUtil.Truncate(t.FullPath, 70) + " — " + err);
        }

        foreach (var t in targets.Where(t => !File.Exists(t.FullPath)).ToList())
            _rows.Remove(t);

        UpdateButtons();
        var msg = $"已移到回收站：{ok} 个";
        if (failed.Count > 0) msg += $"\n失败：{failed.Count} 个\n" + string.Join("\n", failed.Take(6));
        Ui.Info(msg, "移到回收站");
        AppLog.Info("大文件移到回收站：" + ok + " 个，失败 " + failed.Count);
    }
}
