using System.Diagnostics;
using CleanMaster.Core.Config;
using CleanMaster.Core.Models;
using CleanMaster.Core.Rules;
using CleanMaster.Core.Util;

namespace CleanMaster.Core.Engine;

/// <summary>
/// 清理执行器：按选中的类别执行删除/备份/回收站操作。
/// - 每个文件写入前都过安全过滤器与备份区自保护；
/// - 并行执行类别、实时进度、随时取消；
/// - 全程记录台账所需数据（数量、字节、跳过原因、错误样本）。
/// </summary>
public sealed class CleanExecutor
{
    private readonly RuleLibrary _lib;
    private readonly SafetyFilter _safety;
    private readonly AppConfig _cfg;
    private readonly string _backupRoot;

    public CleanExecutor(RuleLibrary lib, SafetyFilter safety, AppConfig cfg)
    {
        _lib = lib;
        _safety = safety;
        _cfg = cfg;
        _backupRoot = cfg.BackupRoot;
    }

    public CleanReport Execute(CleanRequest req, IProgress<CleanProgress>? progress, CancellationToken ct)
    {
        var report = new CleanReport
        {
            SessionId = MakeSessionId(),
            StartedAtUtc = DateTime.UtcNow,
            Mode = req.Mode,
            Admin = SystemInfo.IsAdmin(),
            DryRun = req.DryRun,
        };
        var sw = Stopwatch.StartNew();
        report.FreeBeforeBytes = SystemInfo.GetFreeBytes();

        var selected = new List<(CategoryRule rule, CleanSelectionItem sel)>();
        foreach (var sel in req.Items)
        {
            var rule = _lib.Categories.FirstOrDefault(c => string.Equals(c.Id, sel.CategoryId, StringComparison.OrdinalIgnoreCase));
            if (rule == null)
            {
                report.Errors.Add($"未知类别：{sel.CategoryId}");
                continue;
            }

            selected.Add((rule, sel));
        }

        var throttle = new ProgressThrottle<CleanProgress>(progress);
        long doneFiles = 0, doneBytes = 0, freed = 0, skippedTotal = 0;
        var doneCats = 0;
        var results = new CategoryCleanResult?[selected.Count];

        var zone = new BackupZone(_backupRoot);
        string? backupSessionId = null;
        var sessionLock = new object();
        string EnsureBackupSession()
        {
            lock (sessionLock)
            {
                backupSessionId ??= zone.CreateSession(req.Label);
                return backupSessionId;
            }
        }

        if (req.CreateRestorePoint && !req.DryRun)
        {
            throttle.Flush(() => MakeProgress("restorepoint", doneCats, selected.Count, "", "", doneFiles, doneBytes, freed, skippedTotal, sw));
            report.RestorePointResult = "Linux 版不支持系统还原点，已跳过";
            AppLog.Info("系统还原点：" + report.RestorePointResult);
        }

        var dop = Math.Clamp(Environment.ProcessorCount / 6, 2, 4);
        using var sem = new SemaphoreSlim(dop);
        var tasks = new List<Task>(selected.Count);

        for (var i = 0; i < selected.Count; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(() =>
            {
                sem.Wait(ct);
                try
                {
                    var (rule, sel) = selected[idx];
                    results[idx] = CleanCategory(rule, sel, req, zone, EnsureBackupSession, ct,
                        (path, dFiles, dBytes, dFreed, dSkipped) =>
                        {
                            Interlocked.Add(ref doneFiles, dFiles);
                            Interlocked.Add(ref doneBytes, dBytes);
                            Interlocked.Add(ref freed, dFreed);
                            Interlocked.Add(ref skippedTotal, dSkipped);
                            var cur = path;
                            throttle.Post(() => MakeProgress("clean", Volatile.Read(ref doneCats), selected.Count, rule.Name, cur,
                                Interlocked.Read(ref doneFiles), Interlocked.Read(ref doneBytes), Interlocked.Read(ref freed), Interlocked.Read(ref skippedTotal), sw));
                        });
                }
                catch (OperationCanceledException)
                {
                    results[idx] = new CategoryCleanResult
                    {
                        CategoryId = selected[idx].rule.Id,
                        Name = selected[idx].rule.Name,
                        Cancelled = true,
                    };
                }
                catch (Exception ex)
                {
                    AppLog.Exception($"清理类别 {selected[idx].rule.Id} 失败", ex);
                    results[idx] = new CategoryCleanResult
                    {
                        CategoryId = selected[idx].rule.Id,
                        Name = selected[idx].rule.Name,
                        Errors = { "清理失败：" + ex.Message },
                    };
                }
                finally
                {
                    sem.Release();
                    var d = Interlocked.Increment(ref doneCats);
                    var dIdx = idx;
                    throttle.Post(() => MakeProgress("clean", d, selected.Count, selected[dIdx].rule.Name, "",
                        Interlocked.Read(ref doneFiles), Interlocked.Read(ref doneBytes), Interlocked.Read(ref freed), Interlocked.Read(ref skippedTotal), sw));
                }
            }, ct));
        }

        try
        {
            Task.WhenAll(tasks).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
        }

        zone.CloseAll();

        foreach (var r in results)
        {
            if (r == null) continue;
            report.Categories.Add(r);
            if (r.Cancelled) report.Cancelled = true;
        }

        report.BackupSessionId = backupSessionId;
        report.DeletedBytes = report.Categories.Sum(c => c.DeletedBytes);
        report.BackedUpBytes = report.Categories.Sum(c => c.BackedUpBytes);
        report.TrashedBytes = report.Categories.Sum(c => c.TrashedBytes);
        report.FinishedAtUtc = DateTime.UtcNow;
        report.FreeAfterBytes = SystemInfo.GetFreeBytes();

        throttle.Flush(() => MakeProgress("done", doneCats, selected.Count, "", "",
            Interlocked.Read(ref doneFiles), Interlocked.Read(ref doneBytes), Interlocked.Read(ref freed), Interlocked.Read(ref skippedTotal), sw));

        AppLog.Info($"清理完成：会话 {report.SessionId}，模式 {report.Mode}，删除 {HumanSize.Format(report.DeletedBytes)}，备份 {HumanSize.Format(report.BackedUpBytes)}，回收站 {HumanSize.Format(report.TrashedBytes)}，跳过 {report.TotalSkippedFiles}，耗时 {report.ElapsedSeconds:0.0}s，取消={report.Cancelled}");
        return report;
    }

    private CategoryCleanResult CleanCategory(
        CategoryRule rule,
        CleanSelectionItem sel,
        CleanRequest req,
        BackupZone zone,
        Func<string> ensureSession,
        CancellationToken ct,
        Action<string, long, long, long, long> tick)
    {
        var result = new CategoryCleanResult { CategoryId = rule.Id, Name = rule.Name };
        var sw = Stopwatch.StartNew();

        var strategy = ResolveStrategy(req.Mode, rule.Strategy, rule.Kind);
        var excl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sel.ExcludePaths != null)
            foreach (var p in sel.ExcludePaths) excl.Add(PathUtil.Normalize(p));

        // 特殊类别：清空回收站
        if (rule.Kind == RuleKind.EmptyRecycleBin)
        {
            var (items, bytes) = Trashes.Query();
            if (req.DryRun)
            {
                result.TrashedFiles = items;
                result.TrashedBytes = bytes;
            }
            else if (Trashes.Empty(out var err))
            {
                result.TrashedFiles = items;
                result.TrashedBytes = bytes;
            }
            else
            {
                result.Errors.Add("清空回收站失败：" + err);
            }

            result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            return result;
        }

        var resolved = RulePathResolver.Resolve(rule);
        foreach (var w in resolved.Warnings)
            if (result.Errors.Count < 100) result.Errors.Add(w);

        var safeRoots = new List<string>();
        foreach (var root in resolved.Paths)
        {
            var d = _safety.Check(root);
            if (d.Allowed) safeRoots.Add(root);
            else if (result.Errors.Count < 100) result.Errors.Add($"根路径被安全保护拒绝：{PathUtil.Truncate(root)}（{d.Reason}）");
        }

        var minAge = sel.MinAgeDays >= 0 ? sel.MinAgeDays : rule.MinAgeDays;
        var cutoff = minAge > 0 ? DateTime.UtcNow.AddDays(-minAge) : DateTime.MinValue;

        void Skip(WalkFile f, string reason)
        {
            result.SkippedFiles++;
            result.SkippedBytes += f.Size;
            if (result.Skipped.Count < 100) result.Skipped.Add(new SkippedItem { Path = f.Path, Reason = reason, Size = f.Size });
            tick(f.Path, 0, f.Size, 0, 1);
        }

        void HandleFile(WalkFile file)
        {
            if (excl.Contains(file.Path)) return;

            if (PathUtil.IsUnder(file.Path, _backupRoot))
            {
                Skip(file, "位于备份区，自动跳过");
                return;
            }

            var decision = _safety.Check(file.Path);
            if (!decision.Allowed)
            {
                Skip(file, "受保护：" + decision.Reason);
                return;
            }

            if (req.DryRun)
            {
                switch (strategy)
                {
                    case CleanStrategy.Delete:
                        result.DeletedFiles++;
                        result.DeletedBytes += file.Size;
                        tick(file.Path, 1, file.Size, file.Size, 0);
                        break;
                    case CleanStrategy.Backup:
                        result.BackedUpFiles++;
                        result.BackedUpBytes += file.Size;
                        tick(file.Path, 1, file.Size, 0, 0);
                        break;
                    default:
                        result.TrashedFiles++;
                        result.TrashedBytes += file.Size;
                        tick(file.Path, 1, file.Size, 0, 0);
                        break;
                }

                return;
            }

            switch (strategy)
            {
                case CleanStrategy.Delete:
                    if (FileOps.TryDeleteFile(file.Path, out var delErr))
                    {
                        result.DeletedFiles++;
                        result.DeletedBytes += file.Size;
                        tick(file.Path, 1, file.Size, file.Size, 0);
                    }
                    else
                    {
                        Skip(file, "删除失败：" + delErr);
                    }

                    break;

                case CleanStrategy.Backup:
                    {
                        var sid = ensureSession();
                        if (zone.MoveOne(sid, rule.Id, file, out var berr))
                        {
                            result.BackedUpFiles++;
                            result.BackedUpBytes += file.Size;
                            tick(file.Path, 1, file.Size, 0, 0);
                        }
                        else
                        {
                            Skip(file, berr);
                        }

                        break;
                    }

                case CleanStrategy.Trash:
                    if (Trashes.TryRecycle(file.Path, out var terr))
                    {
                        result.TrashedFiles++;
                        result.TrashedBytes += file.Size;
                        tick(file.Path, 1, file.Size, 0, 0);
                    }
                    else if (terr.Contains("跨文件系统", StringComparison.Ordinal))
                    {
                        // 跨分区不支持回收站：转入备份区（仍可恢复）
                        var sid = ensureSession();
                        if (zone.MoveOne(sid, rule.Id, file, out var berr2))
                        {
                            result.BackedUpFiles++;
                            result.BackedUpBytes += file.Size;
                            tick(file.Path, 1, file.Size, 0, 0);
                        }
                        else
                        {
                            Skip(file, "跨分区转入备份区失败：" + berr2);
                        }
                    }
                    else
                    {
                        Skip(file, "移入回收站失败：" + terr);
                    }

                    break;
            }
        }

        try
        {
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
                        HandleFile(new WalkFile(f, fi.Length, fi.LastWriteTimeUtc));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (result.Errors.Count < 100) result.Errors.Add($"{PathUtil.Truncate(f)} :: {ex.Message}");
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
                        HandleFile(file);
                        return true;
                    }, stats);
                }
            }

            foreach (var e in stats.AccessErrors)
                if (result.Errors.Count < 100) result.Errors.Add("遍历告警：" + e);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }
        catch (Exception ex)
        {
            result.Errors.Add("执行异常：" + ex.Message);
            AppLog.Exception($"清理类别 {rule.Id} 异常", ex);
        }

        result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    private static CleanStrategy ResolveStrategy(CleanMode mode, CleanStrategy ruleStrategy, RuleKind kind)
    {
        if (kind == RuleKind.EmptyRecycleBin) return ruleStrategy;

        return mode switch
        {
            CleanMode.AllToBackup => CleanStrategy.Backup,
            CleanMode.AllPermanent => CleanStrategy.Delete,
            _ => ruleStrategy,
        };
    }

    private static string MakeSessionId() =>
        DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Random.Shared.Next(0x1000, 0x10000).ToString("x4");

    private static CleanProgress MakeProgress(string phase, int doneCats, int totalCats, string currentCat, string currentPath,
        long files, long bytes, long freed, long skipped, Stopwatch sw) => new()
    {
        Phase = phase,
        CategoriesDone = doneCats,
        CategoriesTotal = totalCats,
        CurrentCategory = currentCat,
        CurrentPath = currentPath,
        DoneFiles = files,
        DoneBytes = bytes,
        FreedBytes = freed,
        Skipped = skipped,
        Seconds = sw.Elapsed.TotalSeconds,
    };
}
