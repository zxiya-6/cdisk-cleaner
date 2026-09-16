namespace CleanMaster.Core.Models;

/// <summary>备份区会话概览。</summary>
public sealed class BackupSessionInfo
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public long TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public long RestoredFiles { get; set; }
    public long PurgedFiles { get; set; }
    public List<string> Categories { get; set; } = new();

    /// <summary>仍在备份区中的文件数。</summary>
    public long FilesRemaining { get; set; }

    /// <summary>仍在备份区中的字节数。</summary>
    public long BytesRemaining { get; set; }
}

/// <summary>恢复冲突处理策略。</summary>
public enum ConflictPolicy
{
    /// <summary>跳过（原位置已存在文件）。</summary>
    Skip = 0,

    /// <summary>保留两者：恢复为「原名_恢复_时间戳.扩展名」。</summary>
    RenameAndRestore = 1,

    /// <summary>覆盖（原位置文件先删除）。</summary>
    Overwrite = 2,
}

/// <summary>恢复选项。</summary>
public sealed class RestoreOptions
{
    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.RenameAndRestore;

    /// <summary>只恢复这些原始路径（null = 全部）。</summary>
    public List<string>? OnlyOriginalPaths { get; set; }
}

/// <summary>恢复结果。</summary>
public sealed class RestoreReport
{
    public string SessionId { get; set; } = "";
    public long RestoredFiles { get; set; }
    public long RestoredBytes { get; set; }
    public long SkippedFiles { get; set; }
    public long FailedFiles { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Skipped { get; set; } = new();
}

/// <summary>清空备份区结果。</summary>
public sealed class PurgeReport
{
    public string SessionId { get; set; } = "";
    public bool All { get; set; }
    public long PurgedFiles { get; set; }
    public long PurgedBytes { get; set; }
    public long FailedFiles { get; set; }
    public List<string> Errors { get; set; } = new();
}
