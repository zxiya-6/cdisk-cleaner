using System.Text;
using CleanMaster.Core.Config;
using CleanMaster.Core.Models;
using CleanMaster.Core.Util;

namespace CleanMaster.Core.Rules;

/// <summary>规则更新检查结果。</summary>
public sealed class RuleUpdateCheck
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? Source { get; set; }
    public string? Json { get; set; }
    public RuleLibrary? Library { get; set; }
    public List<string> Problems { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? DiffSummary { get; set; }
}

/// <summary>
/// 规则在线更新器：下载 → 结构校验 → 安全预检 → 差异对比 → 备份应用 → 复核（失败自动回滚）。
/// 仅下载规则文件，不上传任何本机数据；执行期安全保护名单不可被远程修改。
/// </summary>
public static class RuleUpdater
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            h.DefaultRequestHeaders.UserAgent.ParseAdd($"CleanMaster/{BuildInfo.Version} (+local-cleaner)");
        }
        catch
        {
        }

        return h;
    }

    /// <summary>下载并校验（不写盘）。urlOrFile 支持 http(s):// 或本地文件路径。</summary>
    public static RuleUpdateCheck Fetch(string urlOrFile, RuleLibrary? current)
    {
        var r = new RuleUpdateCheck { Source = urlOrFile };
        try
        {
            string text;
            if (IsHttp(urlOrFile))
            {
                using var resp = Http.GetAsync(urlOrFile).GetAwaiter().GetResult();
                r.Message = $"HTTP {(int)resp.StatusCode}";
                resp.EnsureSuccessStatusCode();
                text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (urlOrFile.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !IsLocal(urlOrFile))
                    r.Warnings.Add("更新源使用未加密的 HTTP，建议改用 HTTPS 或受信的本地文件");
            }
            else
            {
                text = File.ReadAllText(urlOrFile, Encoding.UTF8);
                r.Message = "本地文件";
            }

            var lib = Json.FromJson<RuleLibrary>(text, pretty: true);
            if (lib == null)
            {
                r.Message = "解析失败：不是有效的规则库 JSON";
                return r;
            }

            r.Problems.AddRange(RuleLibraryLoader.Validate(lib));

            // 模板安全预检：解析路径前缀并对照保护名单（防止更新版规则误配到个人/系统保护区）
            var filter = RuleLibraryLoader.LoadSafetyFilter();
            foreach (var c in lib.Categories)
            {
                if (c.Kind == RuleKind.EmptyRecycleBin || c.Paths.Count == 0) continue;
                foreach (var raw in c.Paths)
                {
                    var expanded = PathUtil.ExpandEnv(raw).Trim();
                    if (expanded.Length == 0) continue;
                    var probe = expanded;
                    var wild = probe.IndexOfAny(new[] { '*', '?' });
                    if (wild >= 0)
                    {
                        var cut = probe.LastIndexOf('\\', wild);
                        probe = cut > 0 ? probe.Substring(0, cut) : probe;
                    }

                    var d = filter.Check(probe);
                    if (!d.Allowed)
                        r.Warnings.Add($"类别「{c.Id}」路径 {raw} 触及保护区域（{d.Reason}），执行时会被自动跳过");
                }
            }

            r.Library = lib;
            r.Json = text;
            r.Success = r.Problems.Count == 0;
            r.Message = r.Success ? "校验通过" : "校验未通过";
            if (r.Success) r.DiffSummary = Diff(current, lib);
        }
        catch (Exception ex)
        {
            r.Message = ex.Message;
            AppLog.Exception("规则更新下载/解析失败", ex);
        }

        return r;
    }

    /// <summary>对比新旧规则库，返回可读差异摘要。</summary>
    public static string Diff(RuleLibrary? oldLib, RuleLibrary newLib)
    {
        if (oldLib == null) return $"新规则库共 {newLib.Categories.Count} 个类别";

        var oldIds = oldLib.Categories.ToDictionary(c => c.Id, c => c, StringComparer.OrdinalIgnoreCase);
        var newIds = newLib.Categories.ToDictionary(c => c.Id, c => c, StringComparer.OrdinalIgnoreCase);
        var added = newIds.Keys.Where(k => !oldIds.ContainsKey(k)).ToList();
        var removed = oldIds.Keys.Where(k => !newIds.ContainsKey(k)).ToList();
        var changed = newIds.Keys
            .Where(k => oldIds.ContainsKey(k) && !Json.ToCompact(oldIds[k]).Equals(Json.ToCompact(newIds[k])))
            .ToList();

        var parts = new List<string> { $"类别数 {oldLib.Categories.Count} → {newLib.Categories.Count}" };
        if (added.Count > 0) parts.Add("新增：" + string.Join("、", added.Take(10)) + (added.Count > 10 ? "…" : ""));
        if (removed.Count > 0) parts.Add("移除：" + string.Join("、", removed.Take(10)) + (removed.Count > 10 ? "…" : ""));
        if (changed.Count > 0) parts.Add("修改：" + string.Join("、", changed.Take(10)) + (changed.Count > 10 ? "…" : ""));
        if (added.Count == 0 && removed.Count == 0 && changed.Count == 0) parts.Add("内容无变化");
        return string.Join("；", parts);
    }

    /// <summary>应用更新：备份旧版 → 写入用户规则目录 → 复核（失败自动还原）。</summary>
    public static RuleUpdateCheck Apply(RuleUpdateCheck check, string? targetFile = null)
    {
        if (!check.Success || check.Json == null)
        {
            check.Success = false;
            return check;
        }

        var target = targetFile ?? AppPaths.UserRulesFile;
        string? backup = null;
        try
        {
            var dir = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(dir);

            if (File.Exists(target))
            {
                backup = target + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(target, backup, overwrite: true);
                PruneBackups(dir);
            }
            else if (File.Exists(AppPaths.ExeRulesFile) && !PathUtil.EqualsPath(target, AppPaths.ExeRulesFile))
            {
                // 首次生成用户副本：留一份程序原版快照，便于对照与回退
                var snapshot = Path.Combine(dir, "rules.original.json");
                if (!File.Exists(snapshot)) File.Copy(AppPaths.ExeRulesFile, snapshot);
            }

            File.WriteAllText(target, check.Json, new UTF8Encoding(true));

            // 复核：能加载且校验通过才算成功
            var verify = RuleLibraryLoader.Load(target);
            var problems = RuleLibraryLoader.Validate(verify);
            if (problems.Count > 0)
                throw new InvalidDataException("写入后复核失败：" + string.Join("；", problems));

            check.Message = "更新已应用" + (backup != null ? $"（旧版已备份：{Path.GetFileName(backup)}）" : "");
            AppLog.Info($"规则更新已应用：{check.Source} → {target}；{check.DiffSummary}");
            return check;
        }
        catch (Exception ex)
        {
            // 回滚：如果写过目标文件且存在备份，尽量恢复
            try
            {
                if (backup != null && File.Exists(backup))
                {
                    File.Copy(backup, target, overwrite: true);
                    AppLog.Warn("规则更新失败，已从备份还原：" + backup);
                }
                else if (File.Exists(target) && !File.Exists(AppPaths.UserRulesFile.Replace("rules.json", "rules.original.json")))
                {
                    File.Delete(target);
                }
            }
            catch (Exception rex)
            {
                AppLog.Exception("规则回滚失败", rex);
            }

            check.Success = false;
            check.Message = "应用失败：" + ex.Message + "（已保持/还原原规则）";
            AppLog.Exception("规则更新应用失败", ex);
            return check;
        }
    }

    private static void PruneBackups(string dir)
    {
        try
        {
            var baks = Directory.GetFiles(dir, "rules.json.bak-*")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .Skip(5)
                .ToList();
            foreach (var b in baks) File.Delete(b);
        }
        catch
        {
        }
    }

    private static bool IsHttp(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocal(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.IsLoopback || uri.Host is "localhost" or "127.0.0.1" or "::1";
        }
        catch
        {
            return false;
        }
    }
}
