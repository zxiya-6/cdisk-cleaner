using Avalonia;

namespace CleanMaster.App;

internal static class Program
{
    // Avalonia 初始化（无自定义模板时用 BuildAvaloniaApp）。
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
