namespace CleanMaster.Core.Models;

/// <summary>扫描到的文件条目（用于预览展示）。</summary>
public readonly record struct FileItem(string Path, long Size, DateTime LastWriteUtc);

/// <summary>单个类别的扫描结果。</summary>
public sealed class CategoryScanResult
{
    public string CategoryId { get; set; } = "";
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    public RiskLevel Risk { get; set; }
    public CleanStrategy Strategy { get; set; }
    public bool RequiresAdmin { get; set; }
    public bool Recommend { get; set; }
    public bool DefaultChecked { get; set; }

    /// <summary>匹配到的文件总数。</summary>
    public long TotalFiles { get; set; }

    /// <summary>匹配到的总字节数。</summary>
    public long TotalBytes { get; set; }

    /// <summary>扫描中发现无权限访问而被跳过的目录数。</summary>
    public long SkippedAccessDirs { get; set; }

    /// <summary>扫描中被跳过的符号链接/联接点数量（防重复计数与死循环）。</summary>
    public long SkippedReparse { get; set; }

    /// <summary>按大小排序的预览条目（Top N）。</summary>
    public List<FileItem> TopItems { get; set; } = new();

    /// <summary>实际扫描到的具体根路径（用于展示与日志）。</summary>
    public List<string> Roots { get; set; } = new();

    /// <summary>提示信息（例如浏览器正在运行）。</summary>
    public List<string> WarnNotes { get; set; } = new();

    /// <summary>访问错误样本（最多保留若干条）。</summary>
    public List<string> AccessErrors { get; set; } = new();

    /// <summary>存在需要管理员权限的访问问题。</summary>
    public bool NeedsAdminHint { get; set; }

    public DateTime ScannedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>扫描进度。</summary>
public sealed class ScanProgress
{
    public string Phase { get; set; } = "";
    public int CategoriesTotal { get; set; }
    public int CategoriesDone { get; set; }
    public string CurrentCategory { get; set; } = "";
    public long Files { get; set; }
    public long Bytes { get; set; }
    public long Errors { get; set; }
    public double Seconds { get; set; }
}

/// <summary>一次完整扫描的汇总。</summary>
public sealed class ScanSummary
{
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public bool Cancelled { get; set; }
    public bool Admin { get; set; }
    public long TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public List<CategoryScanResult> Categories { get; set; } = new();

    public double ElapsedSeconds => (FinishedAtUtc - StartedAtUtc).TotalSeconds;
}
