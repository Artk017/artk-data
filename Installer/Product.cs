namespace NovaSetup;

/// <summary>安装向导要安装的产品信息。Artk Tools 和 Artk Server 共用同一套向导代码，只换这里的值。</summary>
internal sealed class Product
{
    /// <summary>界面上显示的名字（标题、提示、快捷方式描述）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>内嵌资源名（Velopack 生成的 Setup.exe）。</summary>
    public required string SetupResourceName { get; init; }

    /// <summary>释放到临时目录时用的文件名。</summary>
    public required string InnerSetupFileName { get; init; }

    /// <summary>安装目录 current\ 下的主程序文件名。</summary>
    public required string MainExeFileName { get; init; }

    /// <summary>
    /// 产品自己的目录名：默认安装位置是 %LocalAppData%\&lt;该目录&gt;；
    /// 用户选的安装位置不是这个目录时，向导会自动在后面补上（比如选 G:\dow → G:\dow\Artk Server）。
    /// </summary>
    public required string InstallFolderName { get; init; }

    /// <summary>桌面快捷方式文件名。</summary>
    public required string ShortcutName { get; init; }

    /// <summary>Velopack 的 packId，也是卸载项在注册表里的键名。</summary>
    public required string PackId { get; init; }

    /// <summary>覆盖安装前要结束的进程名（用 exe 文件名比较）。</summary>
    public required string[] RunningExeNames { get; init; }

    /// <summary>是否有「注册开机自启后台服务」这一步（Artk Server 才有）。</summary>
    public bool SupportsService { get; init; }
}
