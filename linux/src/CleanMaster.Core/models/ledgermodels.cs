namespace CleanMaster.Core.Models;

/// <summary>台账记录（每次清理/恢复/清空备份区/工具操作一条）。</summary>
public sealed class LedgerEntry
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>记录 ID（时间戳+随机后缀）。</summary>
    public string Id { get; set; } = "";

    /// <summary>类型：clean / restore / purge / tool。</summary>
    public string Kind { get; set; } = "clean";

    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }

    /// <summary>清理模式（standard / allToBackup / allPermanent）或操作说明。</summary>
    public string Mode { get; set; } = "";

    public bool Admin { get; set; }
    public bool Cancelled { get; set; }
    public bool DryRun { get; set; }

    /// <summary>操作前 HOME 所在分区可用空间。</summary>
    public long FreeBeforeBytes { get; set; }

    /// <summary>操作后 HOME 所在分区可用空间。</summary>
    public long FreeAfterBytes { get; set; }

    /// <summary>本次立即释放的字节数。</summary>
    public long ReleasedBytes { get; set; }

    /// <summary>移入备份区的字节数。</summary>
    public long BackedUpBytes { get; set; }

    /// <summary>移到回收站的字节数。</summary>
    public long TrashedBytes { get; set; }

    public string? BackupSessionId { get; set; }

    public string ProductVersion { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Os { get; set; } = "";

    public List<LedgerCategory> Categories { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public string? Notes { get; set; }

    public double ElapsedSeconds => (FinishedAtUtc - StartedAtUtc).TotalSeconds;
}

/// <summary>台账中的单类别摘要。</summary>
public sealed class LedgerCategory
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long Files { get; set; }
    public long Bytes { get; set; }
    public string Strategy { get; set; } = "";
    public long Skipped { get; set; }
}
