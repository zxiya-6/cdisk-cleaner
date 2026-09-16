using System.Runtime.InteropServices;
using System.Text;

namespace CleanMaster.Core.Util;

/// <summary>系统与磁盘信息（Linux）。</summary>
public static class SystemInfo
{
    public static bool IsAdmin()
    {
        try { return GetEuid() == 0; }
        catch { return string.Equals(Environment.UserName, "root", StringComparison.Ordinal); }
    }

    public static string CurrentUser => Environment.UserName;
    public static string MachineName => Environment.MachineName;

    public static string OsDescription
    {
        get
        {
            try
            {
                if (File.Exists("/etc/os-release"))
                {
                    foreach (var line in File.ReadLines("/etc/os-release", Encoding.UTF8))
                    {
                        if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                            return line.Substring("PRETTY_NAME=".Length).Trim('"');
                    }
                }
            }
            catch
            {
            }

            return Environment.OSVersion.VersionString;
        }
    }

    public static string ProductVersionString => BuildInfo.Version;

    /// <summary>指定路径所在文件系统的可用字节数（默认 HOME 所在挂载点；异常时返回 0）。</summary>
    public static long GetFreeBytes(string path = "")
    {
        var p = string.IsNullOrEmpty(path) ? PathUtil.HomeDir : path;
        try
        {
            if (TryStatvfs(p, out var st) && st.BAvail > 0) return (long)(st.BAvail * st.Frsize);
        }
        catch
        {
        }

        return 0;
    }

    /// <summary>指定路径所在文件系统的总字节数（异常时返回 0）。</summary>
    public static long GetTotalBytes(string path = "")
    {
        var p = string.IsNullOrEmpty(path) ? PathUtil.HomeDir : path;
        try
        {
            if (TryStatvfs(p, out var st) && st.Blocks > 0) return (long)(st.Blocks * st.Frsize);
        }
        catch
        {
        }

        return 0;
    }

    // ---- libc ----

    [DllImport("libc", SetLastError = true, EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    /// <summary>解析后的 statvfs 关键字段（f_frsize 在偏移 8，f_blocks 在 16，f_bavail 在 32）。</summary>
    private readonly struct StatvfsValues
    {
        public readonly ulong Frsize;
        public readonly ulong Blocks;
        public readonly ulong BAvail;

        public StatvfsValues(ulong frsize, ulong blocks, ulong bavail)
        {
            Frsize = frsize;
            Blocks = blocks;
            BAvail = bavail;
        }
    }

    // glibc struct statvfs 为 112 字节（含 __f_spare[6]）；用 128 字节缓冲避免结构体布局差异
    [DllImport("libc", SetLastError = true, EntryPoint = "statvfs")]
    private static extern int StatvfsNative(string path, [Out] byte[] buf);

    private static bool TryStatvfs(string path, out StatvfsValues st)
    {
        var buf = new byte[128];
        if (StatvfsNative(path, buf) != 0)
        {
            st = default;
            return false;
        }

        st = new StatvfsValues(
            BitConverter.ToUInt64(buf, 8),
            BitConverter.ToUInt64(buf, 16),
            BitConverter.ToUInt64(buf, 32));
        return true;
    }
}
