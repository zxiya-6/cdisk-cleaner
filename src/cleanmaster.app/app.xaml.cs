using System.Windows;
using CleanMaster.Core.Util;

namespace CleanMaster.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Exception("UI 未处理异常", args.Exception);
            System.Windows.MessageBox.Show("发生了一个未预期的错误，已记录到日志文件。\n\n" + args.Exception.Message,
                "C盘清理助手", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLog.Exception("后台未处理异常", ex);
        };
    }
}
