namespace NovaSetup;

/// <summary>Artk Server 安装向导的产品定义（服务端安装包用这一份）。</summary>
internal static class ProductInfo
{
    public static readonly Product Current = new()
    {
        DisplayName = "Artk Server",
        SetupResourceName = "ArtkServerInternalSetup.exe",
        InnerSetupFileName = "ArtkServer-win-Setup.exe",
        MainExeFileName = "Artk Server.exe",
        InstallFolderName = "Artk Server",
        ShortcutName = "Artk Server.lnk",
        PackId = "ArtkServer",
        RunningExeNames = new[] { "Artk Server.exe", "Update.exe" },
        SupportsService = true,
    };
}
