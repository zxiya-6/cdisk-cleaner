namespace CleanMaster.Core.Models;

/// <summary>清理模式（用户在确认步骤选择）。</summary>
public enum CleanMode
{
    /// <summary>标准：按规则库中每个类别自带的策略执行（推荐）。</summary>
    Standard = 0,

    /// <summary>全部通道备份区：一切先移入备份区，确保可恢复；清空备份区后释放空间。</summary>
    AllToBackup = 1,

    /// <summary>全部永久删除：直接释放空间，不可恢复（需强确认）。</summary>
    AllPermanent = 2,
}

/// <summary>单个类别的清理选择。</summary>
public sealed class CleanSelectionItem
{
    public string CategoryId { get; set; } = "";

    /// <summary>覆盖规则里的「只清理 N 天前文件」（-1 = 使用规则默认）。</summary>
    public int MinAgeDays { get; set; } = -1;

    /// <summary>用户逐项排除的文件全路径（不区分大小写）。</summary>
    public List<string> ExcludePaths { get; set; } = new();
}

/// <summary>一次清理请求。</summary>
public sealed class CleanRequest
{
    public CleanMode Mode { get; set; } = CleanMode.Standard;
    public List<CleanSelectionItem> Items { get; set; } = new();
    public bool CreateRestorePoint { get; set; }
    public bool DryRun { get; set; }
    public string? Label { get; set; }
}

/// <summary>被跳过的条目及原因。</summary>
public sealed class SkippedItem
{
    public string Path { get; set; } = "";
    public string Reason { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>单个类别的清理结果。</summary>
public sealed class CategoryCleanResult
{
    public string CategoryId { get; set; } = "";
    public string Name { get; set; } = "";

    public long DeletedFiles { get; set; }
    public long DeletedBytes { get; set; }
    public long BackedUpFiles { get; set; }
    public long BackedUpBytes { get; set; }
    public long TrashedFiles { get; set; }
    public long TrashedBytes { get; set; }

    public long SkippedFiles { get; set; }
    public long SkippedBytes { get; set; }

    /// <summary>跳过详情（最多保留若干条，完整数量在 SkippedFiles）。</summary>
    public List<SkippedItem> Skipped { get; set; } = new();

    public List<string> Errors { get; set; } = new();
    public bool Cancelled { get; set; }
    public double ElapsedSeconds { get; set; }
}

/// <summary>清理进度。</summary>
public sealed class CleanProgress
{
    public string Phase { get; set; } = "";
    public string CurrentCategory { get; set; } = "";
    public string CurrentPath { get; set; } = "";
    public int CategoriesTotal { get; set; }
    public int CategoriesDone { get; set; }
    public long DoneFiles { get; set; }
    public long DoneBytes { get; set; }
    public long FreedBytes { get; set; }
    public long Skipped { get; set; }
    public double Seconds { get; set; }
}

/// <summary>一次清理的完整报告。</summary>
public sealed class CleanReport
{
    public string SessionId { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public CleanMode Mode { get; set; }
    public bool Admin { get; set; }
    public bool DryRun { get; set; }
    public bool Cancelled { get; set; }

    /// <summary>清理前 C 盘可用空间（字节）。</summary>
    public long FreeBeforeBytes { get; set; }

    /// <summary>清理后 C 盘可用空间（字节）。</summary>
    public long FreeAfterBytes { get; set; }

    /// <summary>永久删除的字节数（= 立即释放）。</summary>
    public long DeletedBytes { get; set; }

    /// <summary>移入备份区的字节数（清空备份区后释放）。</summary>
    public long BackedUpBytes { get; set; }

    /// <summary>移到回收站的字节数（清空回收站后释放）。</summary>
    public long TrashedBytes { get; set; }

    /// <summary>备份会话 ID（如果有文件进入备份区）。</summary>
    public string? BackupSessionId { get; set; }

    /// <summary>系统还原点创建结果描述（未创建则为 null）。</summary>
    public string? RestorePointResult { get; set; }

    public List<CategoryCleanResult> Categories { get; set; } = new();
    public List<string> Errors { get; set; } = new();

    public double ElapsedSeconds => (FinishedAtUtc - StartedAtUtc).TotalSeconds;

    /// <summary>本次清理立即释放的字节数。</summary>
    public long ReleasedBytes => DeletedBytes;

    public long TotalSkippedFiles { get { long n = 0; foreach (var c in Categories) n += c.SkippedFiles; return n; } }
}
