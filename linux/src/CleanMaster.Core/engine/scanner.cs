using System.Diagnostics;
using CleanMaster.Core.Config;
using CleanMaster.Core.Models;
using CleanMaster.Core.Rules;
using CleanMaster.Core.Util;

namespace CleanMaster.Core.Engine;

/// <summary>
/// 扫描器：并行扫描各清理类别，实时上报进度，随时可取消，不误报受保护路径。
/// </summary>
public sealed class Scanner
{
    private readonly RuleLibrary _lib;
    private readonly SafetyFilter _safety;
    private readonly AppConfig _cfg;

    public Scanner(RuleLibrary lib, SafetyFilter safety, AppConfig cfg)
    {
        _lib = lib;
        _safety = safety;
        _cfg = cfg;
    }

    private sealed class Counters
    {
        public long Files;
        public long Bytes;
        public long Errors;
    }

    public async Task<ScanSummary> ScanAsync(IReadOnlyList<string>? onlyIds, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var summary = new ScanSummary { StartedAtUtc = DateTime.UtcNow, Admin = SystemInfo.IsAdmin() };

        var rules = _lib.Categories
            .Where(c => c.Enabled && (onlyIds == null || onlyIds.Count == 0 || onlyIds.Contains(c.Id, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        var counters = new Counters();
        var throttle = new ProgressThrottle<ScanProgress>(progress);
        var done = 0;
        var results = new CategoryScanResult?[rules.Count];

        // 阶段一：解析路径（秒级完成）
        var resolved = new (CategoryRule rule, RulePathResolver.ResolveResult res)[rules.Count];
        for (var i = 0; i < rules.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            resolved[i] = (rules[i], RulePathResolver.Resolve(rules[i]));
        }

        throttle.Flush(() => MakeProgress("scan", 0, rules.Count, "准备扫描…", counters, sw));

        // 阶段二：并行扫描类别
        var dop = Math.Clamp(Environment.ProcessorCount / 4, 2, 6);
        using var sem = new SemaphoreSlim(dop);
        var tasks = new List<Task>(rules.Count);

        for (var i = 0; i < rules.Count; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(() =>
            {
                sem.Wait(ct);
                try
                {
                    var (rule, res) = resolved[idx];
                    results[idx] = ScanCategory(rule, res, summary.Admin, counters, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref counters.Errors);
                    AppLog.Exception($"扫描类别 {resolved[idx].rule.Id} 失败", ex);
                    results[idx] = new CategoryScanResult
                    {
                        CategoryId = resolved[idx].rule.Id,
                        Name = resolved[idx].rule.Name,
                        Group = resolved[idx].rule.Group,
                        AccessErrors = { "扫描失败：" + ex.Message },
                    };
                }
                finally
                {
                    sem.Release();
                    var d = Interlocked.Increment(ref done);
                    var idx2 = idx;
                    throttle.Post(() => MakeProgress("scan", d, rules.Count, resolved[idx2].rule.Name, counters, sw));
                }
            }, ct));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            summary.Cancelled = true;
        }

        foreach (var r in results)
        {
            if (r == null) continue;
            summary.Categories.Add(r);
            summary.TotalFiles += r.TotalFiles;
            summary.TotalBytes += r.TotalBytes;
        }

        summary.Categories.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        summary.FinishedAtUtc = DateTime.UtcNow;
        throttle.Flush(() => MakeProgress(summary.Cancelled ? "cancelled" : "done", done, rules.Count, "", counters, sw));

        AppLog.Info($"扫描完成：{summary.Categories.Count} 个类别，{summary.TotalFiles} 个文件，{HumanSize.Format(summary.TotalBytes)}，耗时 {sw.Elapsed.TotalSeconds:0.0}s，取消={summary.Cancelled}");
        return summary;
    }

    private CategoryScanResult ScanCategory(CategoryRule rule, RulePathResolver.ResolveResult res, bool admin, Counters counters, CancellationToken ct)
    {
        var r = new CategoryScanResult
        {
            CategoryId = rule.Id,
            Group = rule.Group,
            Name = rule.Name,
            Note = rule.Note,
            Risk = rule.Risk,
            Strategy = rule.Strategy,
            RequiresAdmin = rule.RequiresAdmin,
            Recommend = rule.Recommend,
            DefaultChecked = rule.DefaultChecked,
            ScannedAtUtc = DateTime.UtcNow,
        };

        r.Roots.AddRange(res.Paths);
        foreach (var w in res.Warnings)
            if (r.AccessErrors.Count < 100) r.AccessErrors.Add(w);

        // 安全过滤：检查解析出的根路径
        var safeRoots = new List<string>();
        foreach (var root in res.Paths)
        {
            var decision = _safety.Check(root);
            if (decision.Allowed) safeRoots.Add(root);
            else if (r.AccessErrors.Count < 100) r.AccessErrors.Add($"根路径被安全保护拒绝：{PathUtil.Truncate(root)}（{decision.Reason}）");
        }

        if (rule.Kind == RuleKind.EmptyRecycleBin)
        {
            var (items, bytes) = Trashes.Query();
            r.TotalFiles = items;
            r.TotalBytes = bytes;
            r.Roots.Add(Trashes.TrashRoot + "（freedesktop Trash 查询）");
            Interlocked.Add(ref counters.Files, items);
            Interlocked.Add(ref counters.Bytes, bytes);
            return Finalize(r);
        }

        var topLimit = Math.Max(100, _cfg.TopItemsPerCategory);
        var cutoff = rule.MinAgeDays > 0 ? DateTime.UtcNow.AddDays(-rule.MinAgeDays) : DateTime.MinValue;
        var stats = new WalkStats();

        if (rule.Kind == RuleKind.Files)
        {
            foreach (var f in safeRoots)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(f);
                    if (!fi.Exists) continue;
                    if (cutoff != DateTime.MinValue && fi.LastWriteTimeUtc > cutoff) continue;
                    r.TotalFiles++;
                    r.TotalBytes += fi.Length;
                    AddTop(r.TopItems, new FileItem(f, fi.Length, fi.LastWriteTimeUtc), topLimit);
                    Interlocked.Increment(ref counters.Files);
                    Interlocked.Add(ref counters.Bytes, fi.Length);
                }
                catch (Exception ex)
                {
                    if (r.AccessErrors.Count < 100) r.AccessErrors.Add($"{PathUtil.Truncate(f)} :: {ex.Message}");
                }
            }
        }
        else
        {
            foreach (var root in safeRoots)
            {
                ct.ThrowIfCancellationRequested();
                FileWalker.Walk(root, ct, file =>
                {
                    var name = Path.GetFileName(file.Path);
                    if (!PathUtil.MatchAny(rule.IncludeFiles, name)) return true;
                    if (rule.ExcludeFiles.Count > 0 && PathUtil.MatchAny(rule.ExcludeFiles, name)) return true;
                    if (cutoff != DateTime.MinValue && file.LastWriteUtc > cutoff) return true;

                    r.TotalFiles++;
                    r.TotalBytes += file.Size;
                    AddTop(r.TopItems, new FileItem(file.Path, file.Size, file.LastWriteUtc), topLimit);
                    Interlocked.Increment(ref counters.Files);
                    Interlocked.Add(ref counters.Bytes, file.Size);
                    return true;
                }, stats);
            }

            r.SkippedAccessDirs = stats.SkippedAccessDirs;
            r.SkippedReparse = stats.SkippedReparse;
            foreach (var e in stats.AccessErrors)
                if (r.AccessErrors.Count < 100) r.AccessErrors.Add(e);
        }

        // 进程占用提示
        foreach (var procName in rule.WarnProcesses)
        {
            try
            {
                var ps = Process.GetProcessesByName(procName);
                if (ps.Length > 0)
                {
                    r.WarnNotes.Add($"{procName} 正在运行，相关文件可能被占用（建议先关闭再清理）");
                    foreach (var p in ps) p.Dispose();
                }
            }
            catch
            {
            }
        }

        if (rule.RequiresAdmin && !admin) r.NeedsAdminHint = true;
        if (rule.RequiresAdmin && stats.SkippedAccessDirs > 0 && admin) r.NeedsAdminHint = false;

        return Finalize(r);
    }

    private static CategoryScanResult Finalize(CategoryScanResult r)
    {
        r.TopItems.Sort((a, b) => b.Size.CompareTo(a.Size));
        return r;
    }

    private static void AddTop(List<FileItem> list, FileItem item, int limit)
    {
        list.Add(item);
        if (list.Count >= limit * 2)
        {
            list.Sort((a, b) => b.Size.CompareTo(a.Size));
            list.RemoveRange(limit, list.Count - limit);
        }
    }

    private static ScanProgress MakeProgress(string phase, int done, int total, string current, Counters c, Stopwatch sw) => new()
    {
        Phase = phase,
        CategoriesDone = done,
        CategoriesTotal = total,
        CurrentCategory = current,
        Files = Interlocked.Read(ref c.Files),
        Bytes = Interlocked.Read(ref c.Bytes),
        Errors = Interlocked.Read(ref c.Errors),
        Seconds = sw.Elapsed.TotalSeconds,
    };
}
