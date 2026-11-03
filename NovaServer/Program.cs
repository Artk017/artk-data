using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NovaServer;
using Velopack;

/// <summary>
/// 入口：
///   不带参数      → WinUI 设置界面（服务没在跑的话顺手在本进程里跑起来）
///   --headless    → 无窗口后台服务（计划任务 / 服务器开机自启）
///   --init / --publish-now / --refresh-data → 原来的一次性命令行任务
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Velopack 的安装 / 更新 / 卸载钩子必须在最开始处理（安装包会带参数启动本程序）
        VelopackApp.Build().Run();

        bool headless = args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase));
        bool oneShot = args.Any(a => a.Equals("--init", StringComparison.OrdinalIgnoreCase)
                                    || a.Equals("--publish-now", StringComparison.OrdinalIgnoreCase));

        if (headless || oneShot)
        {
            return RunConsole(args);
        }

        // 界面只允许开一个：再点桌面图标时把已有窗口（可能藏在托盘里）叫出来
        if (!SingleInstance.TryTake(() => MainWindow.Active?.BringToFront()))
        {
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });

        return 0;
    }

    /// <summary>控制台 / 后台模式。WinExe 没有自带控制台，能从 PowerShell 拉起来就附加上去。</summary>
    private static int RunConsole(string[] args)
    {
        AttachConsoleToParent();

        ServerHost host = ServerHost.Load();
        bool refresh = host.Config.RefreshDataOnStart
                       || args.Any(a => a.Equals("--refresh-data", StringComparison.OrdinalIgnoreCase));

        host.PrepareAsync(refresh).GetAwaiter().GetResult();

        if (args.Any(a => a.Equals("--init", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"配置: {host.ConfigPath}");
            Console.WriteLine($"密钥: {host.Config.ApiKey}");
            Console.WriteLine($"端口: {host.Port}");
            return 0;
        }

        if (args.Any(a => a.Equals("--publish-now", StringComparison.OrdinalIgnoreCase)))
        {
            (bool published, string message) = host.Publisher.PublishNowAsync(force: true).GetAwaiter().GetResult();
            Console.WriteLine(message);
            return published ? 0 : 1;
        }

        host.BuildApp();
        host.Publisher.Start();
        host.PrintBanner();
        host.BuildApp().Run();

        return 0;
    }

    private static void AttachConsoleToParent()
    {
        try
        {
            if (!AttachConsole(-1))
            {
                // 没有父控制台（计划任务 / 双击）就算了，日志静默丢弃
                return;
            }

            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 某些宿主不给改编码，忽略
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
}
