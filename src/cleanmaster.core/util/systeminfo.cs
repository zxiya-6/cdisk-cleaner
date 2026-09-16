using System.Security.Principal;

namespace CleanMaster.Core.Util;

/// <summary>系统与磁盘信息。</summary>
public static class SystemInfo
{
    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static string CurrentUser => Environment.UserName;
    public static string MachineName => Environment.MachineName;
    public static string OsDescription => Environment.OSVersion.VersionString;

    public static string ProductVersionString => BuildInfo.Version;

    /// <summary>C 盘可用字节数（异常时返回 0）。</summary>
    public static long GetFreeBytes(string driveLetter = "C")
    {
        try
        {
            var di = new DriveInfo(driveLetter);
            if (di.IsReady) return di.AvailableFreeSpace;
        }
        catch
        {
        }

        return 0;
    }

    public static long GetTotalBytes(string driveLetter = "C")
    {
        try
        {
            var di = new DriveInfo(driveLetter);
            if (di.IsReady) return di.TotalSize;
        }
        catch
        {
        }

        return 0;
    }
}
