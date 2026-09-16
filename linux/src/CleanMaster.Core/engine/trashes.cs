using System.Text;
using CleanMaster.Core.Config;
using CleanMaster.Core.Util;

namespace CleanMaster.Core.Engine;

/// <summary>
/// Linux 回收站（freedesktop.org Trash 规范）：
/// 移入 ~/.local/share/Trash/files/ + info/*.trashinfo；查询与清空。
/// 跨文件系统时不支持移入回收站（提示改用备份区），与 Windows 版"长路径回退备份区"保持一致精神。
/// </summary>
public static class Trashes
{
    /// <summary>回收站根目录（遵循 XDG_DATA_HOME）。</summary>
    public static string TrashRoot => Path.Combine(AppPaths.XdgDataHome, "Trash");

    public static string FilesDir => Path.Combine(TrashRoot, "files");
    public static string InfoDir => Path.Combine(TrashRoot, "info");

    /// <summary>查询回收站中的条目数与字节数（递归统计）。</summary>
    public static (long items, long bytes) Query()
    {
        long items = 0, bytes = 0;
        try
        {
            if (!Directory.Exists(FilesDir)) return (0, 0);
            foreach (var entry in Directory.EnumerateFileSystemEntries(FilesDir))
            {
                items++;
                if (Directory.Exists(entry))
                {
                    var stats = new WalkStats();
                    FileWalker.Walk(entry, CancellationToken.None, f =>
                    {
                        bytes += f.Size;
                        return true;
                    }, stats);
                }
                else
                {
                    try { bytes += new FileInfo(entry).Length; } catch { }
                }
            }
        }
        catch
        {
        }

        return (items, bytes);
    }

    /// <summary>清空回收站（不可恢复）。</summary>
    public static bool Empty(out string error)
    {
        error = "";
        try
        {
            if (Directory.Exists(FilesDir))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(FilesDir))
                {
                    if (Directory.Exists(entry)) FileOps.TryDeleteDirectoryRecursive(entry, out _);
                    else FileOps.TryDeleteFile(entry, out _);
                }
            }

            if (Directory.Exists(InfoDir))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(InfoDir))
                    FileOps.TryDeleteFile(entry, out _);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>把文件/目录移入回收站（可恢复）。跨文件系统时返回 false 并说明原因。</summary>
    public static bool TryRecycle(string path, out string error)
    {
        error = "";
        var full = PathUtil.Normalize(path);
        if (full.Length == 0) { error = "空路径"; return false; }
        if (!PathUtil.SameVolume(full, TrashRoot))
        {
            error = "跨文件系统（不同分区不支持移入回收站，建议改用备份区）";
            return false;
        }

        try
        {
            Directory.CreateDirectory(FilesDir);
            Directory.CreateDirectory(InfoDir);

            var name = Path.GetFileName(full.TrimEnd('/'));
            if (string.IsNullOrEmpty(name)) { error = "无效路径"; return false; }

            var (destName, destInfoName) = FindFreeNames(name);
            var dest = Path.Combine(FilesDir, destName);
            var infoFile = Path.Combine(InfoDir, destInfoName);

            if (!FileOps.MoveOrCopy(full, dest, out var moveErr))
            {
                error = "移入回收站失败：" + moveErr;
                return false;
            }

            var info = $"[Trash Info]\nPath={Encode(full)}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n";
            try { File.WriteAllText(infoFile, info, new UTF8Encoding(false)); }
            catch (Exception ex)
            {
                // 写入 .trashinfo 失败：回滚（把文件移回原处）
                FileOps.MoveOrCopy(dest, full, out _);
                error = "写入回收站信息失败：" + ex.Message;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static (string file, string info) FindFreeNames(string name)
    {
        for (var i = 0; ; i++)
        {
            var n = i == 0 ? name : $"{name}.{i}";
            if (!File.Exists(Path.Combine(FilesDir, n)) && !Directory.Exists(Path.Combine(FilesDir, n)))
                return (n, n + ".trashinfo");
        }
    }

    /// <summary>按 Trash 规范编码 Path 字段（保留 / 与安全字符，其余百分号转义）。</summary>
    private static string Encode(string path)
    {
        var sb = new StringBuilder(path.Length);
        foreach (var c in path)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or ':' or '@' or '=' or '+' or '~' or '&')
                sb.Append(c);
            else
                sb.Append('%').Append(((int)c).ToString("X2"));
        }

        return sb.ToString();
    }
}
