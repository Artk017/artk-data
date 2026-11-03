namespace NovaSetup;

/// <summary>安装选项，可从命令行解析（供静默安装 / 测试）。</summary>
internal sealed class InstallOptions
{
    public string InstallDir { get; set; } = DefaultInstallDir();

    /// <summary>命令行是否显式给了 --installto（没给时优先沿用已安装目录）。</summary>
    public bool InstallDirProvided { get; set; }

    public bool CreateDesktopShortcut { get; set; } = true;

    public bool LaunchAfterInstall { get; set; } = true;

    /// <summary>安装完成后是否注册开机自启后台服务（只有 Artk Server 支持）。</summary>
    public bool RegisterService { get; set; } = ProductInfo.Current.SupportsService;

    /// <summary>命令行带了 --silent（无界面）。</summary>
    public bool Silent { get; set; }

    /// <summary>是否显示界面（--silent 时默认为 false；--ui 可强制显示）。</summary>
    public bool ShowUi { get; set; } = true;

    public static string DefaultInstallDir()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, ProductInfo.Current.InstallFolderName);
    }

    /// <summary>
    /// 用户选的是父目录时自动补上产品自己的子目录，避免和父目录里别的东西混在一起。
    /// 例：选 G:\dow → G:\dow\Artk Server；已经就是产品目录（或已有安装）则原样返回。
    /// </summary>
    public static string NormalizeProductDir(string raw)
    {
        string text = (raw ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return DefaultInstallDir();
        }

        string dir;

        try
        {
            dir = Path.GetFullPath(text).TrimEnd('\\', '/');
        }
        catch
        {
            return text.TrimEnd('\\', '/');
        }

        if (dir.EndsWith(":", StringComparison.Ordinal))
        {
            dir += "\\";
        }

        string folder = ProductInfo.Current.InstallFolderName;
        string leaf = Path.GetFileName(dir);

        if (leaf.Equals(folder, StringComparison.OrdinalIgnoreCase))
        {
            return dir;
        }

        // 已经装过的目录（Velopack 会在根目录放 Update.exe）保持不动，更新时不能把程序挪走
        if (File.Exists(Path.Combine(dir, "Update.exe")))
        {
            return dir;
        }

        return Path.Combine(dir, folder);
    }

    public static InstallOptions Parse(string[] args)
    {
        var o = new InstallOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--silent":
                case "-s":
                    o.Silent = true;
                    o.ShowUi = false;
                    // 静默默认不自动打开，避免自动化测试时弹窗；需要时用 --launch 显式开启
                    o.LaunchAfterInstall = false;
                    break;

                case "--ui":
                    o.ShowUi = true;
                    break;

                case "--installto":
                case "-t":
                    if (i + 1 < args.Length)
                    {
                        o.InstallDir = args[++i];
                        o.InstallDirProvided = true;
                    }
                    break;

                case "--desktop":
                    o.CreateDesktopShortcut = true;
                    break;

                case "--no-desktop":
                    o.CreateDesktopShortcut = false;
                    break;

                case "--launch":
                    o.LaunchAfterInstall = true;
                    break;

                case "--no-launch":
                    o.LaunchAfterInstall = false;
                    break;

                case "--service":
                    o.RegisterService = true;
                    break;

                case "--no-service":
                    o.RegisterService = false;
                    break;
            }
        }

        return o;
    }
}
