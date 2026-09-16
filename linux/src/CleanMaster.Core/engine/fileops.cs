using CleanMaster.Core.Util;

namespace CleanMaster.Core.Engine;

/// <summary>文件级操作（删除/移动/复制校验），统一处理只读属性、长路径与错误转译。</summary>
public static class FileOps
{
    /// <summary>删除文件；已不存在视为成功。返回 false 时 error 给出可读原因。</summary>
    public static bool TryDeleteFile(string path, out string error)
    {
        error = "";
        var p = PathUtil.IsLong(path) ? PathUtil.AddLongPrefix(path) : path;
        try
        {
            var attr = File.GetAttributes(p);
            if ((attr & FileAttributes.ReadOnly) != 0)
            {
                try { File.SetAttributes(p, attr & ~FileAttributes.ReadOnly); } catch { }
            }
            File.Delete(p);
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception ex)
        {
            error = DescribeIoError(ex);
            return false;
        }
    }

    /// <summary>把源文件移动到目标位置；跨卷时自动复制+校验+删源。目标父目录自动创建。</summary>
    public static bool MoveOrCopy(string src, string dst, out string error)
    {
        error = "";
        try
        {
            EnsureDir(Path.GetDirectoryName(dst) ?? "");
            if (PathUtil.SameVolume(src, dst))
            {
                File.Move(PathUtil.IsLong(src) ? PathUtil.AddLongPrefix(src) : src,
                          PathUtil.IsLong(dst) ? PathUtil.AddLongPrefix(dst) : dst);
                return true;
            }

            // 跨卷：复制 -> 校验长度 -> 删源
            File.Copy(PathUtil.IsLong(src) ? PathUtil.AddLongPrefix(src) : src,
                      PathUtil.IsLong(dst) ? PathUtil.AddLongPrefix(dst) : dst);
            var srcLen = new FileInfo(PathUtil.IsLong(src) ? PathUtil.AddLongPrefix(src) : src).Length;
            var dstLen = new FileInfo(PathUtil.IsLong(dst) ? PathUtil.AddLongPrefix(dst) : dst).Length;
            if (srcLen != dstLen)
            {
                error = "复制校验失败（长度不一致）";
                try { File.Delete(PathUtil.IsLong(dst) ? PathUtil.AddLongPrefix(dst) : dst); } catch { }
                return false;
            }

            if (!TryDeleteFile(src, out var delErr))
            {
                error = "源文件删除失败：" + delErr;
                try { File.Delete(PathUtil.IsLong(dst) ? PathUtil.AddLongPrefix(dst) : dst); } catch { }
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = DescribeIoError(ex);
            return false;
        }
    }

    /// <summary>递归删除目录（先整删；失败则逐文件删+自底向上清目录）。</summary>
    public static bool TryDeleteDirectoryRecursive(string path, out string error)
    {
        error = "";
        var p = PathUtil.IsLong(path) ? PathUtil.AddLongPrefix(path) : path;
        try
        {
            if (!Directory.Exists(p)) return true;
            Directory.Delete(p, true);
            return true;
        }
        catch
        {
            // fallback：逐文件删除
        }

        try
        {
            var stats = new WalkStats();
            FileWalker.Walk(PathUtil.StripLongPrefix(p), CancellationToken.None, f =>
            {
                TryDeleteFile(f.Path, out _);
                return true;
            }, stats);

            foreach (var d in Directory.EnumerateDirectories(PathUtil.StripLongPrefix(p), "*", SearchOption.AllDirectories)
                         .OrderByDescending(s => s.Length))
            {
                try { Directory.Delete(d, false); } catch { }
            }

            try { Directory.Delete(PathUtil.StripLongPrefix(p), false); } catch { }
            return !Directory.Exists(PathUtil.StripLongPrefix(p));
        }
        catch (Exception ex)
        {
            error = DescribeIoError(ex);
            return false;
        }
    }

    public static void EnsureDir(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(PathUtil.IsLong(dir) ? PathUtil.AddLongPrefix(dir) : dir);
    }

    /// <summary>把 Windows IO 异常转译成中文可读原因。</summary>
    public static string DescribeIoError(Exception ex)
    {
        switch (ex)
        {
            case UnauthorizedAccessException:
                return "无权限（可能需要管理员）";
            case PathTooLongException:
                return "路径过长";
            case DirectoryNotFoundException:
                return "目录不存在";
            case FileNotFoundException:
                return "文件不存在";
            case IOException io:
                var hr = io.HResult & 0xFFFF;
                return hr switch
                {
                    32 => "文件被占用（另一个程序正在使用）",
                    33 => "文件被锁定（另一个程序正在使用）",
                    5 => "访问被拒绝",
                    19 => "介质写保护",
                    112 => "磁盘空间不足",
                    123 => "文件名或路径无效",
                    206 => "文件名或扩展名太长",
                    3 => "系统找不到指定路径",
                    2 => "系统找不到指定文件",
                    _ => "IO 错误：" + io.Message,
                };
            default:
                return ex.Message;
        }
    }
}
