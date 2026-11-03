using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Velopack;
using WinUIDemo.Services;

namespace WinUIDemo;

public partial class App : Application
{
    private Window? _window;

    /// <summary>当前主窗口（选择文件等需要窗口句柄的地方用）。</summary>
    public static Window? MainWindow { get; private set; }

    public App()
    {
        // Velopack 的安装钩子必须最先处理：安装 / 更新时它会以
        // WinUIDemo.exe --veloapp-install <版本> 之类的参数把程序拉起来，
        // 这里 Run() 把这类参数吃掉、跑完钩子就退出，不会再弹主界面
        // （不处理的话安装过程中会提前出现窗口，装完又被关掉重开）。
        VelopackApp.Build()
            // 卸载前清理自身数据目录 + 残留的开机磁贴备份注册表项
            .OnBeforeUninstallFastCallback(_ => UninstallCleanup.PurgeAll())
            .Run();

        // 兜底：未处理异常写到 %TEMP%\nova-crash.log，方便排查崩溃（正常路径不产生文件）
        UnhandledException += (_, e) =>
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nova-crash.log");
                System.IO.File.AppendAllText(path, $"[{DateTime.Now:O}] {e.Exception}\r\n\r\n");
            }
            catch
            {
                // 记日志失败就别再抛
            }
        };

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 被 runas 拉起的提权实例：只做需要管理员的那一件事，做完就退出，不建窗口
        int exitCode = TryRunElevatedTask();
        if (exitCode >= 0)
        {
            Environment.Exit(exitCode);
            return;
        }

        // 只允许开一个：再点桌面图标时把已有窗口拉到前面，然后本进程退出
        if (!SingleInstance.TryTake(ActivateExistingWindow))
        {
            Environment.Exit(0);
            return;
        }

        try
        {
            _window = new MainWindow();
            MainWindow = _window;
            _window.Activate();
        }
        catch (Exception ex)
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nova-crash.log");
                System.IO.File.AppendAllText(path, $"[{DateTime.Now:O}] OnLaunched: {ex}\r\n\r\n");
            }
            catch
            {
                // 忽略
            }

            throw;
        }    }

    /// <summary>把已在运行的窗口从最小化恢复并拉到前面。</summary>
    private static void ActivateExistingWindow()
    {
        Window? window = MainWindow;

        if (window is null)
        {
            return;
        }

        window.DispatcherQueue.TryEnqueue(() =>
        {
            if (window.AppWindow.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            window.AppWindow.Show();
            window.Activate();
        });
    }

    /// <summary>没有提权任务时返回 -1；有任务时返回进程退出码（0 = 成功）。</summary>
    private static int TryRunElevatedTask()
    {
        string[] commandLine = Environment.GetCommandLineArgs();

        for (int i = 0; i < commandLine.Length - 1; i++)
        {
            if (string.Equals(commandLine[i], RegistryAcl.ElevationSwitch, StringComparison.OrdinalIgnoreCase))
            {
                bool granted = RegistryAcl.Apply(commandLine[i + 1], out string error);

                if (!granted)
                {
                    System.Diagnostics.Debug.WriteLine($"grant acl failed: {error}");
                }

                return granted ? 0 : 1;
            }

            if (string.Equals(commandLine[i], FirewallControl.ElevationSwitch, StringComparison.OrdinalIgnoreCase))
            {
                bool disabled = commandLine[i + 1] == "0";
                bool ok = FirewallControl.Apply(disabled, out string error);

                if (!ok)
                {
                    System.Diagnostics.Debug.WriteLine($"set firewall failed: {error}");
                }

                return ok ? 0 : 1;
            }

            if (string.Equals(commandLine[i], DefenderPolicy.ElevationSwitch, StringComparison.OrdinalIgnoreCase))
            {
                bool disabled = commandLine[i + 1] == "1";
                bool ok = DefenderPolicy.Apply(disabled, out string error);

                if (!ok)
                {
                    System.Diagnostics.Debug.WriteLine($"set defender policy failed: {error}");
                }

                return ok ? 0 : 1;
            }

            if (string.Equals(commandLine[i], DefenderRegistryTweak.ElevationSwitch, StringComparison.OrdinalIgnoreCase))
            {
                bool enabled = commandLine[i + 1] == "1";
                bool ok = DefenderRegistryTweak.Apply(enabled, out string error);

                if (!ok)
                {
                    System.Diagnostics.Debug.WriteLine($"set defender registry tweak failed: {error}");
                }

                return ok ? 0 : 1;
            }

            if (string.Equals(commandLine[i], UacNotify.ElevationSwitch, StringComparison.OrdinalIgnoreCase))            {
                bool enabled = commandLine[i + 1] == "1";
                bool ok = UacNotify.Apply(enabled, out string error);

                if (!ok)
                {
                    System.Diagnostics.Debug.WriteLine($"set uac notify failed: {error}");
                }

                return ok ? 0 : 1;
            }
        }

        return -1;
    }
}
