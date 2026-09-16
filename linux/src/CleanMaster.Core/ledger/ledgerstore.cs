using System.Text;
using CleanMaster.Core.Config;
using CleanMaster.Core.Models;
using CleanMaster.Core.Util;

namespace CleanMaster.Core.Ledger;

/// <summary>台账存储：JSONL 追加式记录，天然防止重启丢数据。</summary>
public static class LedgerStore
{
    private static readonly object Sync = new();

    public static void Append(LedgerEntry entry, string? ledgerFile = null)
    {
        var file = ledgerFile ?? AppPaths.LedgerFile;
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var line = Json.ToCompact(entry);
            lock (Sync)
            {
                File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("写入台账失败", ex);
        }
    }

    /// <summary>读取台账（最新在前，最多 max 条）。</summary>
    public static List<LedgerEntry> LoadAll(int max = 1000, string? ledgerFile = null)
    {
        var list = new List<LedgerEntry>();
        var file = ledgerFile ?? AppPaths.LedgerFile;
        try
        {
            if (!File.Exists(file)) return list;
            var lines = File.ReadAllLines(file, Encoding.UTF8);
            for (var i = lines.Length - 1; i >= 0 && list.Count < max; i--)
            {
                var l = lines[i];
                if (string.IsNullOrWhiteSpace(l)) continue;
                try
                {
                    var e = Json.FromJson<LedgerEntry>(l);
                    if (e != null) list.Add(e);
                }
                catch
                {
                    // 跳过损坏行
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("读取台账失败", ex);
        }

        return list;
    }

    public static int Count(string? ledgerFile = null)
    {
        try
        {
            var file = ledgerFile ?? AppPaths.LedgerFile;
            if (!File.Exists(file)) return 0;
            var n = 0;
            foreach (var line in File.ReadLines(file, Encoding.UTF8))
                if (!string.IsNullOrWhiteSpace(line)) n++;
            return n;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>把执行报告映射为台账记录。</summary>
public static class LedgerFactory
{
    public static LedgerEntry FromClean(CleanReport r, string? notes = null)
    {
        var e = new LedgerEntry
        {
            Id = r.SessionId,
            Kind = "clean",
            StartedAtUtc = r.StartedAtUtc,
            FinishedAtUtc = r.FinishedAtUtc,
            Mode = ModeText(r.Mode),
            Admin = r.Admin,
            Cancelled = r.Cancelled,
            DryRun = r.DryRun,
            FreeBeforeBytes = r.FreeBeforeBytes,
            FreeAfterBytes = r.FreeAfterBytes,
            ReleasedBytes = r.DeletedBytes,
            BackedUpBytes = r.BackedUpBytes,
            TrashedBytes = r.TrashedBytes,
            BackupSessionId = r.BackupSessionId,
            ProductVersion = BuildInfo.Version,
            Machine = SystemInfo.MachineName,
            Os = SystemInfo.OsDescription,
            Notes = notes,
        };

        foreach (var c in r.Categories)
        {
            var parts = new List<string>();
            if (c.DeletedFiles > 0) parts.Add("delete");
            if (c.BackedUpFiles > 0) parts.Add("backup");
            if (c.TrashedFiles > 0) parts.Add("trash");
            e.Categories.Add(new LedgerCategory
            {
                Id = c.CategoryId,
                Name = c.Name,
                Files = c.DeletedFiles + c.BackedUpFiles + c.TrashedFiles,
                Bytes = c.DeletedBytes + c.BackedUpBytes + c.TrashedBytes,
                Strategy = parts.Count > 0 ? string.Join("+", parts) : "none",
                Skipped = c.SkippedFiles,
            });
        }

        foreach (var er in r.Errors) e.Errors.Add(er);
        return e;
    }

    public static LedgerEntry FromRestore(string sessionId, RestoreReport r, long freeBefore, long freeAfter)
    {
        var e = new LedgerEntry
        {
            Id = MakeId("restore"),
            Kind = "restore",
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-r.RestoredFiles * 0.001),
            FinishedAtUtc = DateTime.UtcNow,
            Mode = "restore",
            Admin = SystemInfo.IsAdmin(),
            FreeBeforeBytes = freeBefore,
            FreeAfterBytes = freeAfter,
            ReleasedBytes = 0,
            BackedUpBytes = -r.RestoredBytes,
            BackupSessionId = sessionId,
            ProductVersion = BuildInfo.Version,
            Machine = SystemInfo.MachineName,
            Os = SystemInfo.OsDescription,
            Notes = $"从备份区恢复 {r.RestoredFiles} 个文件（{HumanSize.Format(r.RestoredBytes)}）",
        };
        foreach (var er in r.Errors) e.Errors.Add(er);
        return e;
    }

    public static LedgerEntry FromPurge(PurgeReport r, long freeBefore, long freeAfter)
    {
        var e = new LedgerEntry
        {
            Id = MakeId("purge"),
            Kind = "purge",
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-1),
            FinishedAtUtc = DateTime.UtcNow,
            Mode = r.All ? "purgeAll" : "purge",
            Admin = SystemInfo.IsAdmin(),
            FreeBeforeBytes = freeBefore,
            FreeAfterBytes = freeAfter,
            ReleasedBytes = r.PurgedBytes,
            BackupSessionId = r.SessionId,
            ProductVersion = BuildInfo.Version,
            Machine = SystemInfo.MachineName,
            Os = SystemInfo.OsDescription,
            Notes = $"清空备份区释放 {HumanSize.Format(r.PurgedBytes)}（{r.PurgedFiles} 个文件）",
        };
        foreach (var er in r.Errors) e.Errors.Add(er);
        return e;
    }

    public static LedgerEntry FromTool(string toolName, string resultText, long freeBefore, long freeAfter)
    {
        return new LedgerEntry
        {
            Id = MakeId("tool"),
            Kind = "tool",
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-1),
            FinishedAtUtc = DateTime.UtcNow,
            Mode = toolName,
            Admin = SystemInfo.IsAdmin(),
            FreeBeforeBytes = freeBefore,
            FreeAfterBytes = freeAfter,
            ReleasedBytes = Math.Max(0, freeAfter - freeBefore),
            ProductVersion = BuildInfo.Version,
            Machine = SystemInfo.MachineName,
            Os = SystemInfo.OsDescription,
            Notes = resultText,
        };
    }

    private static string MakeId(string prefix) =>
        prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Random.Shared.Next(0x1000, 0x10000).ToString("x4");

    private static string ModeText(CleanMode mode) => mode switch
    {
        CleanMode.AllToBackup => "allToBackup",
        CleanMode.AllPermanent => "allPermanent",
        _ => "standard",
    };
}
