using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NovaServer;

/// <summary>
/// 应用内卸载：起一个提权的 PowerShell 脚本，等界面退出后清干净
/// 后台服务（计划任务 + 进程）、防火墙规则、server.json / ui.json / data，最后交给 Velopack 卸载本体。
/// </summary>
internal static class ServerUninstaller
{
    /// <summary>启动卸载脚本；用户在 UAC 弹窗点了取消时返回 false。</summary>
    public static bool Launch(string homeDir)
    {
        string script = Path.Combine(Path.GetTempPath(), $"artk-server-uninstall-{Environment.ProcessId}.ps1");
        File.WriteAllText(script, BuildScript(homeDir, Environment.ProcessId), new UTF8Encoding(true));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -File \"{script}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 里点了“否”
            return false;
        }
    }

    private static string BuildScript(string homeDir, int processId)
    {
        string home = homeDir.TrimEnd('\\', '/');

        static string Quote(string value) => value.Replace("'", "''");

        return $$"""
            $ErrorActionPreference = 'SilentlyContinue'

            # 等界面进程退出，不然 exe / 安装目录删不掉
            Wait-Process -Id {{processId}} -Timeout 120

            # 后台服务：计划任务、常驻进程
            schtasks /End /TN 'ArtkServer' | Out-Null
            schtasks /Delete /TN 'ArtkServer' /F | Out-Null
            Remove-NetFirewallRule -DisplayName 'Artk Server 清单服务'
            Get-Process -Name 'Artk Server' | Stop-Process -Force

            Start-Sleep -Milliseconds 800

            # 配置与数据先删，避免卸载器把它们当用户数据保留
            Remove-Item -LiteralPath '{{Quote(home)}}\server.json' -Force
            Remove-Item -LiteralPath '{{Quote(home)}}\ui.json' -Force
            Remove-Item -LiteralPath '{{Quote(home)}}\data' -Recurse -Force

            $update = '{{Quote(home)}}\Update.exe'

            if (Test-Path -LiteralPath $update) {
                # Velopack 安装版：由它自己注销卸载项、删快捷方式和程序目录
                Start-Process -FilePath $update -ArgumentList '--uninstall','--silent' -Wait
            }
            else {
                # 绿色版：直接删整个目录
                Remove-Item -LiteralPath '{{Quote(home)}}' -Recurse -Force
            }

            # Velopack 自己的日志
            Remove-Item -LiteralPath "$env:LOCALAPPDATA\velopack\velopack_ArtkServer.log" -Force

            Remove-Item -LiteralPath $PSCommandPath -Force
            """;
    }
}
