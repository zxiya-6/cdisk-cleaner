using System.Globalization;

namespace CleanMaster.Core.Util;

/// <summary>人类可读的体积/数量格式化。</summary>
public static class HumanSize
{
    public static string Format(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double b = bytes;
        if (b < 1024) return $"{bytes} B";
        if (b < 1024 * 1024) return (b / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        if (b < 1024L * 1024 * 1024) return (b / 1048576).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        if (b < 1024L * 1024 * 1024 * 1024) return (b / 1073741824).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        return (b / 1099511627776).ToString("0.00", CultureInfo.InvariantCulture) + " TB";
    }

    /// <summary>按 MB 显示（保留一位小数），兼容旧习惯。</summary>
    public static string Mbs(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    public static string FileCount(long n) => n.ToString("N0", CultureInfo.InvariantCulture) + " 个文件";

    public static string Seconds(double s) => s < 1 ? (s * 1000).ToString("0") + " ms" : s.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
