namespace NovaSetup;

/// <summary>Artk Tools 安装向导的产品定义（客户端安装包用这一份）。</summary>
internal static class ProductInfo
{
    public static readonly Product Current = new()
    {
        DisplayName = "Artk Tools",
        SetupResourceName = "ArtkToolsInternalSetup.exe",
        InnerSetupFileName = "ArtkTools-win-Setup.exe",
        MainExeFileName = "ArtkTools.exe",
        InstallFolderName = "Artk Tools",
        ShortcutName = "Artk Tools.lnk",
        PackId = "NovaWorkbench",
        RunningExeNames = new[] { "ArtkTools.exe", "Nova.exe", "Update.exe" },
        SupportsService = false,
    };
}
