using System.Diagnostics;
using System.Text;
using CleanMaster.Core;
using CleanMaster.Core.Config;
using CleanMaster.Core.Engine;
using CleanMaster.Core.Ledger;
using CleanMaster.Core.Models;
using CleanMaster.Core.Rules;
using CleanMaster.Core.Selftest;
using CleanMaster.Core.Util;

namespace CleanMaster.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("错误：" + ex.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var (cmd, o) = Parse(args);

        if (V(o, "data-dir") is { } dd)
            Environment.SetEnvironmentVariable("CC5_DATA_DIR", Path.GetFullPath(dd));

        switch (cmd)
        {
            case "":
            case "help":
            case "?":
                PrintHelp();
                return 0;
            case "version":
                Console.WriteLine($"{BuildInfo.ProductName} 命令行版 v{BuildInfo.Version}（{BuildInfo.Stage}）");
                Console.WriteLine($"数据目录：{AppPaths.DataRoot}");
                Console.WriteLine($"管理员：{(SystemInfo.IsAdmin() ? "是" : "否")}");
                return 0;
            case "rules-check":
                return CmdRulesCheck(o);
            case "rules-update":
                return CmdRulesUpdate(o);
            case "scan":
                return CmdScan(o);
            case "clean":
                return CmdClean(o);
            case "bigfiles":
                return CmdBigFiles(o);
            case "backup-list":
                return CmdBackupList(o);
            case "backup-restore":
                return CmdBackupRestore(o);
            case "backup-purge":
                return CmdBackupPurge(o);
            case "ledger":
                return CmdLedger(o);
            case "ledger-export":
                return CmdLedgerExport(o);
            case "selftest":
                return CmdSelfTest(o);
            case "journal-vacuum":
                return CmdJournalVacuum(o);
            default:
                Console.WriteLine($"未知命令：{cmd}\n");
                PrintHelp();
                return 1;
        }
    }

    // ================= 命令实现 =================

    private static int CmdRulesCheck(Dictionary<string, List<string>> o)
    {
        var lib = RuleLibraryLoader.Load(V(o, "rules"));
        var problems = RuleLibraryLoader.Validate(lib);
        Console.WriteLine($"规则库：{V(o, "rules") ?? AppPaths.RulesFile}");
        Console.WriteLine($"类别总数：{lib.Categories.Count}（启用 {lib.Categories.Count(c => c.Enabled)}）");
        Console.WriteLine($"安全级 {lib.Categories.Count(c => c.Risk == RiskLevel.Safe)} · 谨慎级 {lib.Categories.Count(c => c.Risk == RiskLevel.Moderate)} · 高风险 {lib.Categories.Count(c => c.Risk == RiskLevel.High)}");
        if (problems.Count == 0)
        {
            Console.WriteLine("校验结果：通过");
            return 0;
        }

        Console.WriteLine("校验问题：");
        foreach (var p in problems) Console.WriteLine("  - " + p);
        return 1;
    }

    private static int CmdScan(Dictionary<string, List<string>> o)
    {
        var ctx = LoadContext(o);
        var only = SplitCsv(Vs(o, "category"));
        var scanner = new Scanner(ctx.lib, ctx.safety, ctx.cfg);

        Console.WriteLine($"{BuildInfo.ProductName} v{BuildInfo.Version} · 扫描开始（管理员={SystemInfo.IsAdmin()}）");
        var sw = Stopwatch.StartNew();
        var summary = scanner.ScanAsync(only.Count > 0 ? only : null, new ScanConsoleProgress(), CancellationToken.None)
            .GetAwaiter().GetResult();
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"{"类别",-22}{"文件数",12}{"大小",14}{"风险",8}{"策略",8}");
        Console.WriteLine(new string('-', 72));
        foreach (var c in summary.Categories)
        {
            Console.WriteLine($"{c.Name,-22}{c.TotalFiles,12:N0}{HumanSize.Format(c.TotalBytes),14}{RiskText(c.Risk),8}{c.Strategy,8}");
        }

        Console.WriteLine(new string('-', 72));
        Console.WriteLine($"合计：{summary.Categories.Count} 个类别 · {summary.TotalFiles:N0} 个文件 · {HumanSize.Format(summary.TotalBytes)}");
        Console.WriteLine($"耗时：{summary.ElapsedSeconds:0.00}s（墙钟 {sw.Elapsed.TotalSeconds:0.00}s）· 取消={summary.Cancelled}");

        var accessIssues = summary.Categories.Sum(c => c.SkippedAccessDirs);
        var reparse = summary.Categories.Sum(c => c.SkippedReparse);
        Console.WriteLine($"跳过：无权限目录 {accessIssues} 个 · 符号链接/联接点 {reparse} 个");

        if (V(o, "json") is { } jf)
        {
            File.WriteAllText(Path.GetFullPath(jf), Json.ToPretty(summary), new UTF8Encoding(true));
            Console.WriteLine($"扫描结果 JSON：{Path.GetFullPath(jf)}");
        }

        return summary.Cancelled ? 2 : 0;
    }

    private static int CmdClean(Dictionary<string, List<string>> o)
    {
        var ctx = LoadContext(o);
        var ids = new List<string>(SplitCsv(Vs(o, "category")));

        if (F(o, "recommend"))
            ids.AddRange(ctx.lib.Categories.Where(c => c.Enabled && c.Recommend).Select(c => c.Id));
        if (F(o, "all-safe"))
            ids.AddRange(ctx.lib.Categories.Where(c => c.Enabled && c.Risk == RiskLevel.Safe).Select(c => c.Id));

        ids = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
        {
            Console.WriteLine("请通过 --category <id,...> / --recommend / --all-safe 指定要清理的类别。");
            return 1;
        }

        var unknown = ids.Where(id => ctx.lib.Categories.All(c => !string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
        {
            Console.WriteLine("未知类别：" + string.Join(", ", unknown));
            return 1;
        }

        var mode = (V(o, "mode") ?? "standard").ToLowerInvariant() switch
        {
            "backup" or "alltobackup" => CleanMode.AllToBackup,
            "permanent" or "allpermanent" => CleanMode.AllPermanent,
            _ => CleanMode.Standard,
        };

        var dry = F(o, "dry-run");
        if (!dry && !F(o, "yes"))
        {
            Console.WriteLine("实际清理需要 --yes 确认。可先加 --dry-run 预演；或使用图形界面操作。");
            return 1;
        }

        var req = new CleanRequest
        {
            Mode = mode,
            DryRun = dry,
            CreateRestorePoint = F(o, "restore-point"),
            Label = V(o, "label"),
        };

        var exclusions = Vs(o, "exclude").Select(PathUtil.Normalize).Where(p => p.Length > 0).ToList();
        var minAgeMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Vs(o, "min-age"))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && int.TryParse(kv[1], out var days)) minAgeMap[kv[0]] = days;
        }

        foreach (var id in ids)
        {
            var item = new CleanSelectionItem { CategoryId = id };
            if (minAgeMap.TryGetValue(id, out var d)) item.MinAgeDays = d;
            item.ExcludePaths.AddRange(exclusions);
            req.Items.Add(item);
        }

        var executor = new CleanExecutor(ctx.lib, ctx.safety, ctx.cfg);
        using var cts = new CancellationTokenSource();
        if (V(o, "max-seconds") is { } ms && int.TryParse(ms, out var maxSec) && maxSec > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(maxSec));

        Console.WriteLine($"{BuildInfo.ProductName} v{BuildInfo.Version} · {(dry ? "预演（不会真正删除）" : "清理开始")} · 模式={ModeText(mode)} · 类别 {ids.Count} 个");
        if (req.CreateRestorePoint) Console.WriteLine("（Linux 版不支持系统还原点，将跳过）");

        var before = SystemInfo.GetFreeBytes();
        var report = executor.Execute(req, new CleanConsoleProgress(), cts.Token);

        Console.WriteLine();
        Console.WriteLine($"{"类别",-22}{"删除",10}{"备份",10}{"回收站",10}{"跳过",8}");
        Console.WriteLine(new string('-', 76));
        foreach (var c in report.Categories)
        {
            Console.WriteLine($"{c.Name,-22}{c.DeletedFiles,10:N0}{c.BackedUpFiles,10:N0}{c.TrashedFiles,10:N0}{c.SkippedFiles,8:N0}");
        }

        Console.WriteLine(new string('-', 76));
        Console.WriteLine($"{(dry ? "预计释放" : "永久删除")}：{HumanSize.Format(report.DeletedBytes)}");
        Console.WriteLine($"移入备份区：{HumanSize.Format(report.BackedUpBytes)}（清空备份区后释放；可随时恢复）");
        Console.WriteLine($"移至回收站：{HumanSize.Format(report.TrashedBytes)}");
        Console.WriteLine($"跳过：{report.TotalSkippedFiles:N0} 个文件");
        Console.WriteLine($"HOME 所在分区可用：{HumanSize.Format(report.FreeBeforeBytes)} → {HumanSize.Format(report.FreeAfterBytes)}（Δ {HumanSize.Format(report.FreeAfterBytes - report.FreeBeforeBytes)}）");
        Console.WriteLine($"耗时：{report.ElapsedSeconds:0.0}s · 取消={report.Cancelled}");
        if (report.RestorePointResult != null) Console.WriteLine($"还原点：{report.RestorePointResult}");

        var sampleSkips = report.Categories.SelectMany(c => c.Skipped).Take(5).ToList();
        if (sampleSkips.Count > 0)
        {
            Console.WriteLine("跳过示例：");
            foreach (var s in sampleSkips) Console.WriteLine($"  - {PathUtil.Truncate(s.Path, 90)} :: {s.Reason}");
        }

        if (!dry)
        {
            var entry = LedgerFactory.FromClean(report, V(o, "label"));
            LedgerStore.Append(entry);
            Console.WriteLine($"台账已记录：{entry.Id} → {AppPaths.LedgerFile}");
        }

        return report.Cancelled ? 2 : 0;
    }

    private static int CmdRulesUpdate(Dictionary<string, List<string>> o)
    {
        var ctx = LoadContext(o, quiet: true);
        var source = V(o, "url") ?? ctx.cfg.RuleUpdateUrl;
        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine("未提供更新地址：请用 --url <URL|文件> 或先在配置中设置 ruleUpdateUrl。");
            Console.WriteLine($"配置文件：{AppPaths.ConfigFile}");
            return 1;
        }

        Console.WriteLine($"当前规则：{AppPaths.RulesFile}（{ctx.lib.Categories.Count} 个类别）");
        Console.WriteLine($"更新源：{source}");
        var check = RuleUpdater.Fetch(source, ctx.lib);

        if (check.Problems.Count > 0)
        {
            Console.WriteLine("校验问题：");
            foreach (var p in check.Problems) Console.WriteLine("  - " + p);
        }

        if (!check.Success)
        {
            Console.WriteLine("检查失败：" + check.Message);
            return 1;
        }

        Console.WriteLine("差异：" + check.DiffSummary);
        foreach (var w in check.Warnings) Console.WriteLine("提示：" + w);

        if (F(o, "check"))
        {
            Console.WriteLine("（--check 模式：仅检查，未应用任何更改）");
            return 0;
        }

        if (!F(o, "yes"))
        {
            Console.WriteLine("如需应用更新请加 --yes（将在备份旧版后写入用户规则目录，可随时回退）。");
            return 2;
        }

        check = RuleUpdater.Apply(check);
        Console.WriteLine(check.Message);
        if (check.Success)
        {
            if (V(o, "url") is { } u)
            {
                ctx.cfg.RuleUpdateUrl = u;
                ConfigStore.Save(ctx.cfg);
                Console.WriteLine("已将更新地址保存到配置。");
            }

            var reloaded = RuleLibraryLoader.Load();
            Console.WriteLine($"重载验证：{reloaded.Categories.Count} 个类别可用。");
        }

        return check.Success ? 0 : 1;
    }

    private static int CmdBigFiles(Dictionary<string, List<string>> o)
    {
        var ctx = LoadContext(o, quiet: true);
        var root = V(o, "root") ?? "/";
        var minMb = int.TryParse(V(o, "min-mb"), out var mm) ? mm : 100;
        var top = int.TryParse(V(o, "top"), out var t) ? t : 100;

        Console.WriteLine($"大文件扫描：{root}（阈值 {minMb} MB，Top {top}）");
        var result = BigFileScanner.Scan(root, minMb * 1024L * 1024L, top, new BigFileConsoleProgress(), CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"{"大小",14}  文件");
        Console.WriteLine(new string('-', 90));
        foreach (var f in result.Items.Take(top))
        {
            Console.WriteLine($"{HumanSize.Format(f.Size),14}  {f.Path}");
        }

        Console.WriteLine(new string('-', 90));
        Console.WriteLine($"共 {result.Items.Count} 个命中 · 扫描 {result.ScannedFiles:N0} 个文件 / {HumanSize.Format(result.ScannedBytes)} · 耗时 {result.ElapsedSeconds:0.0}s");

        if (V(o, "json") is { } jf)
        {
            File.WriteAllText(Path.GetFullPath(jf), Json.ToPretty(result), new UTF8Encoding(true));
            Console.WriteLine($"结果 JSON：{Path.GetFullPath(jf)}");
        }

        return result.Cancelled ? 2 : 0;
    }

    private static int CmdBackupList(Dictionary<string, List<string>> o)
    {
        var ctx = LoadContext(o, quiet: true);
        var zone = new BackupZone(ctx.cfg.BackupRoot);
        var sessions = zone.ListSessions();

        Console.WriteLine($"备份区：{zone.Root}");
        Console.WriteLine($"会话数：{sessions.Count}");
        Console.WriteLine();
        Console.WriteLine($"{"会话",-24}{"时间",20}{"剩余文件",10}{"剩余大小",14}{"已恢复",8}{"已清空",8}");
        Console.WriteLine(new string('-', 90));
        foreach (var s in sessions)
        {
            var t = s.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            Console.WriteLine($"{s.Id,-24}{t,20}{s.FilesRemaining,10:N0}{HumanSize.Format(s.BytesRemaining),14}{s.RestoredFiles,8}{s.PurgedFiles,8}");
        }

        if (V(o, "json") is { } jf)
        {
            File.WriteAllText(Path.GetFullPath(jf), Json.ToPretty(sessions), new UTF8Encoding(true));
            Console.WriteLine($"结果 JSON：{Path.GetFullPath(jf)}");
        }

        return 0;
    }

    private static int CmdBackupRestore(Dictionary<string, List<string>> o)
    {
        var ctx = LoadContext(o, quiet: true);
        var sid = V(o, "session");
        if (string.IsNullOrEmpty(sid))
        {
            Console.WriteLine("请指定 --session <会话ID>（可用 backup-list 查看）。");
            return 1;
        }

        var conflict = (V(o, "conflict") ?? "rename").ToLowerInvariant() switch
        {
            "skip" => ConflictPolicy.Skip,
            "overwrite" => ConflictPolicy.Overwrite,
            _ => ConflictPolicy.RenameAndRestore,
        };

        var zone = new BackupZone(ctx.cfg.BackupRoot);
        var before = SystemInfo.GetFreeBytes();
        var report = zone.Restore(sid, new RestoreOptions { Conflict = conflict }, CancellationToken.None,
            new Progress<(long done, long total, string current)>(p =>
            {
                if (p.done % 200 == 0 || p.done == p.total)
                    Console.WriteLine($"  恢复 {p.done}/{p.total} {PathUtil.Truncate(p.current, 70)}");
            }));

        Console.WriteLine($"恢复完成：成功 {report.RestoredFiles} 个（{HumanSize.Format(report.RestoredBytes)}）· 跳过 {report.SkippedFiles} · 失败 {report.FailedFiles}");
        foreach (var e in report.Errors.Take(5)) Console.WriteLine("  ! " + e);

        var entry = LedgerFactory.FromRestore(sid, report, before, SystemInfo.GetFreeBytes());
        LedgerStore.Append(entry);
        Console.WriteLine($"台账已记录：{entry.Id}");
        return report.FailedFiles > 0 ? 1 : 0;
    }

    private static int CmdBackupPurge(Dictionary<string, List<string>> o)
    {
        var ctx = LoadContext(o, quiet: true);
        var zone = new BackupZone(ctx.cfg.BackupRoot);
        var before = SystemInfo.GetFreeBytes();

        if (F(o, "all"))
        {
            if (!F(o, "yes") && !F(o, "dry-run"))
            {
                Console.WriteLine("清空全部备份区需要 --yes 确认（不可恢复；如需保留请先 backup-restore）。");
                return 1;
            }

            if (F(o, "dry-run"))
            {
                var sessions = zone.ListSessions();
                Console.WriteLine($"预演：将清空 {sessions.Count} 个会话，释放约 {HumanSize.Format(sessions.Sum(s => s.BytesRemaining))}");
                return 0;
            }

            var reportAll = zone.PurgeAll(CancellationToken.None);
            Console.WriteLine($"已清空全部备份区：{reportAll.PurgedFiles:N0} 个文件，释放 {HumanSize.Format(reportAll.PurgedBytes)}");
            LedgerStore.Append(LedgerFactory.FromPurge(reportAll, before, SystemInfo.GetFreeBytes()));
            return reportAll.FailedFiles > 0 ? 1 : 0;
        }

        var sid = V(o, "session");
        if (string.IsNullOrEmpty(sid))
        {
            Console.WriteLine("请指定 --session <会话ID> 或 --all。");
            return 1;
        }

        if (!F(o, "yes") && !F(o, "dry-run"))
        {
            Console.WriteLine("清空备份会话需要 --yes 确认（不可恢复）。");
            return 1;
        }

        if (F(o, "dry-run"))
        {
            var info = zone.ListSessions().FirstOrDefault(s => s.Id == sid);
            Console.WriteLine(info == null
                ? "未找到该会话。"
                : $"预演：会话 {sid} 将释放 {HumanSize.Format(info.BytesRemaining)}（{info.FilesRemaining:N0} 个文件）");
            return 0;
        }

        var report = zone.Purge(sid, CancellationToken.None);
        Console.WriteLine($"已清空会话 {sid}：{report.PurgedFiles:N0} 个文件，释放 {HumanSize.Format(report.PurgedBytes)}");
        LedgerStore.Append(LedgerFactory.FromPurge(report, before, SystemInfo.GetFreeBytes()));
        return report.FailedFiles > 0 ? 1 : 0;
    }

    private static int CmdLedger(Dictionary<string, List<string>> o)
    {
        _ = LoadContext(o, quiet: true);
        var limit = int.TryParse(V(o, "limit"), out var l) ? l : 50;
        var entries = LedgerStore.LoadAll(limit);

        Console.WriteLine($"台账文件：{AppPaths.LedgerFile}");
        Console.WriteLine($"最近 {entries.Count} 条记录（共 {LedgerStore.Count()} 条）：");
        Console.WriteLine();
        Console.WriteLine($"{"时间",20}{"类型",10}{"模式",14}{"释放",12}{"备份",12}{"取消",6}");
        Console.WriteLine(new string('-', 82));
        foreach (var e in entries)
        {
            Console.WriteLine($"{e.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss,20}{KindText(e.Kind),10}{e.Mode,14}{HumanSize.Format(Math.Max(0, e.ReleasedBytes)),12}{HumanSize.Format(Math.Max(0, e.BackedUpBytes)),12}{(e.Cancelled ? "是" : "否"),6}");
        }

        if (V(o, "json") is { } jf)
        {
            File.WriteAllText(Path.GetFullPath(jf), Json.ToPretty(entries), new UTF8Encoding(true));
            Console.WriteLine($"台账 JSON：{Path.GetFullPath(jf)}");
        }

        return 0;
    }

    private static int CmdLedgerExport(Dictionary<string, List<string>> o)
    {
        _ = LoadContext(o, quiet: true);
        var format = (V(o, "format") ?? "csv").ToLowerInvariant();
        var outFile = V(o, "out");
        if (string.IsNullOrEmpty(outFile))
        {
            Console.WriteLine("请指定 --out <文件路径>。");
            return 1;
        }

        var limit = int.TryParse(V(o, "limit"), out var l) ? l : 10000;
        var entries = LedgerStore.LoadAll(limit);

        string content;
        var ext = "";
        switch (format)
        {
            case "csv":
                content = LedgerExporter.ToCsv(entries);
                ext = ".csv";
                break;
            case "csv-detail":
                content = LedgerExporter.ToCategoryCsv(entries);
                ext = ".csv";
                break;
            case "html":
                content = LedgerExporter.ToHtml(entries);
                ext = ".html";
                break;
            default:
                Console.WriteLine("未知格式（支持 csv / csv-detail / html）。");
                return 1;
        }

        var final = Path.GetFullPath(outFile);
        if (!final.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) final += ext;
        File.WriteAllText(final, content, new UTF8Encoding(true));
        Console.WriteLine($"已导出 {entries.Count} 条记录 → {final}（{new FileInfo(final).Length:N0} 字节）");
        return 0;
    }

    private static int CmdSelfTest(Dictionary<string, List<string>> o)
    {
        Console.WriteLine($"{BuildInfo.ProductName} v{BuildInfo.Version} · 内置自检");
        Console.WriteLine($"样本目录：{V(o, "samples") ?? "（默认 /tmp/cleanmaster-selftest）"}");
        Console.WriteLine();

        var results = SelfTestRunner.Run(V(o, "samples"), s => Console.WriteLine(s));
        Console.WriteLine();
        Console.WriteLine("===== 自检结果 =====");
        foreach (var r in results)
        {
            Console.WriteLine($"{(r.Passed ? "通过" : "失败")} · {r.Name} · {r.Ms:0} ms");
            Console.WriteLine($"    {r.Detail}");
        }

        var passed = results.Count(x => x.Passed);
        Console.WriteLine($"共 {results.Count} 项，通过 {passed}，失败 {results.Count - passed}");

        if (V(o, "json") is { } jf)
        {
            File.WriteAllText(Path.GetFullPath(jf), Json.ToPretty(results), new UTF8Encoding(true));
            Console.WriteLine($"自检 JSON：{Path.GetFullPath(jf)}");
        }

        return passed == results.Count ? 0 : 1;
    }

    // ================= 基础设施 =================

    private sealed record Context(AppConfig cfg, RuleLibrary lib, SafetyFilter safety);

    private static Context LoadContext(Dictionary<string, List<string>> o, bool quiet = false)
    {
        var cfg = ConfigStore.InitRuntime();
        if (V(o, "backup-root") is { } br) cfg.BackupRoot = Path.GetFullPath(br);

        var lib = RuleLibraryLoader.Load(V(o, "rules"));

        foreach (var kv in Vs(o, "set-root"))
        {
            var parts = kv.Split('=', 2);
            if (parts.Length != 2) continue;
            var cat = lib.Categories.FirstOrDefault(c => string.Equals(c.Id, parts[0], StringComparison.OrdinalIgnoreCase));
            if (cat != null)
            {
                cat.Paths.Clear();
                cat.Paths.Add(parts[1]);
            }
        }

        var safety = RuleLibraryLoader.LoadSafetyFilter();
        if (!quiet) AppLog.Info($"CLI 加载规则：{lib.Categories.Count} 个类别");
        return new Context(cfg, lib, safety);
    }

    private sealed class ScanConsoleProgress : IProgress<ScanProgress>
    {
        private long _last;

        public void Report(ScanProgress p)
        {
            var now = Environment.TickCount64;
            if (p.Phase == "scan" && now - _last < 1500) return;
            _last = now;
            Console.WriteLine($"  · [{p.CategoriesDone}/{p.CategoriesTotal}] {p.Files:N0} 个文件 · {HumanSize.Format(p.Bytes)} · {p.Seconds:0.0}s · {p.CurrentCategory}");
        }
    }

    private sealed class CleanConsoleProgress : IProgress<CleanProgress>
    {
        private long _last;

        public void Report(CleanProgress p)
        {
            var now = Environment.TickCount64;
            if (now - _last < 1500 && p.Phase != "restorepoint") return;
            _last = now;
            Console.WriteLine($"  · [{p.CategoriesDone}/{p.CategoriesTotal}] 处理 {p.DoneFiles:N0}（{HumanSize.Format(p.DoneBytes)}）· 释放 {HumanSize.Format(p.FreedBytes)} · 跳过 {p.Skipped:N0} · {p.Seconds:0.0}s · {p.CurrentCategory}");
        }
    }

    private sealed class BigFileConsoleProgress : IProgress<BigFileProgress>
    {
        private long _last;

        public void Report(BigFileProgress p)
        {
            var now = Environment.TickCount64;
            if (now - _last < 2500) return;
            _last = now;
            Console.WriteLine($"  · 已扫描 {p.Files:N0} 个文件 / {HumanSize.Format(p.Bytes)} · {p.Seconds:0.0}s");
        }
    }

    private static (string cmd, Dictionary<string, List<string>> opts) Parse(string[] args)
    {
        var valueOpts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "category", "json", "mode", "min-age", "set-root", "exclude", "root", "min-mb", "top",
            "session", "conflict", "format", "out", "limit", "samples", "data-dir", "backup-root",
            "rules", "max-seconds", "label", "url",
        };

        var o = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var cmd = "";
        var i = 0;
        while (i < args.Length)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                var key = a.Substring(2);
                if (valueOpts.Contains(key) && i + 1 < args.Length)
                {
                    Add(o, key, args[i + 1]);
                    i += 2;
                }
                else
                {
                    Add(o, key, "1");
                    i += 1;
                }
            }
            else
            {
                if (cmd.Length == 0) cmd = a.ToLowerInvariant();
                else Add(o, "args", a);
                i += 1;
            }
        }

        return (cmd, o);
    }

    private static void Add(Dictionary<string, List<string>> o, string key, string value)
    {
        if (!o.TryGetValue(key, out var list))
        {
            list = new List<string>();
            o[key] = list;
        }

        list.Add(value);
    }

    private static string? V(Dictionary<string, List<string>> o, string key) =>
        o.TryGetValue(key, out var l) && l.Count > 0 ? l[^1] : null;

    private static List<string> Vs(Dictionary<string, List<string>> o, string key) =>
        o.TryGetValue(key, out var l) ? l : new List<string>();

    private static bool F(Dictionary<string, List<string>> o, string key) => o.ContainsKey(key);

    private static List<string> SplitCsv(List<string> values) =>
        values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();

    private static string RiskText(RiskLevel r) => r switch
    {
        RiskLevel.Safe => "安全",
        RiskLevel.Moderate => "谨慎",
        _ => "高风险",
    };

    private static string ModeText(CleanMode m) => m switch
    {
        CleanMode.AllToBackup => "全部备份",
        CleanMode.AllPermanent => "全部永久删除",
        _ => "标准",
    };

    private static string KindText(string kind) => kind switch
    {
        "clean" => "清理",
        "restore" => "恢复",
        "purge" => "清空备份区",
        "tool" => "工具",
        _ => kind,
    };

    private static int CmdJournalVacuum(Dictionary<string, List<string>> o)
    {
        var size = V(o, "size") ?? "100M";
        var time = V(o, "time") ?? "7d";

        // 参数合法性检查（交给 journalctl 报错前先拦截明显非法值）
        if (!System.Text.RegularExpressions.Regex.IsMatch(size, @"^\d+(K|M|G|T)?$"))
        {
            Console.WriteLine($"错误：--size 格式应为数字+单位（如 100M、1G），实际：{size}");
            return 2;
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(time, @"^\d+(s|m|h|d|w|months|years)?$"))
        {
            Console.WriteLine($"错误：--time 格式应为数字+单位（如 7d、30d），实际：{time}");
            return 2;
        }

        if (!F(o, "yes"))
        {
            Console.WriteLine("本命令将执行：sudo journalctl --vacuum-size=" + size + " --vacuum-time=" + time);
            Console.WriteLine("需要使用 --yes 确认执行。先用 --size 0 --time 0 可查看当前占用。");
            return 2;
        }

        var before = SystemInfo.GetFreeBytes();
        Console.WriteLine("查看当前日志占用…");
        var usage = RunCmd("sudo", "journalctl --disk-usage");
        Console.WriteLine(usage.Trim().Length > 0 ? usage.Trim() : "（journalctl 无输出，可能无 systemd 日志）");

        Console.WriteLine($"执行：sudo journalctl --vacuum-size={size} --vacuum-time={time}");
        var output = RunCmd("sudo", $"journalctl --vacuum-size={size} --vacuum-time={time}");
        Console.WriteLine(output.Trim());

        var after = SystemInfo.GetFreeBytes();
        Console.WriteLine($"释放：{HumanSize.Format(after - before)}（HOME 所在分区可用空间 {HumanSize.Format(before)} → {HumanSize.Format(after)}）");

        try
        {
            var entry = LedgerFactory.FromTool("journal-vacuum", $"journalctl --vacuum-size={size} --vacuum-time={time}", after - before, 0);
            LedgerStore.Append(entry);
        }
        catch (Exception ex)
        {
            Console.WriteLine("（台账记录失败：" + ex.Message + "）");
        }

        return 0;
    }

    private static string RunCmd(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi);
        if (p == null) return "（无法启动进程）";
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (so + se).Trim();
    }

    // ================= 帮助 =================

    private static void PrintHelp()
    {
        Console.WriteLine($"""
{BuildInfo.ProductName} 命令行版 v{BuildInfo.Version}
用法：CleanMasterCli <命令> [选项]

命令：
  scan            扫描各类别可清理内容
                  [--category id1,id2] [--json 输出路径]
  clean           执行清理（需要 --yes；先 --dry-run 可预演）
                  --category id1,id2 | --recommend | --all-safe
                  [--mode standard|backup|permanent] [--dry-run] [--yes]
                  [--restore-point] [--max-seconds N] [--label 说明]
                  [--min-age id=天数 ...] [--exclude 路径 ...]
  bigfiles        大文件扫描 [--root /] [--min-mb 100] [--top 100] [--json 输出]
  backup-list     列出备份区会话 [--json 输出]
  backup-restore  从备份区恢复 --session <id> [--conflict rename|skip|overwrite]
  backup-purge    清空备份区 --session <id> | --all [--yes] [--dry-run]
  ledger          查看台账 [--limit N] [--json 输出]
  ledger-export   导出台账 --format csv|csv-detail|html --out 文件 [--limit N]
  selftest        运行内置自检 [--samples 样本目录] [--json 输出]
  journal-vacuum  压缩/清理 systemd 日志（journalctl --vacuum，需 root）
                  [--size 100M] [--time 7d] [--yes]
  rules-check     校验规则库
  rules-update    检查/应用规则更新 [--url URL|文件] [--check] [--yes]
  version         显示版本

通用选项：
  --rules 规则文件路径        --data-dir 数据目录
  --backup-root 备份区路径    --set-root 类别id=路径（替换类别扫描根，测试用）
""");
    }
}
