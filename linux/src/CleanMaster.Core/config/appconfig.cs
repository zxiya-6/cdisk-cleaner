using CleanMaster.Core.Util;

namespace CleanMaster.Core.Config;

/// <summary>应用配置（Linux 版）。</summary>
public sealed class AppConfig
{
    /// <summary>备份区根目录（默认 ~/.local/share/cleanmaster/backup）。</summary>
    public string BackupRoot { get; set; } = "";

    /// <summary>备份保留天数（0 = 一直保留，直到用户手动清空）。</summary>
    public int BackupRetentionDays { get; set; } = 0;

    /// <summary>默认清理模式：standard / allToBackup / allPermanent。</summary>
    public string DefaultCleanMode { get; set; } = "standard";

    /// <summary>清理前默认创建系统还原点（Linux 不支持，保留字段）。</summary>
    public bool CreateRestorePointByDefault { get; set; } = false;

    /// <summary>每个类别最多保留多少个预览条目（按大小排序）。</summary>
    public int TopItemsPerCategory { get; set; } = 2000;

    /// <summary>清理时是否自动清理长期未访问的备份（预留）。</summary>
    public bool AutoPurgeOldBackups { get; set; } = false;

    /// <summary>规则在线更新地址（http/https URL 或本地文件路径；留空 = 未配置）。</summary>
    public string RuleUpdateUrl { get; set; } = "";
}

/// <summary>运行时路径（数据目录、日志、备份区、规则库定位；遵循 XDG 约定）。</summary>
public static class AppPaths
{
    public static string ExeDir => AppContext.BaseDirectory;

    /// <summary>便携模式：可执行文件旁存在 portable.flag 时，数据放到程序目录下。</summary>
    public static bool Portable => File.Exists(Path.Combine(ExeDir, "portable.flag"));

    public static string DataRoot => Environment.GetEnvironmentVariable("CC5_DATA_DIR") is { Length: > 0 } over
        ? over
        : Portable
            ? Path.Combine(ExeDir, "data")
            : Path.Combine(XdgDataHome, "cleanmaster");

    /// <summary>XDG_DATA_HOME（默认 ~/.local/share）。</summary>
    public static string XdgDataHome
    {
        get
        {
            var x = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrEmpty(x)) return x;
            return Path.Combine(PathUtil.HomeDir, ".local", "share");
        }
    }

    /// <summary>XDG_CACHE_HOME（默认 ~/.cache）。</summary>
    public static string XdgCacheHome
    {
        get
        {
            var x = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (!string.IsNullOrEmpty(x)) return x;
            return Path.Combine(PathUtil.HomeDir, ".cache");
        }
    }

    /// <summary>XDG_CONFIG_HOME（默认 ~/.config）。</summary>
    public static string XdgConfigHome
    {
        get
        {
            var x = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrEmpty(x)) return x;
            return Path.Combine(PathUtil.HomeDir, ".config");
        }
    }

    public static string ConfigFile => Path.Combine(DataRoot, "config.json");
    public static string LedgerDir => Path.Combine(DataRoot, "ledger");
    public static string LedgerFile => Path.Combine(LedgerDir, "ledger.jsonl");
    public static string LogsDir => Path.Combine(DataRoot, "logs");
    public static string DefaultBackupRoot => Path.Combine(DataRoot, "backup");

    /// <summary>程序自带规则目录。</summary>
    public static string ExeRulesDir => Path.Combine(ExeDir, "rules");

    /// <summary>用户规则目录（在线更新/导入的规则存放处，优先于程序自带规则）。</summary>
    public static string UserRulesDir => Path.Combine(DataRoot, "rules");

    public static string ExeRulesFile => Path.Combine(ExeRulesDir, "rules.json");
    public static string UserRulesFile => Path.Combine(UserRulesDir, "rules.json");

    /// <summary>生效的规则文件：存在用户规则副本时优先使用。</summary>
    public static string RulesFile => File.Exists(UserRulesFile) ? UserRulesFile : ExeRulesFile;

    /// <summary>生效规则所在目录。</summary>
    public static string EffectiveRulesDir => File.Exists(UserRulesFile) ? UserRulesDir : ExeRulesDir;

    /// <summary>生效的规则目录（兼容别名）。</summary>
    public static string RulesDir => EffectiveRulesDir;

    public static string ProtectedFile
    {
        get
        {
            var user = Path.Combine(UserRulesDir, "protected.json");
            return File.Exists(user) ? user : Path.Combine(ExeRulesDir, "protected.json");
        }
    }
}

/// <summary>配置读写。</summary>
public static class ConfigStore
{
    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var cfg = Json.FromJson<AppConfig>(File.ReadAllText(AppPaths.ConfigFile), pretty: true);
                if (cfg != null)
                {
                    ApplyDefaults(cfg);
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("读取配置失败，使用默认配置", ex);
        }

        var fresh = new AppConfig();
        ApplyDefaults(fresh);
        return fresh;
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            File.WriteAllText(AppPaths.ConfigFile, Json.ToPretty(cfg), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppLog.Exception("保存配置失败", ex);
        }
    }

    public static void ApplyDefaults(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.BackupRoot)) cfg.BackupRoot = AppPaths.DefaultBackupRoot;
        if (cfg.TopItemsPerCategory <= 0) cfg.TopItemsPerCategory = 2000;
    }

    /// <summary>应用启动时的统一初始化（日志、目录）。</summary>
    public static AppConfig InitRuntime()
    {
        var cfg = Load();
        try { Directory.CreateDirectory(AppPaths.DataRoot); } catch { }
        AppLog.Init(AppPaths.LogsDir);
        AppLog.Info($"启动 {BuildInfo.FullName}；数据目录 {AppPaths.DataRoot}；便携模式={AppPaths.Portable}");
        return cfg;
    }
}
