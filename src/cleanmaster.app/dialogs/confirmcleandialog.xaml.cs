using System.Windows;
using CleanMaster.App.ViewModels;
using CleanMaster.Core.Models;
using CleanMaster.Core.Util;

namespace CleanMaster.App.Dialogs;

public partial class ConfirmCleanDialog : Window
{
    private readonly List<CategoryRow> _rows;
    private readonly bool _hasHighRisk;
    private readonly bool _hasModerate;

    public bool Confirmed { get; private set; }
    public CleanMode SelectedMode { get; private set; } = CleanMode.Standard;
    public bool CreateRestorePoint { get; private set; }

    public ConfirmCleanDialog(IEnumerable<CategoryRow> selectedRows)
    {
        InitializeComponent();
        _rows = selectedRows.ToList();
        foreach (var r in _rows) LvItems.Items.Add(r);

        long files = 0, bytes = 0;
        foreach (var r in _rows)
        {
            files += r.FileCount;
            bytes += r.SizeBytes;
        }

        _hasHighRisk = _rows.Any(r => r.Risk == RiskLevel.High);
        _hasModerate = _rows.Any(r => r.Risk == RiskLevel.Moderate);

        TxtHeader.Text = $"确认清理 {_rows.Count} 个类别";
        TxtTotals.Text = $"共 {files:N0} 个文件 · 预计处理 {HumanSize.Format(bytes)}。以下内容将被删除或移动：";

        var warnings = new List<string>();
        if (_hasHighRisk)
        {
            var names = _rows.Where(r => r.Risk == RiskLevel.High).Select(r => "「" + r.Name + "」");
            warnings.Add("高风险项：" + string.Join("、", names) + " — 后果不可逆，请务必确认。");
        }

        if (_hasModerate)
        {
            var names = _rows.Where(r => r.Risk == RiskLevel.Moderate).Select(r => "「" + r.Name + "」");
            warnings.Add("谨慎项：" + string.Join("、", names) + " — 删除后部分使用体验会受影响（会另行说明）。");
        }

        if (!SystemInfo.IsAdmin() && _rows.Any(r => r.RequiresAdmin))
            warnings.Add("部分类别需要管理员权限，未提升权限的项目将被自动跳过（可关闭本窗口后用「以管理员身份重启」）。");

        var warnNotes = _rows.SelectMany(r => r.Result?.WarnNotes ?? new List<string>()).Distinct().Take(4).ToList();
        warnings.AddRange(warnNotes);

        TxtWarnings.Text = warnings.Count > 0 ? string.Join("\n", warnings) : "全部为安全级项目，可放心清理。";

        if (_hasHighRisk)
        {
            ChkAck.Visibility = Visibility.Visible;
            ChkAck.Checked += (_, _) => BtnOk.IsEnabled = true;
            ChkAck.Unchecked += (_, _) => BtnOk.IsEnabled = false;
            BtnOk.IsEnabled = false;
        }

        AppLog.Info($"确认清理对话框已就绪（{_rows.Count} 个类别，高风险={_hasHighRisk}）");
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (_hasHighRisk && ChkAck.IsChecked != true) return;
        SelectedMode = RbBackup.IsChecked == true ? CleanMode.AllToBackup
            : RbPermanent.IsChecked == true ? CleanMode.AllPermanent
            : CleanMode.Standard;
        CreateRestorePoint = ChkRestorePoint.IsChecked == true;
        Confirmed = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();
}
