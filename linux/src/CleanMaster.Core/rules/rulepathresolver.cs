using CleanMaster.Core.Models;
using CleanMaster.Core.Util;

namespace CleanMaster.Core.Rules;

/// <summary>把规则中的路径模板（含 $ENV、~ 与通配段）解析为具体存在的路径集合（POSIX）。</summary>
public static class RulePathResolver
{
    /// <summary>解析结果：DirContents → 目录集合；Files → 文件集合；EmptyRecycleBin → 空集合。</summary>
    public sealed class ResolveResult
    {
        public List<string> Paths { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    public static ResolveResult Resolve(CategoryRule rule)
    {
        var result = new ResolveResult();
        if (rule.Kind == RuleKind.EmptyRecycleBin) return result;

        var dedupe = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in rule.Paths)
        {
            var expanded = PathUtil.ExpandEnv(raw).Trim();
            if (expanded.Length == 0) continue;

            if (!Path.IsPathRooted(expanded))
                expanded = Path.GetFullPath(expanded);

            var segments = SplitSegments(expanded);
            if (segments.Count == 0)
            {
                result.Warnings.Add($"无法解析路径：{raw}");
                continue;
            }

            var current = new List<string> { segments[0] };
            for (var i = 1; i < segments.Count; i++)
            {
                var seg = segments[i];
                var isLast = i == segments.Count - 1;
                var next = new List<string>();

                foreach (var b in current)
                {
                    if (PathUtil.HasWildcard(seg))
                    {
                        try
                        {
                            if (isLast && rule.Kind == RuleKind.Files)
                            {
                                foreach (var f in Directory.EnumerateFiles(b, seg)) next.Add(f);
                            }
                            else
                            {
                                foreach (var d in Directory.EnumerateDirectories(b, seg)) next.Add(d);
                            }
                        }
                        catch (Exception ex)
                        {
                            if (result.Warnings.Count < 50)
                                result.Warnings.Add($"枚举 {PathUtil.Truncate(b)}/{seg} 失败：{ex.Message}");
                        }
                    }
                    else
                    {
                        var p = Path.Combine(b, seg);
                        if (isLast)
                        {
                            if (rule.Kind == RuleKind.Files)
                            {
                                if (File.Exists(p)) next.Add(p);
                            }
                            else if (Directory.Exists(p))
                            {
                                next.Add(p);
                            }
                        }
                        else if (Directory.Exists(p))
                        {
                            next.Add(p);
                        }
                    }
                }

                current = next;
                if (current.Count == 0) break;
            }

            foreach (var p in current)
            {
                var n = PathUtil.Normalize(p);
                if (n.Length > 0) dedupe.Add(n);
            }
        }

        result.Paths.AddRange(dedupe.OrderBy(p => p, StringComparer.Ordinal));
        return result;
    }

    private static List<string> SplitSegments(string fullPath)
    {
        var segs = new List<string> { "/" };
        var rest = fullPath.TrimStart('/');
        foreach (var s in rest.Split('/', StringSplitOptions.RemoveEmptyEntries))
            segs.Add(s);
        return segs;
    }
}
