namespace CleanMaster.Core;

/// <summary>产品与版本常量。</summary>
public static class BuildInfo
{
    public const string ProductName = "C盘清理助手";
    public const string ProductNameEn = "CleanMaster";
    public const string Version = "5.0.0";
    public const string Stage = "release";

    public static string FullName => $"{ProductName} v{Version}";
}
