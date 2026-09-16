using System.Text;

namespace CleanMaster.Core.Util;

/// <summary>轻量文件日志（线程安全，按天分文件）。</summary>
public static class AppLog
{
    private static readonly object Sync = new();
    private static string? _file;

    public static bool Enabled { get; set; } = true;

    /// <summary>新增日志行事件（供界面订阅；异常不影响主流程）。</summary>
    public static event Action<string>? Line;

    public static void Init(string logsDir)
    {
        try
        {
            Directory.CreateDirectory(logsDir);
            _file = Path.Combine(logsDir, $"app-{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            _file = null;
        }
    }

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    public static void Exception(string context, Exception ex) =>
        Write("ERROR", $"{context} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string msg)
    {
        if (!Enabled || _file == null) return;
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}{Environment.NewLine}";
            lock (Sync)
            {
                File.AppendAllText(_file, line, Encoding.UTF8);
            }

            try { Line?.Invoke($"{level.Trim()} {msg}"); } catch { }
        }
        catch
        {
            // 日志失败绝不影响主流程
        }
    }
}
