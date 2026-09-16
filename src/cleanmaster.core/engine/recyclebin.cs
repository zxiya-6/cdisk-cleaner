using System.Runtime.InteropServices;
using CleanMaster.Core.Util;

namespace CleanMaster.Core.Engine;

/// <summary>Windows 回收站互操作（Shell API）。所有调用在专用 STA 线程执行。</summary>
public static class RecycleBin
{
    // ---- SHFileOperation ----
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    // ---- SHQueryRecycleBin / SHEmptyRecycleBin ----
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    /// <summary>把单个文件/目录移到回收站（可恢复）。</summary>
    public static bool TryRecycle(string path, out string error)
    {
        error = "";
        var full = PathUtil.Normalize(path);
        if (full.Length == 0) { error = "空路径"; return false; }
        if (PathUtil.IsLong(full)) { error = "路径过长（回收站不支持，建议改用备份区）"; return false; }

        var ok = false;
        var err = "";
        RunSta(() =>
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            var ptr = Marshal.StringToHGlobalUni(full + "\0"); // StringToHGlobalUni 自带结尾 NUL，拼一个形成双 NUL
            try
            {
                op.pFrom = ptr;
                var r = SHFileOperationW(ref op);
                if (r == 0 && op.fAnyOperationsAborted == 0) ok = true;
                else if (r == 0) { err = "操作被中止（文件可能被占用）"; }
                else err = $"Shell 错误码 0x{r:X}";
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }, ex => err = ex.Message);

        error = err;
        return ok;
    }

    /// <summary>查询指定盘回收站的文件数与字节数。</summary>
    public static (long items, long bytes) Query(string driveRoot = "C:\\")
    {
        long items = 0, bytes = 0;
        RunSta(() =>
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            var r = SHQueryRecycleBinW(driveRoot, ref info);
            if (r == 0)
            {
                items = info.i64NumItems;
                bytes = info.i64Size;
            }
        }, _ => { });
        return (items, bytes);
    }

    /// <summary>清空指定盘回收站（不可恢复）。</summary>
    public static bool Empty(string driveRoot, out string error)
    {
        error = "";
        var ok = false;
        var err = "";
        RunSta(() =>
        {
            var r = SHEmptyRecycleBinW(IntPtr.Zero, driveRoot, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            if (r == 0) ok = true;
            else err = $"Shell 错误码 0x{r:X}";
        }, ex => err = ex.Message);

        error = err;
        return ok;
    }

    private static void RunSta(Action action, Action<Exception> onError)
    {
        Exception? caught = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { caught = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join();
        if (caught != null) onError(caught);
    }
}
