using System.ComponentModel;
using System.Diagnostics;

namespace NovaSetup;

/// <summary>
/// Artk Server 专用：调用安装目录里的 install-service.ps1，把开机自启计划任务和防火墙规则一起配上。
/// 脚本需要管理员权限，这里用 runas 拉起，用户拒绝授权就只提示、不影响已经装好的文件。
/// </summary>
internal static class ServiceInstaller
{
    private const string TaskName = "ArtkServer";

    /// <summary>
    /// 安装 / 更新前停掉后台服务：计划任务以 SYSTEM 身份运行，普通权限杀不掉，
    /// 文件被占用会让 Velopack 的 Setup 以退出码 1 失败。
    /// </summary>
    public static void StopIfRunning(Action<string>? log)
    {
        try
        {
            Process[] running = Process.GetProcessesByName("Artk Server");
            if (running.Length == 0)
            {
                return;
            }

            // 同用户下运行的就直接结束，不需要提权
            foreach (Process p in running)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // SYSTEM 进程 / 其它用户的进程杀不掉，走下面的提权路径
                }
            }

            Thread.Sleep(500);
            if (Process.GetProcessesByName("Artk Server").Length == 0)
            {
                return;
            }

            log?.Invoke("正在停止后台服务（需要管理员授权）…");
            try
            {
                RunElevated("schtasks.exe", $"/End /TN {TaskName}");
                RunElevated("taskkill.exe", "/F /IM \"Artk Server.exe\"");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // 用户拒绝授权：继续安装，失败时会给出提示
            }

            Thread.Sleep(1200);
        }
        catch
        {
            // 停不掉就算了，交给安装流程自己报错
        }
    }

    private static void RunElevated(string file, string arguments)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using Process? proc = Process.Start(psi);
        proc?.WaitForExit(60_000);
    }

    public static void Register(string installDir, Action<string>? log)
    {
        string script = Path.Combine(installDir, "current", "install-service.ps1");
        if (!File.Exists(script))
        {
            log?.Invoke("没找到 install-service.ps1，已跳过服务注册（之后可以手动以管理员身份运行它）。");
            return;
        }

        log?.Invoke("正在注册后台服务（会弹出管理员授权）…");

        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
            };

            using Process? proc = Process.Start(psi);
            if (proc is null)
            {
                log?.Invoke("无法启动 PowerShell，服务未注册。");
                return;
            }

            if (!proc.WaitForExit(180_000))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 已经退出就忽略
                }

                log?.Invoke("服务注册超时，已跳过（之后可以手动以管理员身份运行 install-service.ps1）。");
                return;
            }

            log?.Invoke(proc.ExitCode == 0
                ? "后台服务已注册，开机自动启动。"
                : $"服务注册未完成（退出码 {proc.ExitCode}），之后可以手动以管理员身份运行 install-service.ps1。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            log?.Invoke("已取消管理员授权，服务未注册（之后可在安装目录右键以管理员运行 install-service.ps1）。");
        }
        catch (Exception ex)
        {
            log?.Invoke("服务注册失败：" + ex.Message + "（之后可以手动以管理员身份运行 install-service.ps1）");
        }
    }
}
