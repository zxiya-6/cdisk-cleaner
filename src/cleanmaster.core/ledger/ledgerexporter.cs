using System.Globalization;
using System.Net;
using System.Text;
using CleanMaster.Core.Models;
using CleanMaster.Core.Util;

namespace CleanMaster.Core.Ledger;

/// <summary>台账导出：CSV（Excel 兼容）与自包含 HTML 报告。</summary>
public static class LedgerExporter
{
    public static string ToCsv(IReadOnlyList<LedgerEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("会话ID,类型,开始时间,结束时间,耗时(秒),模式,管理员,已取消,试运行,清理前可用(字节),清理后可用(字节),实际释放(字节),释放(可读),备份区(字节),回收站(字节),备份会话,类别数,文件数,跳过数,错误数,备注");
        foreach (var e in entries)
        {
            long files = 0, skipped = 0;
            foreach (var c in e.Categories) { files += c.Files; skipped += c.Skipped; }

            sb.Append(Csv(e.Id)).Append(',')
              .Append(Csv(KindText(e.Kind))).Append(',')
              .Append(Csv(e.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(Csv(e.FinishedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(e.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(e.Mode)).Append(',')
              .Append(e.Admin ? "是" : "否").Append(',')
              .Append(e.Cancelled ? "是" : "否").Append(',')
              .Append(e.DryRun ? "是" : "否").Append(',')
              .Append(e.FreeBeforeBytes).Append(',')
              .Append(e.FreeAfterBytes).Append(',')
              .Append(e.ReleasedBytes).Append(',')
              .Append(Csv(HumanSize.Format(e.ReleasedBytes))).Append(',')
              .Append(e.BackedUpBytes).Append(',')
              .Append(e.TrashedBytes).Append(',')
              .Append(Csv(e.BackupSessionId ?? "")).Append(',')
              .Append(e.Categories.Count).Append(',')
              .Append(files).Append(',')
              .Append(skipped).Append(',')
              .Append(e.Errors.Count).Append(',')
              .Append(Csv(e.Notes ?? ""))
              .AppendLine();
        }

        return sb.ToString();
    }

    public static string ToCategoryCsv(IReadOnlyList<LedgerEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("会话ID,类型,时间,类别ID,类别名,文件数,字节数,策略,跳过数,备注");
        foreach (var e in entries)
        {
            var time = e.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            if (e.Categories.Count == 0)
            {
                sb.Append(Csv(e.Id)).Append(',').Append(Csv(KindText(e.Kind))).Append(',').Append(Csv(time))
                  .Append(",,,,,,").Append(Csv(e.Notes ?? "")).AppendLine();
                continue;
            }

            foreach (var c in e.Categories)
            {
                sb.Append(Csv(e.Id)).Append(',')
                  .Append(Csv(KindText(e.Kind))).Append(',')
                  .Append(Csv(time)).Append(',')
                  .Append(Csv(c.Id)).Append(',')
                  .Append(Csv(c.Name)).Append(',')
                  .Append(c.Files).Append(',')
                  .Append(c.Bytes).Append(',')
                  .Append(Csv(c.Strategy)).Append(',')
                  .Append(c.Skipped).Append(',')
                  .Append(Csv(e.Notes ?? ""))
                  .AppendLine();
            }
        }

        return sb.ToString();
    }

    public static string ToHtml(IReadOnlyList<LedgerEntry> entries, string title = "清理台账")
    {
        long totalReleased = 0, totalBacked = 0, totalTrashed = 0, totalFiles = 0;
        foreach (var e in entries)
        {
            totalReleased += Math.Max(0, e.ReleasedBytes);
            totalBacked += Math.Max(0, e.BackedUpBytes);
            totalTrashed += Math.Max(0, e.TrashedBytes);
            foreach (var c in e.Categories) totalFiles += c.Files;
        }

        var sb = new StringBuilder(64 * 1024);
        sb.Append("<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append($"<title>{WebUtility.HtmlEncode(title)} - {BuildInfo.ProductName}</title>\n");
        sb.Append("<style>\n");
        sb.Append("""
:root { --ink:#16202b; --muted:#5b6673; --line:#dfe3e8; --bg:#fbfbfa; --card:#ffffff; --accent:#0b57d0; --ok:#1a7f37; --warn:#b54708; }
* { box-sizing: border-box; }
body { margin:0; background:var(--bg); color:var(--ink); font:14px/1.6 "Segoe UI","Microsoft YaHei UI","PingFang SC",system-ui,sans-serif; }
.wrap { max-width:1080px; margin:0 auto; padding:32px 20px 64px; }
h1 { font-size:24px; margin:0 0 4px; letter-spacing:.01em; }
.sub { color:var(--muted); font-size:13px; margin-bottom:24px; }
.metrics { display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:12px; margin:18px 0 26px; }
.metric { background:var(--card); border:1px solid var(--line); padding:14px 16px; }
.metric .k { font-size:12px; color:var(--muted); }
.metric .v { font-size:20px; font-weight:600; margin-top:2px; font-variant-numeric:tabular-nums; }
table { width:100%; border-collapse:collapse; background:var(--card); border:1px solid var(--line); }
th,td { padding:8px 10px; border-bottom:1px solid var(--line); text-align:left; vertical-align:top; font-size:13px; }
th { background:#f3f4f2; font-weight:600; white-space:nowrap; }
td.num, th.num { text-align:right; font-variant-numeric:tabular-nums; white-space:nowrap; }
tr:last-child td { border-bottom:none; }
details { margin:10px 0 22px; }
summary { cursor:pointer; color:var(--accent); font-weight:600; font-size:13px; }
.tag { display:inline-block; padding:1px 7px; border:1px solid var(--line); border-radius:999px; font-size:12px; color:var(--muted); background:#fff; }
.tag.ok { color:var(--ok); border-color:#bfd8c5; }
.tag.warn { color:var(--warn); border-color:#ecd9b8; }
.foot { margin-top:28px; color:var(--muted); font-size:12px; }
h2 { font-size:16px; margin:28px 0 10px; }
""");
        sb.Append("</style>\n</head>\n<body>\n<div class=\"wrap\">\n");
        sb.Append($"<h1>{WebUtility.HtmlEncode(title)}</h1>\n");
        sb.Append($"<div class=\"sub\">{BuildInfo.ProductName} v{BuildInfo.Version} · 生成时间 {WebUtility.HtmlEncode(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))} · 共 {entries.Count} 条记录</div>\n");

        sb.Append("<div class=\"metrics\">\n");
        Metric(sb, "累计实际释放", HumanSize.Format(totalReleased));
        Metric(sb, "清理文件数", totalFiles.ToString("N0", CultureInfo.InvariantCulture) + " 个");
        Metric(sb, "备份区收容（累计）", HumanSize.Format(totalBacked));
        Metric(sb, "移至回收站（累计）", HumanSize.Format(totalTrashed));
        sb.Append("</div>\n");

        sb.Append("<h2>记录列表</h2>\n");
        sb.Append("<table>\n<thead><tr><th>时间</th><th>类型</th><th>模式</th><th class=\"num\">文件数</th><th class=\"num\">实际释放</th><th class=\"num\">备份区</th><th class=\"num\">回收站</th><th class=\"num\">跳过</th><th>状态</th></tr></thead>\n<tbody>\n");
        foreach (var e in entries)
        {
            long files = 0, skipped = 0;
            foreach (var c in e.Categories) { files += c.Files; skipped += c.Skipped; }

            var status = e.Cancelled ? "<span class=\"tag warn\">已取消</span>"
                : e.DryRun ? "<span class=\"tag\">试运行</span>"
                : e.Errors.Count > 0 ? "<span class=\"tag warn\">有错误</span>"
                : "<span class=\"tag ok\">成功</span>";

            sb.Append("<tr>")
              .Append($"<td>{WebUtility.HtmlEncode(e.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))}</td>")
              .Append($"<td>{WebUtility.HtmlEncode(KindText(e.Kind))}</td>")
              .Append($"<td>{WebUtility.HtmlEncode(e.Mode)}</td>")
              .Append($"<td class=\"num\">{files:N0}</td>")
              .Append($"<td class=\"num\">{WebUtility.HtmlEncode(HumanSize.Format(Math.Max(0, e.ReleasedBytes)))}</td>")
              .Append($"<td class=\"num\">{WebUtility.HtmlEncode(HumanSize.Format(Math.Max(0, e.BackedUpBytes)))}</td>")
              .Append($"<td class=\"num\">{WebUtility.HtmlEncode(HumanSize.Format(Math.Max(0, e.TrashedBytes)))}</td>")
              .Append($"<td class=\"num\">{skipped:N0}</td>")
              .Append($"<td>{status}</td>")
              .AppendLine("</tr>");
        }

        sb.Append("</tbody>\n</table>\n");

        // 每次清理的类别明细
        foreach (var e in entries)
        {
            if (e.Categories.Count == 0) continue;
            sb.Append($"<details>\n<summary>{WebUtility.HtmlEncode(e.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))} · {WebUtility.HtmlEncode(KindText(e.Kind))} 明细（{e.Categories.Count} 个类别）</summary>\n");
            sb.Append("<table>\n<thead><tr><th>类别</th><th>策略</th><th class=\"num\">文件数</th><th class=\"num\">大小</th><th class=\"num\">跳过</th></tr></thead>\n<tbody>\n");
            foreach (var c in e.Categories)
            {
                sb.Append($"<tr><td>{WebUtility.HtmlEncode(c.Name)}</td><td>{WebUtility.HtmlEncode(c.Strategy)}</td>")
                  .Append($"<td class=\"num\">{c.Files:N0}</td><td class=\"num\">{WebUtility.HtmlEncode(HumanSize.Format(c.Bytes))}</td>")
                  .Append($"<td class=\"num\">{c.Skipped:N0}</td></tr>\n");
            }

            sb.Append("</tbody>\n</table>\n</details>\n");
        }

        sb.Append($"<div class=\"foot\">本报告由 {BuildInfo.ProductName} 本地生成，不包含任何上传行为。数据文件：%LOCALAPPDATA%\\C盘清理助手\\ledger\\ledger.jsonl</div>\n");
        sb.Append("</div>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static void Metric(StringBuilder sb, string key, string value)
    {
        sb.Append($"<div class=\"metric\"><div class=\"k\">{WebUtility.HtmlEncode(key)}</div><div class=\"v\">{WebUtility.HtmlEncode(value)}</div></div>\n");
    }

    private static string KindText(string kind) => kind switch
    {
        "clean" => "清理",
        "restore" => "恢复",
        "purge" => "清空备份区",
        "tool" => "工具操作",
        _ => kind,
    };

    private static string Csv(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
