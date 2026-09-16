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
/// 安全保护名单：任何删除/移动动作前都必须通过本过滤器。
/// 原则：宁可误挡（跳过清理），绝不误删（保护系统与个人数据）。
/// </summary>
public sealed class SafetyFilter
{
    private readonly List<string> _exactDeny = new();
    private readonly List<string> _treeDeny = new();
    private readonly List<string> _allowedExactFiles = new();
    private readonly Dictionary<string, List<string>> _conditionalTrees = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _customDeny = new();

    private static readonly string[] RootFileDeny =
    {
        "pagefile.sys", "swapfile.sys", "hiberfil.sys",
        "DumpStack.log", "DumpStack.log.tmp", "bootmgr", "BOOTNXT", "BOOTSECT.BAK",
    };

    private static readonly HashSet<string> SensitiveFirstSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "$recycle.bin", "system volume information", "recovery", "boot",
        "config.msi", "$winreagent", "$sysreset", "$windows.~bt", "$windows.~ws",
        "perflogs", "msocache",
    };

    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try { return PathUtil.Normalize(Environment.GetFolderPath(folder)); }
        catch { return ""; }
    }

    public static SafetyFilter CreateDefault()
    {
        var f = new SafetyFilter();
        var sys = SafeFolder(Environment.SpecialFolder.Windows);
        var pf = SafeFolder(Environment.SpecialFolder.ProgramFiles);
        var pf86 = SafeFolder(Environment.SpecialFolder.ProgramFilesX86);
        var pd = SafeFolder(Environment.SpecialFolder.CommonApplicationData);
        var user = SafeFolder(Environment.SpecialFolder.UserProfile);

        // 相等即拒绝（系统/用户根目录）
        foreach (var p in new[]
                 {
                     sys, pf, pf86, pd, user,
                     Path.Combine(user, "AppData"),
                     Path.Combine(user, "AppData", "Local"),
                     Path.Combine(user, "AppData", "Roaming"),
                     Path.Combine(user, "AppData", "LocalLow"),
                 })
        {
            if (!string.IsNullOrEmpty(p)) f._exactDeny.Add(p);
        }

        // 位于其下即拒绝（个人数据与高敏感区）
        foreach (var p in new[]
                 {
                     Path.Combine(user, "Desktop"),
                     Path.Combine(user, "Documents"),
                     Path.Combine(user, "Pictures"),
                     Path.Combine(user, "Videos"),
                     Path.Combine(user, "Music"),
                     Path.Combine(user, "Saved Games"),
                     Path.Combine(user, "Contacts"),
                     Path.Combine(user, "Favorites"),
                     Path.Combine(user, "Links"),
                     Path.Combine(user, "Searches"),
                     Path.Combine(user, "3D Objects"),
                     Path.Combine(user, "OneDrive"),
                     Path.Combine(user, ".ssh"),
                     Path.Combine(user, ".gnupg"),
                     Path.Combine(user, "AppData", "Roaming", "Microsoft", "Crypto"),
                     Path.Combine(user, "AppData", "Roaming", "Microsoft", "Protect"),
                     Path.Combine(user, "AppData", "Local", "Microsoft", "Credentials"),
                     Path.Combine(pd, "Microsoft", "Crypto"),
                     Path.Combine(pd, "Microsoft", "Protect"),
                     AppPaths.DataRoot, // 自身数据目录自保护
                 })
        {
            if (!string.IsNullOrEmpty(p)) f._treeDeny.Add(p);
        }

        // 条件树：默认整树保护，仅白名单子路径可清理
        if (!string.IsNullOrEmpty(sys))
        {
            f._conditionalTrees[sys] = new List<string>
            {
                "Temp",
                "Logs",
                @"SoftwareDistribution\Download",
                @"SoftwareDistribution\DeliveryOptimization",
                "Minidump",
                "Downloaded Program Files",
            };
            f._allowedExactFiles.Add(Path.Combine(sys, "MEMORY.DMP"));
        }

        if (!string.IsNullOrEmpty(pd))
        {
            f._conditionalTrees[pd] = new List<string>
            {
                @"Microsoft\Windows\WER",
                @"Microsoft\Windows\DeliveryOptimization",
            };
        }

        return f;
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
        if (PathUtil.IsDriveRoot(p)) return SafetyDecision.Deny("盘符根目录");

        var root = Path.GetPathRoot(p) ?? "";
        if (root.Length == 0) return SafetyDecision.Deny("无根路径");

        // 盘根敏感文件（pagefile.sys 等）
        var parent = Path.GetDirectoryName(p);
        if (parent != null && PathUtil.IsDriveRoot(parent))
        {
            var name = Path.GetFileName(p);
            foreach (var rf in RootFileDeny)
                if (string.Equals(name, rf, StringComparison.OrdinalIgnoreCase))
                    return SafetyDecision.Deny("系统关键文件");
        }

        // 盘根敏感一级目录（$Recycle.Bin 等）
        var rel = p.Substring(root.Length);
        var split = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries);
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

    /// <summary>含磁盘 IO 的检查：额外拒绝符号链接/联接点目标本身。</summary>
    public SafetyDecision CheckWithReparse(string fullPath)
    {
        var quick = Check(fullPath);
        if (!quick.Allowed) return quick;
        try
        {
            var attr = File.GetAttributes(fullPath);
            if ((attr & FileAttributes.ReparsePoint) != 0)
                return SafetyDecision.Deny("符号链接/联接点");
        }
        catch
        {
            // 无法读取属性时放行（删除阶段会以文件系统错误再次兜底）
        }

        return SafetyDecision.Ok;
    }
}
