using System.Diagnostics;
using CleanMaster.Core.Models;
using CleanMaster.Core.Util;

namespace CleanMaster.Core.Engine;

public sealed class BigFileProgress
{
    public long Files { get; set; }
    public long Bytes { get; set; }
    public double Seconds { get; set; }
}

public sealed class BigFileResult
{
    public List<FileItem> Items { get; } = new();
    public long ScannedFiles { get; set; }
    public long ScannedBytes { get; set; }
    public long SkippedAccessDirs { get; set; }
    public bool Cancelled { get; set; }
    public double ElapsedSeconds { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>大文件扫描：遍历指定根目录，找出大于阈值的文件（Top N，按大小排序）。</summary>
public static class BigFileScanner
{
    public static BigFileResult Scan(string root, long minBytes, int topN, IProgress<BigFileProgress>? progress, CancellationToken ct)
    {
        var result = new BigFileResult();
        var sw = Stopwatch.StartNew();
        var stats = new WalkStats();
        var lastReport = 0L;

        try
        {
            FileWalker.Walk(root, ct, file =>
            {
                result.ScannedFiles++;
                result.ScannedBytes += file.Size;
                if (file.Size >= minBytes)
                    AddTop(result.Items, new FileItem(file.Path, file.Size, file.LastWriteUtc), topN);

                var now = sw.ElapsedMilliseconds;
                if (now - lastReport > 300)
                {
                    lastReport = now;
                    progress?.Report(new BigFileProgress { Files = result.ScannedFiles, Bytes = result.ScannedBytes, Seconds = sw.Elapsed.TotalSeconds });
                }

                return true;
            }, stats);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }

        result.SkippedAccessDirs = stats.SkippedAccessDirs;
        foreach (var e in stats.AccessErrors)
            if (result.Errors.Count < 100) result.Errors.Add(e);

        result.Items.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (result.Items.Count > topN) result.Items.RemoveRange(topN, result.Items.Count - topN);
        result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    private static void AddTop(List<FileItem> list, FileItem item, int limit)
    {
        list.Add(item);
        if (limit > 0 && list.Count >= limit * 2)
        {
            list.Sort((a, b) => b.Size.CompareTo(a.Size));
            list.RemoveRange(limit, list.Count - limit);
        }
    }
}
