namespace CleanMaster.Core.Models;

/// <summary>清理项风险等级。</summary>
public enum RiskLevel
{
    /// <summary>安全：纯缓存/临时文件，删除后系统或应用自动重建。</summary>
    Safe = 0,

    /// <summary>谨慎：删除会影响部分使用体验（如浏览历史、登录状态、回收站）。</summary>
    Moderate = 1,

    /// <summary>高风险：删除影响较大或不可逆，需要二次确认。</summary>
    High = 2,
}

/// <summary>清理策略。</summary>
public enum CleanStrategy
{
    /// <summary>永久删除（立即释放空间，不可恢复）。适用于可自动重建的缓存。</summary>
    Delete = 0,

    /// <summary>移动到备份区（可一键恢复；清空备份区后释放空间）。</summary>
    Backup = 1,

    /// <summary>移到 Windows 回收站（可从回收站恢复）。</summary>
    Trash = 2,
}

/// <summary>规则类型。</summary>
public enum RuleKind
{
    /// <summary>清理目录内容（递归，按文件名模式过滤）。</summary>
    DirContents = 0,

    /// <summary>清理指定的文件（路径最后一段是文件名模式）。</summary>
    Files = 1,

    /// <summary>清空回收站（特殊：通过 Shell API 执行）。</summary>
    EmptyRecycleBin = 2,
}

/// <summary>规则库（rules.json 根对象）。</summary>
public sealed class RuleLibrary
{
    public int SchemaVersion { get; set; } = 1;
    public string? Updated { get; set; }
    public List<CategoryRule> Categories { get; set; } = new();
}

/// <summary>单个清理类别规则。</summary>
public sealed class CategoryRule
{
    /// <summary>稳定标识（用于设置、台账、CLI 参数）。</summary>
    public string Id { get; set; } = "";

    /// <summary>分组名（界面上的分组标题）。</summary>
    public string Group { get; set; } = "";

    /// <summary>显示名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>说明（副文案，讲清楚会删什么、有没有副作用）。</summary>
    public string Note { get; set; } = "";

    /// <summary>规则类型。</summary>
    public RuleKind Kind { get; set; } = RuleKind.DirContents;

    /// <summary>目标路径（支持 %ENV% 环境变量；目录段和文件名段可用 * 和 ? 通配）。</summary>
    public List<string> Paths { get; set; } = new();

    /// <summary>文件过滤模式（对文件名匹配，默认全部）。</summary>
    public List<string> IncludeFiles { get; set; } = new() { "*" };

    /// <summary>排除的文件名模式。</summary>
    public List<string> ExcludeFiles { get; set; } = new();

    /// <summary>清理策略。</summary>
    public CleanStrategy Strategy { get; set; } = CleanStrategy.Delete;

    /// <summary>风险等级。</summary>
    public RiskLevel Risk { get; set; } = RiskLevel.Safe;

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>扫描后默认是否勾选。</summary>
    public bool DefaultChecked { get; set; } = false;

    /// <summary>是否出现在「一键推荐」组合里。</summary>
    public bool Recommend { get; set; } = false;

    /// <summary>是否必须管理员权限。</summary>
    public bool RequiresAdmin { get; set; } = false;

    /// <summary>只清理超过 N 天未修改的文件（0 = 不限制）。</summary>
    public int MinAgeDays { get; set; } = 0;

    /// <summary>当这些进程在运行时给出提示（进程名不含 .exe，小写）。</summary>
    public List<string> WarnProcesses { get; set; } = new();
}
