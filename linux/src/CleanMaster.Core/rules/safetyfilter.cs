using CleanMaster.Core.Config;
using CleanMaster.Core.Util;

namespace CleanMaster.Core.Rules;

/// <summary>安全检查结论。</summary>
public sealed class SafetyDecision
{
    public bool Allowed { get; init; }
    public string Reason { get; init; } = "";

    public static SafetyDecision Ok => new() { Allowed = true };
    public static SafetyDecision Deny(string reason) => new() { Allowed = false, Reason = reason };
}

/// <summary>
/// 安全保护名单（Linux 版）：任何删除/移动动作前都必须通过本过滤器。
/// 原则：宁可误挡（跳过清理），绝不误删（保护系统与个人数据）。
/// </summary>
public sealed class SafetyFilter
{
    private readonly List<string> _exactDeny = new();
    private readonly List<string> _treeDeny = new();
    private readonly List<string> _allowedExactFiles = new();
    private readonly Dictionary<string, List<string>> _conditionalTrees = new(StringComparer.Ordinal);
    private readonly List<string> _customDeny = new();

    /// <summary>文件系统根下直接命名的关键文件（/vmlinuz 等，多为指向 /boot 的符号链接）。</summary>
    private static readonly string[] RootFileDeny =
    {
        "vmlinuz", "vmlinuz.old", "initrd.img", "initrd.img.old",
        "swapfile", "swap.img", ".dockerenv",
    };

    private static readonly HashSet<string> SensitiveFirstSegments = new(StringComparer.Ordinal)
    {
        "proc", "sys", "dev", "run", "lost+found",
    };

    public static SafetyFilter CreateDefault()
    {
        var f = new SafetyFilter();
        var home = PathUtil.HomeDir;
        var user = Environment.UserName;

        // 相等即拒绝（系统/用户根目录）
        foreach (var p in new[]
                 {
                     "/", home, "/root", "/home",
                     Path.Combine(home, ".local"), Path.Combine(home, ".config"),
                     Path.Combine(home, ".local", "share"),
                 })
        {
            if (!string.IsNullOrEmpty(p)) f._exactDeny.Add(PathUtil.Normalize(p));
        }

        // 位于其下即拒绝（个人数据与高敏感区）
        foreach (var p in new[]
                 {
                     Path.Combine(home, "Documents"), Path.Combine(home, "Desktop"),
                     Path.Combine(home, "Pictures"), Path.Combine(home, "Videos"),
                     Path.Combine(home, "Music"), Path.Combine(home, "Templates"),
                     Path.Combine(home, ".ssh"), Path.Combine(home, ".gnupg"),
                     Path.Combine(home, ".password-store"), Path.Combine(home, ".aws"),
                     Path.Combine(home, ".azure"), Path.Combine(home, ".kube"),
                     Path.Combine(home, ".config", "git"), Path.Combine(home, ".local", "share", "keyrings"),
                     Path.Combine(home, ".local", "share", "Trash"),
                     Path.Combine(home, ".snap"), Path.Combine(home, "snap"),
                     AppPaths.DataRoot, // 自身数据目录自保护
                 })
        {
            if (!string.IsNullOrEmpty(p)) f._treeDeny.Add(PathUtil.Normalize(p));
        }

        // 条件树：默认整树保护，仅白名单子路径可清理
        AddConditional(f, "/proc");
        AddConditional(f, "/sys");
        AddConditional(f, "/dev");
        AddConditional(f, "/run");
        AddConditional(f, "/usr");
        AddConditional(f, "/etc");
        AddConditional(f, "/opt");
        AddConditional(f, "/boot");
        AddConditional(f, "/snap");
        AddConditional(f, "/lib");
        AddConditional(f, "/lib64");
        AddConditional(f, "/bin");
        AddConditional(f, "/sbin");
        AddConditional(f, "/var",
            "cache/apt/archives", "cache/dnf", "cache/yum", "cache/pacman/pkg", "tmp", "log");

        // 多用户主机：只允许清理当前用户的 home
        if (!string.IsNullOrEmpty(user) && user != "root")
            f._conditionalTrees["/home"] = new List<string> { user };

        return f;
    }

    private static void AddConditional(SafetyFilter f, string root, params string[] allowed)
    {
        f._conditionalTrees[root] = new List<string>(allowed);
    }

    /// <summary>追加自定义保护路径（来自 protected.json）。</summary>
    public void AddCustomDeny(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var n = PathUtil.Normalize(p);
            if (n.Length > 0) _customDeny.Add(n);
        }
    }

    public IReadOnlyList<string> CustomDenyList => _customDeny;

    /// <summary>快速检查（无磁盘 IO）：用于逐文件热路径。</summary>
    public SafetyDecision Check(string fullPath)
    {
        var p = PathUtil.Normalize(fullPath);
        if (p.Length == 0) return SafetyDecision.Deny("空路径");
        if (PathUtil.HasWildcard(p)) return SafetyDecision.Deny("路径含未解析通配符");
        if (PathUtil.IsDriveRoot(p)) return SafetyDecision.Deny("文件系统根目录");

        var root = Path.GetPathRoot(p) ?? "";
        if (root.Length == 0) return SafetyDecision.Deny("无根路径");

        // 根下敏感文件（vmlinuz / initrd / swap 等）
        var parent = Path.GetDirectoryName(p);
        if (parent != null && PathUtil.IsDriveRoot(parent))
        {
            var name = Path.GetFileName(p);
            foreach (var rf in RootFileDeny)
                if (string.Equals(name, rf, StringComparison.Ordinal))
                    return SafetyDecision.Deny("系统关键文件");
        }

        // 根下敏感一级目录（proc / sys / dev 等）
        var rel = p.Substring(root.Length);
        var split = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (split.Length > 0 && SensitiveFirstSegments.Contains(split[0]))
            return SafetyDecision.Deny("系统保护目录");

        foreach (var d in _exactDeny)
            if (PathUtil.EqualsPath(p, d))
                return SafetyDecision.Deny("系统/用户根目录");

        foreach (var d in _treeDeny)
            if (PathUtil.IsUnder(p, d))
                return SafetyDecision.Deny("受保护目录(个人数据/敏感区)");

        foreach (var d in _customDeny)
            if (PathUtil.IsUnder(p, d))
                return SafetyDecision.Deny("自定义保护路径");

        foreach (var kv in _conditionalTrees)
        {
            if (!PathUtil.IsUnder(p, kv.Key)) continue;
            foreach (var allowed in kv.Value)
                if (PathUtil.IsUnder(p, Path.Combine(kv.Key, allowed)))
                    return SafetyDecision.Ok;
            foreach (var af in _allowedExactFiles)
                if (PathUtil.EqualsPath(p, af))
                    return SafetyDecision.Ok;
            return SafetyDecision.Deny("系统目录保护");
        }

        return SafetyDecision.Ok;
    }

    /// <summary>含磁盘 IO 的检查：额外拒绝符号链接目标本身。</summary>
    public SafetyDecision CheckWithReparse(string fullPath)
    {
        var quick = Check(fullPath);
        if (!quick.Allowed) return quick;
        try
        {
            var attr = File.GetAttributes(fullPath);
            if ((attr & FileAttributes.ReparsePoint) != 0)
                return SafetyDecision.Deny("符号链接");
        }
        catch
        {
            // 无法读取属性时放行（删除阶段会以文件系统错误再次兜底）
        }

        return SafetyDecision.Ok;
    }
}
