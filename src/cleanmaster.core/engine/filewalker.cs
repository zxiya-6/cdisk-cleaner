using System.IO.Enumeration;
using CleanMaster.Core.Util;

namespace CleanMaster.Core.Engine;

/// <summary>遍历中发现的文件。</summary>
public readonly record struct WalkFile(string Path, long Size, DateTime LastWriteUtc);

/// <summary>遍历统计与错误。</summary>
public sealed class WalkStats
{
    public long Dirs { get; set; }
    public long SkippedReparse { get; set; }
    public long SkippedAccessDirs { get; set; }
    public List<string> AccessErrors { get; } = new();

    public void AddError(string dir, Exception ex)
    {
        SkippedAccessDirs++;
        if (AccessErrors.Count < 100)
            AccessErrors.Add($"{PathUtil.Truncate(dir)} :: {ex.Message}");
    }
}

/// <summary>
/// 快速文件遍历器：基于 System.IO.Enumeration（FindFirstFile 族），
/// 不跟随符号链接/联接点（防死循环与重复计数），支持取消与逐目录错误捕获。
/// </summary>
public static class FileWalker
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    private readonly record struct EntryData(string Path, bool IsDir, long Size, DateTime LastWrite, FileAttributes Attributes);

    /// <summary>
    /// 深度优先遍历 root 下的全部文件。onFile 返回 false 时立即停止整个遍历（用于取消/提前退出）。
    /// </summary>
    public static void Walk(string root, CancellationToken ct, Func<WalkFile, bool> onFile, WalkStats stats)
    {
        if (!Directory.Exists(root)) return;

        var stack = new Stack<string>();
        stack.Push(root);
        var tick = 0;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            stats.Dirs++;

            try
            {
                var enumerable = new FileSystemEnumerable<EntryData>(
                    dir,
                    static (ref FileSystemEntry e) => new EntryData(
                        e.ToFullPath(),
                        e.IsDirectory,
                        e.IsDirectory ? 0 : e.Length,
                        e.LastWriteTimeUtc.UtcDateTime,
                        e.Attributes),
                    Options);

                foreach (var entry in enumerable)
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        stats.SkippedReparse++;
                        continue;
                    }

                    if (entry.IsDir)
                    {
                        stack.Push(entry.Path);
                    }
                    else
                    {
                        if (!onFile(new WalkFile(entry.Path, entry.Size, entry.LastWrite)))
                            return;
                    }

                    if ((++tick & 0x7FF) == 0) ct.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                stats.AddError(dir, ex);
            }
        }
    }
}
