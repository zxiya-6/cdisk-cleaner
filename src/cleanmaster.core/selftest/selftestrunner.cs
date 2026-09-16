using System.Diagnostics;
using System.Text;
using CleanMaster.Core.Config;
using CleanMaster.Core.Engine;
using CleanMaster.Core.Ledger;
using CleanMaster.Core.Models;
using CleanMaster.Core.Rules;
using CleanMaster.Core.Util;

namespace CleanMaster.Core.Selftest;

public sealed class TestResult
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string Detail { get; set; } = "";
    public double Ms { get; set; }
}

/// <summary>内置自检：扫描精度、安全策略、备份恢复、长路径、台账、取消能力。</summary>
public static class SelfTestRunner
{
    public static List<TestResult> Run(string? sampleRoot, Action<string>? log = null)
    {
        sampleRoot ??= Path.Combine(Path.GetTempPath(), "cc5-selftest");
        var results = new List<TestResult>();

        void One(string name, Func<string> body)
        {
            log?.Invoke($"== {name} ==");
            var sw = Stopwatch.StartNew();
            var r = new TestResult { Name = name };
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                r.Detail = body();
                r.Passed = true;
            }
            catch (Exception ex)
            {
                r.Passed = false;
                r.Detail = $"{ex.GetType().Name}: {ex.Message}";
            }

            sw.Stop();
            r.Ms = sw.Elapsed.TotalMilliseconds;
            results.Add(r);
            log?.Invoke($"   {(r.Passed ? "通过" : "失败")} · {r.Detail} · {r.Ms:0} ms");
        }

        try
        {
            if (Directory.Exists(sampleRoot))
            {
                try { FileOps.TryDeleteDirectoryRecursive(sampleRoot, out _); } catch { }
            }

            Directory.CreateDirectory(sampleRoot);

            One("安全过滤器判定", TestSafetyFilter);
            One("规则库加载与校验", TestRules);
            One("样本扫描精度（含排除/联接点）", () => TestScanAccuracy(sampleRoot));
            One("大文件扫描", () => TestBigFiles(sampleRoot));
            One("备份区移动与恢复往返", () => TestBackupRoundtrip(sampleRoot));
            One("删除行为（只读/占用/长路径）", () => TestDeletes(sampleRoot));
            One("台账写入与导出", () => TestLedger(sampleRoot));
            One("扫描取消响应", TestCancel);
        }
        finally
        {
            try { FileOps.TryDeleteDirectoryRecursive(sampleRoot, out _); } catch { }
        }

        return results;
    }

    // ---------------- 各项测试 ----------------

    private static string TestSafetyFilter()
    {
        var f = SafetyFilter.CreateDefault();
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var mustDeny = new[]
        {
            @"C:\",
            Path.Combine(sys, "System32", "kernel32.dll"),
            Path.Combine(sys, "WinSxS", "x"),
            Path.Combine(user, "Documents", "report.docx"),
            Path.Combine(user, "Desktop", "todo.txt"),
            @"C:\pagefile.sys",
            @"C:\$Recycle.Bin\S-1-5-21-1",
            Path.Combine(user, ".ssh", "id_rsa"),
        };

        var mustAllow = new[]
        {
            Path.Combine(Path.GetTempPath(), "abc.tmp"),
            Path.Combine(sys, "Temp", "x.log"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "User Data", "Default", "Cache", "data_1"),
            Path.Combine(sys, "SoftwareDistribution", "Download", "abc.cab"),
        };

        foreach (var p in mustDeny)
        {
            var d = f.Check(p);
            if (d.Allowed) throw new InvalidOperationException($"应当拒绝但被放行：{p}");
        }

        foreach (var p in mustAllow)
        {
            var d = f.Check(p);
            if (!d.Allowed) throw new InvalidOperationException($"应当放行但被拒绝：{p}（{d.Reason}）");
        }

        return $"拒绝 {mustDeny.Length} 项 / 放行 {mustAllow.Length} 项，全部符合预期";
    }

    private static string TestRules()
    {
        var lib = RuleLibraryLoader.Load();
        var problems = RuleLibraryLoader.Validate(lib);
        if (problems.Count > 0)
            throw new InvalidOperationException("规则库校验问题：" + string.Join("；", problems));

        var enabled = lib.Categories.Count(c => c.Enabled);
        return $"加载 {lib.Categories.Count} 个类别（启用 {enabled}），校验通过";
    }

    private static string TestScanAccuracy(string sampleRoot)
    {
        var dir = Path.Combine(sampleRoot, "scan");
        var sub1 = Path.Combine(dir, "sub1");
        var sub2 = Path.Combine(dir, "sub2");
        Directory.CreateDirectory(sub1);
        Directory.CreateDirectory(sub2);

        long expected = 0;
        for (var i = 0; i < 100; i++)
        {
            var p = Path.Combine(sub1, $"a{i:000}.tmp");
            File.WriteAllBytes(p, new byte[1024]);
            expected += 1024;
        }

        for (var i = 0; i < 50; i++)
        {
            var p = Path.Combine(sub2, $"b{i:000}.tmp");
            File.WriteAllBytes(p, new byte[2048]);
            expected += 2048;
        }

        File.WriteAllBytes(Path.Combine(sub1, "skip.me"), new byte[4096]);

        // 联接点（应被跳过，不得重复计数）
        var junction = Path.Combine(sub1, "loop");
        var junctionOk = MakeJunction(junction, dir);

        var rule = new CategoryRule
        {
            Id = "selftest-scan",
            Group = "selftest",
            Name = "扫描精度测试",
            Kind = RuleKind.DirContents,
            Paths = { dir },
            IncludeFiles = { "*" },
            ExcludeFiles = { "skip.me" },
        };

        var lib = new RuleLibrary { Categories = { rule } };
        var cfg = new AppConfig();
        ConfigStore.ApplyDefaults(cfg);
        var scanner = new Scanner(lib, SafetyFilter.CreateDefault(), cfg);
        var summary = scanner.ScanAsync(new[] { "selftest-scan" }, null, CancellationToken.None).GetAwaiter().GetResult();
        if (summary.Cancelled) throw new InvalidOperationException("扫描被意外取消");

        var cat = summary.Categories.Single();
        if (cat.TotalFiles != 150) throw new InvalidOperationException($"文件数不符：期望 150，实际 {cat.TotalFiles}");
        if (cat.TotalBytes != expected) throw new InvalidOperationException($"字节数不符：期望 {expected}，实际 {cat.TotalBytes}");
        if (junctionOk && cat.SkippedReparse < 1) throw new InvalidOperationException("联接点未被跳过");

        return $"150 个文件 / {HumanSize.Format(expected)} 精确匹配；(联接点创建={junctionOk}) 跳过符号链接 {cat.SkippedReparse} 个";
    }

    private static string TestBigFiles(string sampleRoot)
    {
        var dir = Path.Combine(sampleRoot, "big");
        Directory.CreateDirectory(dir);
        using (var fs = new FileStream(Path.Combine(dir, "small.bin"), FileMode.Create)) fs.SetLength(3 * 1024 * 1024L);
        using (var fs = new FileStream(Path.Combine(dir, "mid.bin"), FileMode.Create)) fs.SetLength(5 * 1024 * 1024L);
        using (var fs = new FileStream(Path.Combine(dir, "huge.bin"), FileMode.Create)) fs.SetLength(20 * 1024 * 1024L);

        var result = BigFileScanner.Scan(dir, 10 * 1024 * 1024L, 10, null, CancellationToken.None);
        if (result.Items.Count != 1 || !result.Items[0].Path.EndsWith("huge.bin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"大文件筛选不符：{result.Items.Count} 个命中");

        return $"扫描 {result.ScannedFiles} 个文件，命中 1 个 >10MB（{HumanSize.Format(result.Items[0].Size)}）";
    }

    private static string TestBackupRoundtrip(string sampleRoot)
    {
        var root = Path.Combine(sampleRoot, "bk");
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);

        string[] names = { "a.txt", "b.dat", "c.log" };
        var sizes = new[] { 1024, 2048, 4096 };
        for (var i = 0; i < names.Length; i++)
        {
            var data = new byte[sizes[i]];
            Random.Shared.NextBytes(data);
            File.WriteAllBytes(Path.Combine(src, names[i]), data);
        }

        var zone = new BackupZone(Path.Combine(root, "zone"));
        var sid = zone.CreateSession("selftest");

        var files = names.Select(n =>
        {
            var fi = new FileInfo(Path.Combine(src, n));
            return new WalkFile(fi.FullName, fi.Length, fi.LastWriteTimeUtc);
        }).ToList();

        var counters = new BackupZone.MoveCounters();
        zone.MoveFiles(sid, "selftest", files, counters, CancellationToken.None);
        zone.CloseAll();

        if (counters.Ok != 3 || counters.Fail != 0) throw new InvalidOperationException($"移动失败：ok={counters.Ok} fail={counters.Fail}");
        foreach (var n in names)
            if (File.Exists(Path.Combine(src, n)))
                throw new InvalidOperationException($"源文件未移走：{n}");

        var sessions = zone.ListSessions();
        var info = sessions.FirstOrDefault(s => s.Id == sid) ?? throw new InvalidOperationException("会话列表未找到");
        if (info.FilesRemaining != 3) throw new InvalidOperationException($"剩余文件数不符：{info.FilesRemaining}");

        var report = zone.Restore(sid, new RestoreOptions { Conflict = ConflictPolicy.RenameAndRestore }, CancellationToken.None);
        if (report.RestoredFiles != 3) throw new InvalidOperationException($"恢复失败：{report.RestoredFiles}");

        for (var i = 0; i < names.Length; i++)
        {
            var p = Path.Combine(src, names[i]);
            if (!File.Exists(p)) throw new InvalidOperationException($"恢复后文件缺失：{names[i]}");
            if (new FileInfo(p).Length != sizes[i]) throw new InvalidOperationException($"恢复后大小不符：{names[i]}");
        }

        var remainingAfterRestore = BackupZone.FoldManifest(BackupZone.ManifestPath(BackupZone.SessionDir(zone.Root, sid)));
        if (remainingAfterRestore.Count != 0)
            throw new InvalidOperationException($"恢复后备份区应清空，实际剩 {remainingAfterRestore.Count} 条");

        // 再次移入 2 个文件，然后清空备份区
        var again = names.Take(2).Select(n =>
        {
            var fi = new FileInfo(Path.Combine(src, n));
            return new WalkFile(fi.FullName, fi.Length, fi.LastWriteTimeUtc);
        }).ToList();
        var counters2 = new BackupZone.MoveCounters();
        zone.MoveFiles(sid, "selftest", again, counters2, CancellationToken.None);
        zone.CloseAll();

        var purge = zone.Purge(sid, CancellationToken.None);
        if (purge.PurgedFiles != 2) throw new InvalidOperationException($"清空数量不符：{purge.PurgedFiles}");
        if (Directory.Exists(BackupZone.SessionDir(zone.Root, sid))) throw new InvalidOperationException("清空后会话目录仍存在");

        return "移动 3 文件 → 恢复 3 文件（大小一致）→ 再次移入 2 → 清空备份区，全程一致";
    }

    private static string TestDeletes(string sampleRoot)
    {
        var dir = Path.Combine(sampleRoot, "del");
        Directory.CreateDirectory(dir);

        // 只读
        var ro = Path.Combine(dir, "ro.txt");
        File.WriteAllText(ro, "readonly");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        if (!FileOps.TryDeleteFile(ro, out var e1) || File.Exists(ro))
            throw new InvalidOperationException("只读文件删除失败：" + e1);

        // 被占用
        var busy = Path.Combine(dir, "busy.txt");
        File.WriteAllText(busy, "busy");
        var busyError = "";
        using (var hold = new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ok = FileOps.TryDeleteFile(busy, out busyError);
            if (ok || !busyError.Contains("占用"))
                throw new InvalidOperationException($"占用文件应删除失败且提示占用，实际 ok={ok} err={busyError}");
        }

        // 长路径（>300 字符）
        var deep = dir;
        var sb = new StringBuilder(deep);
        for (var i = 0; i < 12; i++)
        {
            sb.Append(Path.DirectorySeparatorChar).Append("segment_").Append(i.ToString("00")).Append("_abcdefghijklmnop");
            deep = sb.ToString();
        }

        Directory.CreateDirectory(PathUtil.AddLongPrefix(deep));
        var deepFile = Path.Combine(deep, "deep.txt");
        File.WriteAllText(PathUtil.AddLongPrefix(deepFile), "deep");
        if (deepFile.Length <= 260) throw new InvalidOperationException("长路径样本未达标：" + deepFile.Length);

        if (!FileOps.TryDeleteFile(deepFile, out var e3) || File.Exists(PathUtil.AddLongPrefix(deepFile)))
            throw new InvalidOperationException("长路径文件删除失败：" + e3);

        FileOps.TryDeleteDirectoryRecursive(dir, out _);

        return $"只读删除 OK；占用文件正确拒绝（原因：“{busyError}”）；长路径({deepFile.Length} 字符)删除 OK";
    }

    private static string TestLedger(string sampleRoot)
    {
        var file = Path.Combine(sampleRoot, "ledger.jsonl");
        var entry = LedgerFactory.FromTool("selftest", "自检写入", 1000, 2000);
        LedgerStore.Append(entry, file);
        var loaded = LedgerStore.LoadAll(10, file);
        if (loaded.Count != 1 || loaded[0].Id != entry.Id)
            throw new InvalidOperationException("台账读回失败");

        var csv = LedgerExporter.ToCsv(loaded);
        var html = LedgerExporter.ToHtml(loaded, "自检台账");
        if (!csv.Contains(entry.Id) || !html.Contains("自检台账"))
            throw new InvalidOperationException("导出内容缺失");

        return $"写入/读回 1 条；CSV {csv.Length} 字符；HTML {html.Length} 字符";
    }

    private static string TestCancel()
    {
        var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var task = Task.Run(() => BigFileScanner.Scan(@"C:\Windows\System32", 100L * 1024 * 1024, 10, null, cts.Token));
        Thread.Sleep(120);
        cts.Cancel();
        var done = task.Wait(TimeSpan.FromSeconds(6));
        sw.Stop();
        if (!done) throw new InvalidOperationException("取消后 6 秒未返回");
        if (!task.Result.Cancelled) throw new InvalidOperationException("扫描未标记为已取消");

        return $"全量扫描中途取消，{sw.ElapsedMilliseconds} ms 内响应停止";
    }

    // ---------------- 工具 ----------------

    private static bool MakeJunction(string linkPath, string target)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
