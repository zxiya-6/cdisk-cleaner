namespace CleanMaster.App;

/// <summary>主窗口外壳与视图之间的通信桥（状态栏、进度、取消）。</summary>
public static class UiShell
{
    public static Action<string>? SetStatus { get; set; }
    public static Action<bool>? SetBusy { get; set; }

    /// <summary>(百分比 0-100, 是否不确定进度)</summary>
    public static Action<double, bool>? SetProgress { get; set; }

    /// <summary>设置当前可取消操作（null 表示无）。</summary>
    public static Action<Action?>? SetCancel { get; set; }

    /// <summary>右侧汇总文本。</summary>
    public static Action<string>? SetSummary { get; set; }

    public static void Status(string text) => SetStatus?.Invoke(text);
    public static void Summary(string text) => SetSummary?.Invoke(text);
}
