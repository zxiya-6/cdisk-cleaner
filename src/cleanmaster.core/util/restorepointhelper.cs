using System.Diagnostics;
using System.Text;

namespace CleanMaster.Core.Util;

/// <summary>系统还原点辅助（尽力而为，失败不影响清理）。</summary>
public static class RestorePointHelper
{
    public static string Create(string description)
    {
        var desc = (description ?? "").Replace("'", "").Replace("\"", "");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add($"Checkpoint-Computer -Description '{desc}' -RestorePointType 'MODIFY_SETTINGS'");

            // PowerShell 控制台输出为系统 OEM 编码（简体中文系统为 GBK/936），需显式按该编码读取，
            // 否则按 UTF-8 误读会出现乱码（.NET 7 需注册 CodePagesEncodingProvider）。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            psi.StandardOutputEncoding = Encoding.GetEncoding(936);
            psi.StandardErrorEncoding = Encoding.GetEncoding(936);

            using var p = Process.Start(psi);
            if (p == null) return "未创建（无法启动 PowerShell）";

            var soTask = p.StandardOutput.ReadToEndAsync();
            var seTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromSeconds(240)))
            {
                try { p.Kill(true); } catch { }
                return "未创建（超时，已跳过）";
            }

            var so = soTask.GetAwaiter().GetResult();
            var se = seTask.GetAwaiter().GetResult();

            if (p.ExitCode == 0) return "已创建系统还原点";

            var msg = (se + " " + so).Trim();
            if (msg.Contains("频率") || msg.Contains("frequency") || msg.Contains("1440") || msg.Contains("should not be created"))
                return "系统限制了还原点创建频率（24 小时内已创建过），已跳过";
            if (msg.Contains("System Restore is disabled") || msg.Contains("系统还原已禁用") || msg.Contains("已被禁用"))
                return "系统还原功能未启用，已跳过";
            return "未创建（" + PathUtil.Truncate(msg, 160) + "）";
        }
        catch (Exception ex)
        {
            AppLog.Exception("创建还原点异常", ex);
            return "未创建（" + ex.Message + "）";
        }
    }
}
