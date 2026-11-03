using System.Windows.Forms;

namespace NovaSetup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        InstallOptions options = InstallOptions.Parse(args);

        // 已安装过 Artk Tools 且命令行没指定目录：默认更新到原安装位置，避免装出第二份
        if (!options.InstallDirProvided && ExistingInstallDetector.Find() is { } existing)
        {
            options.InstallDir = existing.InstallDir;
        }

        // 调试用：只打印最终会装到哪，不做任何修改（--print-target）
        if (args.Contains("--print-target"))
        {
            Console.WriteLine(InstallOptions.NormalizeProductDir(options.InstallDir));
            return 0;
        }

        // 静默模式：给自动化/测试用，无界面，直接安装并返回退出码
        if (options.Silent && !options.ShowUi)
        {
            int code = SetupEngine.Run(options, null, default);
            if (code == 0)
            {
                SetupEngine.ApplyPostInstall(options);
            }

            return code;
        }

        using var form = new InstallForm(options);
        Application.Run(form);
        return form.ExitCode;
    }
}
