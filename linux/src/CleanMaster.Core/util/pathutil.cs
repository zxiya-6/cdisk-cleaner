using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CleanMaster.Core.Util;

/// <summary>路径规范化与安全检查工具（Linux / POSIX）。</summary>
public static class PathUtil
{
    /// <summary>展开环境变量（支持 $VAR、${VAR}、%VAR% 与开头的 ~）并去掉首尾空白与引号。</summary>
    public static string ExpandEnv(string path)
    {
        var p = path.Trim().Trim('"');
        if (p == "~") return HomeDir;
        if (p.StartsWith("~/", StringComparison.Ordinal)) p = HomeDir + p.Substring(1);

        p = Regex.Replace(p, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}",
            m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);
        p = Regex.Replace(p, @"\$([A-Za-z_][A-Za-z0-9_]*)",
            m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);
        p = Regex.Replace(p, @"%([A-Za-z_][A-Za-z0-9_]*)%",
            m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);
        return p;
    }

    public static string HomeDir
    {
        get
        {
            var h = Environment.GetEnvironmentVariable("HOME");
            return string.IsNullOrEmpty(h) ? "/" : h.TrimEnd('/');
        }
    }

    /// <summary>规范化为完整路径（去尾部分隔符；根 "/" 保留）。不解析符号链接。</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = ExpandEnv(path);
        try { p = Path.GetFullPath(p); } catch { /* 保留原样 */ }
        if (p.Length > 1 && p.EndsWith('/')) p = p.TrimEnd('/');
        if (p.Length == 0) p = "/";
        return p;
    }

    /// <summary>长路径前缀（Linux 无此限制；保留为 API 兼容的空操作）。</summary>
    public static string AddLongPrefix(string path) => path;

    /// <summary>去掉长路径前缀（空操作）。</summary>
    public static string StripLongPrefix(string path) => path;

    /// <summary>是否接近路径长度限制（Linux PATH_MAX=4096，预留余量）。</summary>
    public static bool IsLong(string path) => path.Length >= 3900;

    /// <summary>candidate 是否位于 root 之内（含相等）；Linux 区分大小写。</summary>
    public static bool IsUnder(string candidate, string root)
    {
        var c = Normalize(candidate);
        var r = Normalize(root);
        if (c.Length == 0 || r.Length == 0) return false;
        if (string.Equals(c, r, StringComparison.Ordinal)) return true;
        var prefix = r.EndsWith('/') ? r : r + '/';
        return c.StartsWith(prefix, StringComparison.Ordinal);
    }

    public static bool EqualsPath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    /// <summary>是否同一文件系统（通过 stat 比较设备号；stat 失败时回退为根路径比较）。</summary>
    public static bool SameVolume(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        try
        {
            var da = StatDevice(na);
            var db = StatDevice(nb);
            if (da > 0 && db > 0) return da == db;
        }
        catch
        {
            // 回退
        }

        var ra = Path.GetPathRoot(na) ?? "/";
        var rb = Path.GetPathRoot(nb) ?? "/";
        return string.Equals(ra, rb, StringComparison.Ordinal);
    }

    /// <summary>是否文件系统根（"/"）。</summary>
    public static bool IsDriveRoot(string path) => string.Equals(Normalize(path), "/", StringComparison.Ordinal);

    /// <summary>日志中截断显示超长路径。</summary>
    public static string Truncate(string path, int max = 120)
    {
        if (path == null) return "";
        return path.Length <= max ? path : "…" + path.Substring(path.Length - (max - 1));
    }

    public static bool HasWildcard(string s) => s.IndexOfAny(WildChars) >= 0;

    private static readonly char[] WildChars = { '*', '?' };

    /// <summary>按简单通配符（* ?）匹配文件名（Linux 区分大小写）。</summary>
    public static bool MatchName(string name, string pattern) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern.AsSpan(), name.AsSpan(), ignoreCase: false);

    /// <summary>文件名是否匹配任一模式。</summary>
    public static bool MatchAny(IReadOnlyList<string> patterns, string name)
    {
        foreach (var p in patterns)
            if (MatchName(name, p)) return true;
        return false;
    }

    // ---- libc stat（仅读取 st_dev；用大缓冲避免 glibc 结构体布局差异）----

    [DllImport("libc", SetLastError = true, EntryPoint = "stat")]
    private static extern int Stat(string path, [Out] byte[] buf);

    private static ulong StatDevice(string path)
    {
        // glibc x86_64 的 struct stat 为 144 字节；缓冲取 256 留足余量（其他架构也不越界）
        var buf = new byte[256];
        if (Stat(path, buf) == 0) return BitConverter.ToUInt64(buf, 0);
        return 0;
    }
}
