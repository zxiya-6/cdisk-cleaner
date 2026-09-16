using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using CleanMaster.Core.Models;

namespace CleanMaster.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>清理类别行。</summary>
public sealed class CategoryRow : ObservableObject
{
    public string CategoryId { get; init; } = "";
    public CategoryScanResult? Result { get; set; }

    public string Name { get; init; } = "";
    public string Note { get; init; } = "";
    public string Group { get; init; } = "";
    public RiskLevel Risk { get; init; }
    public CleanStrategy Strategy { get; init; }
    public bool Recommend { get; init; }
    public bool RequiresAdmin { get; init; }

    public string RiskText { get; init; } = "";
    public Brush RiskBrush { get; init; } = Brushes.Gray;
    public string FilesText { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string NoteText => Note;
    public string StrategyText { get; init; } = "";
    public string RecommendText => Recommend ? "推荐" : "";

    public long SizeBytes { get; init; }
    public long FileCount { get; init; }

    private bool _isChecked;

    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    public static CategoryRow From(CategoryScanResult r, bool checkedState)
    {
        return new CategoryRow
        {
            CategoryId = r.CategoryId,
            Result = r,
            Name = r.Name,
            Note = r.Note,
            Group = r.Group,
            Risk = r.Risk,
            Strategy = r.Strategy,
            Recommend = r.Recommend,
            RequiresAdmin = r.RequiresAdmin,
            RiskText = AppServices.RiskText(r.Risk),
            RiskBrush = AppServices.RiskBrush(r.Risk),
            FilesText = r.TotalFiles > 0 ? $"{r.TotalFiles:N0}" : "0",
            SizeText = r.TotalFiles > 0 ? CleanMaster.Core.Util.HumanSize.Format(r.TotalBytes) : "—",
            StrategyText = AppServices.StrategyText(r.Strategy),
            SizeBytes = r.TotalBytes,
            FileCount = r.TotalFiles,
            IsChecked = checkedState,
        };
    }
}

/// <summary>明细文件行（可勾选含/排除）。</summary>
public sealed class DetailFileRow : ObservableObject
{
    public string FullPath { get; init; } = "";
    public long Size { get; init; }
    public string SizeText { get; init; } = "";
    public string DateText { get; init; } = "";
    public DateTime LastWriteUtc { get; init; }

    private bool _included = true;

    public bool IsIncluded
    {
        get => _included;
        set => Set(ref _included, value);
    }
}

/// <summary>备份会话行。</summary>
public sealed class SessionRow : ObservableObject
{
    public BackupSessionInfo Info { get; init; } = new();
    public string Id => Info.Id;
    public string CreatedText { get; init; } = "";
    public string RemainingText => $"{Info.FilesRemaining:N0}";
    public string BytesText => CleanMaster.Core.Util.HumanSize.Format(Info.BytesRemaining);
    public string RestoredText => $"{Info.RestoredFiles:N0}";
    public string PurgedText => $"{Info.PurgedFiles:N0}";

    public string CategoriesText => Info.Categories.Count == 0
        ? "—"
        : string.Join("、", Info.Categories.Take(6)) + (Info.Categories.Count > 6 ? "…" : "");

    private bool _isChecked;

    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }
}

/// <summary>台账记录行。</summary>
public sealed class LedgerRow
{
    public LedgerEntry Entry { get; init; } = new();
    public string TimeText { get; init; } = "";
    public string KindText { get; init; } = "";
    public string ModeText { get; init; } = "";
    public string FilesText { get; init; } = "";
    public string ReleasedText { get; init; } = "";
    public string BackedText { get; init; } = "";
    public string TrashedText { get; init; } = "";
    public string SkippedText { get; init; } = "";
    public string StatusText { get; init; } = "";
    public string NotesText { get; init; } = "";
}

/// <summary>已装软件行（只读展示）。</summary>
public sealed class SoftwareRow
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string InstallDate { get; init; } = "";
    public string Location { get; init; } = "";
}

/// <summary>大文件行。</summary>
public sealed class BigFileRow : ObservableObject
{
    public string FullPath { get; init; } = "";
    public long Size { get; init; }
    public string SizeText { get; init; } = "";
    public string DateText { get; init; } = "";

    private bool _isChecked;

    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }
}
