namespace ArtkTools.Services.ComputerTools;

/// <summary>电脑工具的下载 / 缓存目录。</summary>
internal static class ComputerToolPaths
{
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Artk Tools",
        "ComputerTools");
}
