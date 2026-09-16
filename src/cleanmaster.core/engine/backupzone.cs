using System.Text;
using CleanMaster.Core.Models;
using CleanMaster.Core.Util;

namespace CleanMaster.Core.Engine;

/// <summary>
/// 备份区（隔离区）：把被清理的文件移动进来以便随时恢复；清空备份区即释放空间。
/// 布局：&lt;root&gt;\&lt;sessionId&gt;\data\C\...（保留原目录结构）+ manifest.jsonl（追加式清单）。
/// </summary>
public sealed class BackupZone
{
    private readonly string _root;
    private readonly object _manifestLock = new();
    private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.OrdinalIgnoreCase);

    public BackupZone(string root) => _root = root;

    public string Root => _root;

    /// <summary>清单行。</summary>
    public sealed class ManifestLine
    {
        public string Op { get; set; } = "add"; // add | restored | purged
        public string P { get; set; } = "";     // 原路径
        public string B { get; set; } = "";     // 备份相对路径（相对会话目录）
        public long S { get; set; }             // 大小
        public string C { get; set; } = "";     // 类别
        public long T { get; set; }             // Unix 毫秒
    }

    public sealed class MoveCounters
    {
        public long Ok;
        public long Fail;
        public long Bytes;
        public List<SkippedItem> Failed { get; } = new();
    }

    public string CreateSession(string? label)
    {
        Directory.CreateDirectory(_root);
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Random.Shared.Next(0x1000, 0x10000).ToString("x4");
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        var meta = new
        {
            id,
            createdAtUtc = DateTime.UtcNow.ToString("O"),
            label,
            product = BuildInfo.ProductName,
            version = BuildInfo.Version,
        };
        File.WriteAllText(Path.Combine(dir, "meta.json"), Json.ToPretty(meta), Encoding.UTF8);
        return id;
    }

    public static string SessionDir(string root, string sessionId) => Path.Combine(root, sessionId);

    public static string ManifestPath(string sessionDir) => Path.Combine(sessionDir, "manifest.jsonl");

    /// <summary>把文件移入备份区（同类批量调用）。</summary>
    public void MoveFiles(string sessionId, string categoryId, IReadOnlyList<WalkFile> files, MoveCounters counters, CancellationToken ct)
    {
        var sessionDir = SessionDir(_root, sessionId);
        var dataDir = Path.Combine(sessionDir, "data");
        var writer = GetWriter(sessionId);

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = BuildRelPath(f.Path);
            var dest = Path.Combine(dataDir, rel);
            if (FileOps.MoveOrCopy(f.Path, dest, out var err))
            {
                counters.Ok++;
                counters.Bytes += f.Size;
                Append(writer, new ManifestLine
                {
                    Op = "add",
                    P = f.Path,
                    B = Path.Combine("data", rel),
                    S = f.Size,
                    C = categoryId,
                    T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
            }
            else
            {
                counters.Fail++;
                if (counters.Failed.Count < 100)
                    counters.Failed.Add(new SkippedItem { Path = f.Path, Reason = "移入备份区失败：" + err, Size = f.Size });
            }
        }
    }

    /// <summary>单文件便捷入口。</summary>
    public bool MoveOne(string sessionId, string categoryId, WalkFile file, out string error)
    {
        error = "";
        var counters = new MoveCounters();
        MoveFiles(sessionId, categoryId, new[] { file }, counters, CancellationToken.None);
        if (counters.Ok > 0) return true;
        error = counters.Failed.Count > 0 ? counters.Failed[0].Reason : "移动失败";
        return false;
    }

    private StreamWriter GetWriter(string sessionId)
    {
        lock (_manifestLock)
        {
            if (_writers.TryGetValue(sessionId, out var w)) return w;
            var sessionDir = SessionDir(_root, sessionId);
            Directory.CreateDirectory(sessionDir);
            var sw = new StreamWriter(ManifestPath(sessionDir), append: true, Encoding.UTF8) { AutoFlush = false };
            _writers[sessionId] = sw;
            return sw;
        }
    }

    private void Append(StreamWriter writer, ManifestLine line)
    {
        lock (_manifestLock)
        {
            writer.WriteLine(Json.ToCompact(line));
            if ((++_writeCounter & 0x7F) == 0) writer.Flush();
        }
    }

    private long _writeCounter;

    /// <summary>结束时刷新关闭写入器。</summary>
    public void CloseSession(string sessionId)
    {
        lock (_manifestLock)
        {
            if (_writers.TryGetValue(sessionId, out var w))
            {
                try { w.Flush(); w.Dispose(); } catch { }
                _writers.Remove(sessionId);
            }
        }
    }

    public void CloseAll()
    {
        lock (_manifestLock)
        {
            foreach (var w in _writers.Values)
            {
                try { w.Flush(); w.Dispose(); } catch { }
            }
            _writers.Clear();
        }
    }

    private static string BuildRelPath(string fullPath)
    {
        var p = PathUtil.Normalize(fullPath);
        if (p.Length >= 2 && p[1] == ':')
        {
            var drive = char.ToUpperInvariant(p[0]);
            var rest = p.Length > 3 ? p.Substring(3) : "";
            return string.IsNullOrEmpty(rest) ? drive + "\\_rootfile" : Path.Combine(drive.ToString(), rest);
        }

        return Path.Combine("misc", Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(p));
    }

    /// <summary>读取并折叠清单：返回仍在备份区中的条目（key = 备份相对路径）。</summary>
    public static Dictionary<string, ManifestLine> FoldManifest(string manifestPath)
    {
        var dict = new Dictionary<string, ManifestLine>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(manifestPath)) return dict;

        foreach (var line in File.ReadLines(manifestPath, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = Json.FromJson<ManifestLine>(line);
                if (item == null || string.IsNullOrEmpty(item.B)) continue;
                if (item.Op == "add") dict[item.B] = item;
                else dict.Remove(item.B);
            }
            catch
            {
                // 忽略损坏行
            }
        }

        return dict;
    }

    /// <summary>列出所有备份会话（按时间倒序）。</summary>
    public List<BackupSessionInfo> ListSessions()
    {
        var list = new List<BackupSessionInfo>();
        if (!Directory.Exists(_root)) return list;

        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var id = Path.GetFileName(dir);
            var manifest = ManifestPath(dir);
            var info = new BackupSessionInfo
            {
                Id = id,
                Path = dir,
                CreatedAtUtc = Directory.GetCreationTimeUtc(dir),
            };

            var metaFile = Path.Combine(dir, "meta.json");
            if (File.Exists(metaFile))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metaFile, Encoding.UTF8));
                    if (doc.RootElement.TryGetProperty("createdAtUtc", out var ca) &&
                        DateTime.TryParse(ca.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                        info.CreatedAtUtc = dt.ToUniversalTime();
                }
                catch { }
            }

            if (File.Exists(manifest))
            {
                long totalFiles = 0, totalBytes = 0;
                foreach (var line in File.ReadLines(manifest, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var item = Json.FromJson<ManifestLine>(line);
                        if (item == null) continue;
                        if (item.Op == "add") { totalFiles++; totalBytes += item.S; }
                        else if (item.Op == "restored") info.RestoredFiles++;
                        else if (item.Op == "purged") info.PurgedFiles++;
                    }
                    catch { }
                }

                var remaining = FoldManifest(manifest);
                foreach (var kv in remaining.Values)
                {
                    info.FilesRemaining++;
                    info.BytesRemaining += kv.S;
                    if (!string.IsNullOrEmpty(kv.C) && !info.Categories.Contains(kv.C)) info.Categories.Add(kv.C);
                }

                info.TotalFiles = totalFiles;
                info.TotalBytes = totalBytes;
            }

            list.Add(info);
        }

        return list.OrderByDescending(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>恢复备份文件。</summary>
    public RestoreReport Restore(string sessionId, RestoreOptions options, CancellationToken ct, IProgress<(long done, long total, string current)>? progress = null)
    {
        var report = new RestoreReport { SessionId = sessionId };
        var sessionDir = SessionDir(_root, sessionId);
        var manifest = ManifestPath(sessionDir);
        if (!File.Exists(manifest))
        {
            report.Errors.Add("未找到备份清单（manifest.jsonl）");
            return report;
        }

        var remaining = FoldManifest(manifest);
        var total = remaining.Count;
        long done = 0;
        var writer = GetWriter(sessionId);

        foreach (var kv in remaining.Values)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            if (options.OnlyOriginalPaths != null &&
                !options.OnlyOriginalPaths.Contains(kv.P, StringComparer.OrdinalIgnoreCase))
                continue;

            var src = Path.Combine(sessionDir, kv.B);
            if (!File.Exists(src))
            {
                report.SkippedFiles++;
                if (report.Skipped.Count < 100) report.Skipped.Add(kv.P + "（备份文件缺失）");
                continue;
            }

            var target = kv.P;
            try
            {
                if (File.Exists(target))
                {
                    switch (options.Conflict)
                    {
                        case ConflictPolicy.Skip:
                            report.SkippedFiles++;
                            if (report.Skipped.Count < 100) report.Skipped.Add(target + "（目标已存在）");
                            continue;
                        case ConflictPolicy.Overwrite:
                            FileOps.TryDeleteFile(target, out _);
                            break;
                        case ConflictPolicy.RenameAndRestore:
                            target = BuildRestoredName(target);
                            break;
                    }
                }

                if (FileOps.MoveOrCopy(src, target, out var err))
                {
                    report.RestoredFiles++;
                    report.RestoredBytes += kv.S;
                    Append(writer, new ManifestLine
                    {
                        Op = "restored",
                        P = kv.P,
                        B = kv.B,
                        S = kv.S,
                        C = kv.C,
                        T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
                }
                else
                {
                    report.FailedFiles++;
                    if (report.Errors.Count < 100) report.Errors.Add($"{PathUtil.Truncate(target)} :: {err}");
                }
            }
            catch (Exception ex)
            {
                report.FailedFiles++;
                if (report.Errors.Count < 100) report.Errors.Add($"{PathUtil.Truncate(target)} :: {ex.Message}");
            }

            if (done % 50 == 0) progress?.Report((done, total, target));
        }

        progress?.Report((done, total, ""));
        CloseSession(sessionId);

        // 清理空目录（尽力而为）
        try { CleanupEmptyDataDirs(Path.Combine(sessionDir, "data")); } catch { }

        AppLog.Info($"恢复备份 {sessionId}：成功 {report.RestoredFiles}，跳过 {report.SkippedFiles}，失败 {report.FailedFiles}");
        return report;
    }

    /// <summary>清空单个会话（永久删除，释放空间）。</summary>
    public PurgeReport Purge(string sessionId, CancellationToken ct)
    {
        var report = new PurgeReport { SessionId = sessionId };
        var sessionDir = SessionDir(_root, sessionId);
        if (!Directory.Exists(sessionDir))
        {
            report.Errors.Add("备份会话不存在");
            return report;
        }

        var remaining = FoldManifest(ManifestPath(sessionDir));
        long files = 0, bytes = 0;
        foreach (var kv in remaining.Values)
        {
            ct.ThrowIfCancellationRequested();
            files++;
            bytes += kv.S;
        }

        if (!FileOps.TryDeleteDirectoryRecursive(sessionDir, out var err))
        {
            report.Errors.Add(err);
            report.FailedFiles = files;
        }

        report.PurgedFiles = files;
        report.PurgedBytes = bytes;
        AppLog.Info($"清空备份区 {sessionId}：{files} 个文件，{HumanSize.Format(bytes)}");
        return report;
    }

    /// <summary>清空全部备份会话。</summary>
    public PurgeReport PurgeAll(CancellationToken ct)
    {
        var report = new PurgeReport { All = true };
        if (!Directory.Exists(_root)) return report;

        foreach (var dir in Directory.EnumerateDirectories(_root).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var one = Purge(Path.GetFileName(dir), ct);
            report.PurgedFiles += one.PurgedFiles;
            report.PurgedBytes += one.PurgedBytes;
            report.FailedFiles += one.FailedFiles;
            report.Errors.AddRange(one.Errors);
        }

        return report;
    }

    private static string BuildRestoredName(string target)
    {
        var dir = Path.GetDirectoryName(target) ?? "";
        var name = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        var restored = $"{name}_恢复_{DateTime.Now:yyyyMMddHHmmss}{ext}";
        return Path.Combine(dir, restored);
    }

    private static void CleanupEmptyDataDirs(string dataDir)
    {
        if (!Directory.Exists(dataDir)) return;
        foreach (var d in Directory.EnumerateDirectories(dataDir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(s => s.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(d).Any())
                    Directory.Delete(d, false);
            }
            catch { }
        }
    }
}
