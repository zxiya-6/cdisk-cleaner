using CleanMaster.Core.Config;
using CleanMaster.Core.Models;
using CleanMaster.Core.Util;

namespace CleanMaster.Core.Rules;

/// <summary>protected.json 的自定义保护配置。</summary>
public sealed class ProtectedConfig
{
    /// <summary>自定义保护路径（目录树，位于其下的内容永不清理）。</summary>
    public List<string> CustomDeny { get; set; } = new();

    /// <summary>说明文本（供维护者阅读，程序不解析）。</summary>
    public string? Note { get; set; }
}

/// <summary>规则库加载与校验。</summary>
public static class RuleLibraryLoader
{
    public static RuleLibrary Load(string? rulesFile = null)
    {
        var file = rulesFile ?? AppPaths.RulesFile;
        if (!File.Exists(file))
            throw new FileNotFoundException($"未找到规则库文件：{file}。请确认程序目录下存在 rules\\rules.json。");

        var text = File.ReadAllText(file, System.Text.Encoding.UTF8);
        var lib = Json.FromJson<RuleLibrary>(text, pretty: true);
        if (lib == null) throw new InvalidDataException($"规则库解析失败：{file}");
        return lib;
    }

    public static void Save(RuleLibrary lib, string file)
    {
        File.WriteAllText(file, Json.ToPretty(lib), System.Text.Encoding.UTF8);
    }

    /// <summary>返回校验问题列表（空 = 通过）。</summary>
    public static List<string> Validate(RuleLibrary lib)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in lib.Categories)
        {
            if (string.IsNullOrWhiteSpace(c.Id)) { problems.Add("存在缺少 id 的类别"); continue; }
            if (!seen.Add(c.Id)) problems.Add($"类别 id 重复：{c.Id}");
            if (string.IsNullOrWhiteSpace(c.Name)) problems.Add($"类别 {c.Id} 缺少 name");
            if (c.Kind != RuleKind.EmptyRecycleBin && (c.Paths == null || c.Paths.Count == 0))
                problems.Add($"类别 {c.Id} 未配置路径");
        }
        return problems;
    }

    /// <summary>加载与之配套的安全过滤器（合并 protected.json 自定义项）。</summary>
    public static SafetyFilter LoadSafetyFilter()
    {
        var f = SafetyFilter.CreateDefault();
        try
        {
            var file = AppPaths.ProtectedFile;
            if (File.Exists(file))
            {
                var cfg = Json.FromJson<ProtectedConfig>(File.ReadAllText(file, System.Text.Encoding.UTF8), pretty: true);
                if (cfg?.CustomDeny is { Count: > 0 }) f.AddCustomDeny(cfg.CustomDeny);
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("读取 protected.json 失败（忽略自定义保护，内置保护仍生效）", ex);
        }

        return f;
    }
}
