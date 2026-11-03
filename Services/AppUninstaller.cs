using System;
using System.Diagnostics;
using System.IO;
using Velopack.Locators;

namespace WinUIDemo.Services;

/// <summary>
/// 卸载自身：调用 Velopack 生成的 Update.exe --uninstall。
/// 只有通过安装包（Setup）安装的版本才有 Update.exe；便携版 / 开发直跑则不可自动卸载。
/// </summary>
public static class AppUninstaller
{
    /// <summary>是否具备自动卸载条件；不具备时用 <paramref name="reason"/> 说明原因。</summary>
    public static bool CanUninstall(out string reason)
    {
        reason = string.Empty;

        try
        {
            IVelopackLocator? locator = VelopackLocator.Current;
            if (locator is null)
            {
                reason = "当前不是通过安装包安装的版本，没有可卸载的安装信息。";
                return false;
            }

            if (locator.IsPortable)
            {
                reason = "当前是免安装（便携）版本，直接删除程序文件夹即可完成卸载。";
                return false;
            }

            if (string.IsNullOrEmpty(locator.UpdateExePath) || !File.Exists(locator.UpdateExePath))
            {
                reason = "找不到卸载程序 Update.exe，请在「设置 → 应用 → 已安装的应用」里卸载。";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 拉起 Update.exe --uninstall 开始卸载。成功后调用方应立即退出本进程，
    /// 让 Update.exe 在文件解锁后再删除安装目录并清理快捷方式。
    /// </summary>
    public static bool TryUninstall(out string error)
    {
        error = string.Empty;

        if (!CanUninstall(out error))
        {
            return false;
        }

        try
        {
            string updateExe = VelopackLocator.Current!.UpdateExePath!;

            Process.Start(new ProcessStartInfo(updateExe, "--uninstall")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(updateExe),
            });

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 在 Nova 退出后删除 %LOCALAPPDATA%\Nova（设置 / 清单缓存 / 下载记录）。
    /// 用 cmd 延时执行，等本进程真正退出、文件解锁后再强删。
    /// </summary>
    public static void ScheduleDataCleanup()
    {
        string folder = AppSettings.DataFolder;

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            // -n 5 ≈ 4 秒，足够本进程退出并释放 settings.json 等句柄
            string arguments = $"/c ping 127.0.0.1 -n 5 >nul & rmdir /s /q \"{folder}\"";

            Process.Start(new ProcessStartInfo("cmd.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch
        {
            // 清理失败不影响卸载本身
        }
    }
}
