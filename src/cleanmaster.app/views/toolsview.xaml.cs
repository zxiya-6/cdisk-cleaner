using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CleanMaster.App.Dialogs;
using CleanMaster.App.ViewModels;
using CleanMaster.Core.Config;
using CleanMaster.Core.Ledger;
using CleanMaster.Core.Rules;
using CleanMaster.Core.Util;
using Microsoft.Win32;

namespace CleanMaster.App.Views;

public partial class ToolsView : UserControl
{
    private bool _dismRunning;
    private bool _restorePointRunning;
    private List<SoftwareRow> _software = new();
    private RuleUpdateCheck? _pendingUpdate;

    public ToolsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            TxtBackupRoot.Text = AppServices.Current.Config.BackupRoot;
            TxtRuleUrl.Text = AppServices.Current.Config.RuleUpdateUrl;
        };
    }

    // ================= 系统还原点 =================

    private async void BtnRestorePoint_Click(object sender, RoutedEventArgs e)
    {
        if (_restorePointRunning) return;
        _restorePointRunning = true;
        BtnRestorePoint.IsEnabled = false;
        TxtRestoreResult.Text = "正在创建…（可能需要一两分钟）";
        try
        {
            var before = SystemInfo.GetFreeBytes("C");
            var result = await Task.Run(() => RestorePointHelper.Create("C盘清理助手 手动还原点"));
            TxtRestoreResult.Text = result;
            LedgerStore.Append(LedgerFactory.FromTool("创建还原点", result, before, SystemInfo.GetFreeBytes("C")));
            AppLog.Info("手动创建还原点：" + result);
        }
        finally
        {
            _restorePointRunning = false;
            BtnRestorePoint.IsEnabled = true;
        }
    }

    // ================= DISM =================

    private async void BtnDismAnalyze_Click(object sender, RoutedEventArgs e)
    {
        await RunDismAsync("/Online /Cleanup-Image /AnalyzeComponentStore", "分析组件存储");
    }

    private async void BtnDismClean_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ConfirmDialog("清理组件存储",
            "将运行 DISM 组件清理（StartComponentCleanup），移除系统组件存储中已被替代的旧组件。\n\n该操作通常可释放数 GB 空间，但可能需要 5-15 分钟，期间请勿关闭程序。",
            new[] { "命令：dism /Online /Cleanup-Image /StartComponentCleanup", "影响：已安装的更新无法卸载回滚到旧版本" },
            ackText: "我已了解，并确认运行组件清理", okText: "开始清理", danger: true) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        await RunDismAsync("/Online /Cleanup-Image /StartComponentCleanup", "组件存储清理");
    }

    private async Task RunDismAsync(string args, string label)
    {
        if (_dismRunning) return;
        _dismRunning = true;
        BtnDismAnalyze.IsEnabled = false;
        BtnDismClean.IsEnabled = false;
        TxtDismOutput.Text = $"[{label}] 正在运行：dism {args}{Environment.NewLine}";
        UiShell.SetStatus?.Invoke(label + " 进行中…");
        UiShell.SetBusy?.Invoke(true);

        try
        {
            var before = SystemInfo.GetFreeBytes("C");
            var psi = new ProcessStartInfo
            {
                FileName = "dism.exe",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // DISM 输出为系统控制台编码（简体中文系统为 GBK/936）；.NET 7 需先注册代码页提供程序，
            // 否则 GetEncoding(936) 抛异常导致按 UTF-8 误读而出现乱码。
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                psi.StandardOutputEncoding = Encoding.GetEncoding(936);
                psi.StandardErrorEncoding = Encoding.GetEncoding(936);
            }
            catch
            {
                try { psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8; } catch { }
            }

            var p = Process.Start(psi);
            if (p == null)
            {
                AppendDism("无法启动 dism.exe");
                return;
            }

            p.OutputDataReceived += (_, e) => { if (e.Data != null) Dispatcher.BeginInvoke(() => AppendDism(e.Data)); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Dispatcher.BeginInvoke(() => AppendDism("! " + e.Data)); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await Task.Run(() => p.WaitForExit());

            var after = SystemInfo.GetFreeBytes("C");
            AppendDism($"── 完成：退出码 {p.ExitCode} · C 盘可用 {HumanSize.Format(before)} → {HumanSize.Format(after)} ──");
            LedgerStore.Append(LedgerFactory.FromTool(label, $"退出码 {p.ExitCode}，可用空间 {HumanSize.Format(before)} → {HumanSize.Format(after)}", before, after));
            AppServices.Current.NotifyDiskStats();
            UiShell.SetStatus?.Invoke(label + " 完成");
        }
        catch (Exception ex)
        {
            AppendDism("! 执行失败：" + ex.Message);
            AppLog.Exception(label + " 失败", ex);
        }
        finally
        {
            _dismRunning = false;
            BtnDismAnalyze.IsEnabled = true;
            BtnDismClean.IsEnabled = true;
            UiShell.SetBusy?.Invoke(false);
        }
    }

    private void AppendDism(string line)
    {
        TxtDismOutput.AppendText(line + Environment.NewLine);
        TxtDismOutput.ScrollToEnd();
    }

    // ================= 已装软件 =================

    private void BtnLoadSoft_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _software = LoadInstalledSoftware();
            ApplySoftFilter();
            TxtSoftInfo.Text = $"共 {_software.Count} 个软件";
            AppLog.Info($"已装软件列表：{_software.Count} 项");
        }
        catch (Exception ex)
        {
            AppLog.Exception("读取已装软件失败", ex);
            Ui.Warn("读取失败：" + ex.Message);
        }
    }

    private void TxtSoftFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplySoftFilter();

    private void ApplySoftFilter()
    {
        var q = TxtSoftFilter.Text.Trim();
        var list = string.IsNullOrEmpty(q)
            ? _software
            : _software.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || s.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        LvSoftware.ItemsSource = list;
        if (_software.Count > 0)
            TxtSoftInfo.Text = string.IsNullOrEmpty(q) ? $"共 {_software.Count} 个软件" : $"筛选出 {list.Count} / {_software.Count}";
    }

    private static List<SoftwareRow> LoadInstalledSoftware()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<SoftwareRow>();

        void Read(RegistryKey? root, string path)
        {
            if (root == null) return;
            using var key = root.OpenSubKey(path);
            if (key == null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                try
                {
                    using var k = key.OpenSubKey(sub);
                    if (k == null) continue;
                    var name = k.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (k.GetValue("ParentKeyName") is string pk && !string.IsNullOrEmpty(pk)) continue;
                    var version = k.GetValue("DisplayVersion") as string ?? "";
                    if (!seen.Add(name + "|" + version)) continue;
                    var sizeKb = k.GetValue("EstimatedSize") is int es ? (long)es : 0;
                    var date = k.GetValue("InstallDate") as string ?? "";
                    if (date.Length == 8 && date.All(char.IsDigit)) date = $"{date[..4]}-{date.Substring(4, 2)}-{date.Substring(6, 2)}";
                    rows.Add(new SoftwareRow
                    {
                        Name = name,
                        Version = version,
                        Publisher = k.GetValue("Publisher") as string ?? "",
                        SizeText = sizeKb > 0 ? HumanSize.Format(sizeKb * 1024) : "—",
                        InstallDate = date,
                        Location = k.GetValue("InstallLocation") as string ?? "",
                    });
                }
                catch
                {
                }
            }
        }

        try { Read(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"); } catch { }
        try { Read(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32), @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"); } catch { }
        try { Read(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"); } catch { }

        return rows.OrderByDescending(r => ParseSize(r.SizeText)).ThenBy(r => r.Name).ToList();
    }

    private static long ParseSize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var parts = text.Replace("—", "0 B").Trim().Split(' ', 2);
        if (parts.Length != 2) return 0;
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return 0;
        return parts[1].ToUpperInvariant() switch
        {
            "B" => (long)v,
            "KB" => (long)(v * 1024),
            "MB" => (long)(v * 1048576),
            "GB" => (long)(v * 1073741824L),
            "TB" => (long)(v * 1099511627776L),
            _ => 0,
        };
    }

    private void BtnOpenApps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Ui.Warn("无法打开系统设置：" + ex.Message);
        }
    }

    // ================= 规则与数据 =================

    private void BtnOpenDataDir_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(AppPaths.DataRoot);
    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(AppPaths.LogsDir);

    private void BtnOpenRules_Click(object sender, RoutedEventArgs e)
    {
        var file = AppPaths.RulesFile;
        try
        {
            if (File.Exists(file))
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = false });
            else
                Ui.Warn("未找到规则库文件：\n" + file);
        }
        catch (Exception ex)
        {
            Ui.Warn("无法打开规则库：" + ex.Message);
        }
    }

    private void BtnReloadRules_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Current.ReloadRules();
        (Window.GetWindow(this) as MainWindow)?.ShowRulesState();
        var s = AppServices.Current;
        if (s.Library != null)
        {
            TxtRuleCheck.Text = $"规则库已重载：{s.Library.Categories.Count(c => c.Enabled)} 个启用类别。";
            UiShell.SetStatus?.Invoke("规则库已重载");
        }
        else
        {
            TxtRuleCheck.Text = "重载失败：" + (s.RulesError ?? "未知错误");
        }
    }

    private void BtnCheckRules_Click(object sender, RoutedEventArgs e)
    {
        var s = AppServices.Current;
        if (s.Library == null)
        {
            TxtRuleCheck.Text = "规则库未加载：" + (s.RulesError ?? "请先重载");
            return;
        }

        var problems = Core.Rules.RuleLibraryLoader.Validate(s.Library);
        TxtRuleCheck.Text = problems.Count == 0
            ? $"校验通过：{s.Library.Categories.Count} 个类别（安全 {s.Library.Categories.Count(c => c.Risk == Core.Models.RiskLevel.Safe)} / 谨慎 {s.Library.Categories.Count(c => c.Risk == Core.Models.RiskLevel.Moderate)} / 高风险 {s.Library.Categories.Count(c => c.Risk == Core.Models.RiskLevel.High)}）。"
            : "发现 " + problems.Count + " 个问题：\n" + string.Join("\n", problems.Take(10));
    }

    private void BtnBrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "选择备份区目录", SelectedPath = TxtBackupRoot.Text, UseDescriptionForTitle = true };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TxtBackupRoot.Text = dlg.SelectedPath;
    }

    private void BtnSaveBackup_Click(object sender, RoutedEventArgs e)
    {
        var path = PathUtil.Normalize(TxtBackupRoot.Text);
        if (path.Length == 0)
        {
            Ui.Warn("请输入有效的目录路径。");
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            Ui.Warn("无法创建该目录：" + ex.Message);
            return;
        }

        var cfg = AppServices.Current.Config;
        cfg.BackupRoot = path;
        ConfigStore.Save(cfg);
        TxtBackupRoot.Text = path;
        Ui.Info("备份区位置已保存：\n" + path, "设置");
    }

    // ================= 规则在线更新 =================

    private async void BtnRuleCheck_Click(object sender, RoutedEventArgs e) =>
        await RunRuleCheckAsync(TxtRuleUrl.Text.Trim());

    private async void BtnRuleImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "规则文件 (*.json)|*.json|所有文件|*.*",
            Title = "选择要导入的规则文件",
        };
        if (dlg.ShowDialog() != true) return;
        TxtRuleUrl.Text = dlg.FileName;
        await RunRuleCheckAsync(dlg.FileName);
    }

    private async Task RunRuleCheckAsync(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            Ui.Warn("请先填写更新地址（https://… 或本地文件路径），或使用「从本地文件导入」。");
            return;
        }

        BtnRuleCheck.IsEnabled = false;
        BtnRuleApply.IsEnabled = false;
        TxtRuleUpdate.Text = "正在下载并校验…";
        try
        {
            var check = await Task.Run(() => RuleUpdater.Fetch(source, AppServices.Current.Library));
            _pendingUpdate = check;
            TxtRuleUpdate.Text = FormatUpdateResult(check);
            BtnRuleApply.IsEnabled = check.Success;
            if (check.Success)
            {
                var cfg = AppServices.Current.Config;
                cfg.RuleUpdateUrl = source;
                ConfigStore.Save(cfg);
            }
        }
        finally
        {
            BtnRuleCheck.IsEnabled = true;
        }
    }

    private static string FormatUpdateResult(RuleUpdateCheck check)
    {
        var sb = new StringBuilder();
        if (!check.Success)
        {
            sb.AppendLine("检查失败：" + check.Message);
            foreach (var p in check.Problems) sb.AppendLine("  - " + p);
        }
        else
        {
            sb.AppendLine("差异：" + check.DiffSummary);
            foreach (var w in check.Warnings) sb.AppendLine("提示：" + w);
            sb.Append("校验通过，可应用更新（旧版将自动备份，可回退）。");
        }

        return sb.ToString().TrimEnd();
    }

    private async void BtnRuleApply_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null || !_pendingUpdate.Success)
        {
            Ui.Warn("请先执行「检查更新」。");
            return;
        }

        var dlg = new ConfirmDialog("应用规则更新",
            "将用新规则替换当前生效规则（旧版自动备份，可随时回退）。\n\n差异：" + _pendingUpdate.DiffSummary,
            _pendingUpdate.Warnings.Take(6).ToList(), okText: "应用更新", danger: false) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        BtnRuleApply.IsEnabled = false;
        var applied = await Task.Run(() => RuleUpdater.Apply(_pendingUpdate));
        TxtRuleUpdate.Text = applied.Message;
        if (applied.Success)
        {
            AppServices.Current.ReloadRules();
            (Window.GetWindow(this) as MainWindow)?.ShowRulesState();
            _pendingUpdate = null;
            Ui.Info("规则已更新并重新加载。\n\n" + applied.Message, "更新成功");
        }
        else
        {
            Ui.Warn(applied.Message);
        }
    }
}
