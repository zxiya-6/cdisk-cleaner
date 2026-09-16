namespace CleanMaster.Core.Util;

/// <summary>路径规范化与安全检查工具。</summary>
public static class PathUtil
{
    /// <summary>展开 %ENV% 变量并去掉首尾空白与引号。</summary>
    public static string ExpandEnv(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    /// <summary>规范化为完整路径（去尾部分隔符；盘符根保留 "C:\" 形式）。不解析符号链接。</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = ExpandEnv(path);
        try { p = Path.GetFullPath(p); } catch { /* 保留原样 */ }
        if (p.Length > 3 && (p.EndsWith('\\') || p.EndsWith('/'))) p = p.TrimEnd('\\', '/');
        if (p.Length == 2 && p[1] == ':') p += "\\";
        return p;
    }

    /// <summary>为本地绝对路径添加 \\?\ 长路径前缀（幂等）。</summary>
    public static string AddLongPrefix(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':') return @"\\?\" + path;
        return path;
    }

    /// <summary>去掉 \\?\ / \\?\UNC\ 前缀。</summary>
    public static string StripLongPrefix(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path.Substring(8);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path.Substring(4);
        return path;
    }

    /// <summary>是否接近 MAX_PATH 限制（用于选择长路径安全通道）。</summary>
    public static bool IsLong(string path) => path.Length >= 256;

    /// <summary>candidate 是否位于 root 之内（含相等）；边界感知、大小写不敏感。</summary>
    public static bool IsUnder(string candidate, string root)
    {
        var c = Normalize(candidate);
        var r = Normalize(root);
        if (c.Length == 0 || r.Length == 0) return false;
        if (string.Equals(c, r, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = r.EndsWith('\\') ? r : r + '\\';
        return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool EqualsPath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static bool SameVolume(string a, string b)
    {
        var ra = Path.GetPathRoot(Normalize(a)) ?? "";
        var rb = Path.GetPathRoot(Normalize(b)) ?? "";
        return ra.Length > 0 && string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>是否盘符根（如 "C:\"）。</summary>
    public static bool IsDriveRoot(string path)
    {
        var n = Normalize(path);
        return n.Length <= 3 && n.Length >= 2 && n[1] == ':';
    }

    /// <summary>日志中截断显示超长路径。</summary>
    public static string Truncate(string path, int max = 120)
    {
        if (path == null) return "";
        return path.Length <= max ? path : "…" + path.Substring(path.Length - (max - 1));
    }

    public static bool HasWildcard(string s) => s.IndexOfAny(WildChars) >= 0;

    private static readonly char[] WildChars = { '*', '?' };

    /// <summary>按简单通配符（* ?）匹配文件名，大小写不敏感。</summary>
    public static bool MatchName(string name, string pattern) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern.AsSpan(), name.AsSpan(), ignoreCase: true);

    /// <summary>文件名是否匹配任一模式。</summary>
    public static bool MatchAny(IReadOnlyList<string> patterns, string name)
    {
        foreach (var p in patterns)
            if (MatchName(name, p)) return true;
        return false;
    }
}
