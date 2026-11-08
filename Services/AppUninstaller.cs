using System;
using System.Diagnostics;
using System.IO;
using Velopack.Locators;

namespace ArtkTools.Services;

/// <summary>
/// 卸载自身：Inno Setup 安装的版本调用 unins000.exe（卸载器自己会问是否删除用户数据）；
/// 老的 Velopack 安装版本调用 Update.exe --uninstall。
/// 便携版 / 开发直跑则不可自动卸载。
/// </summary>
public static class AppUninstaller
{
    /// <summary>上次卸载是否由安装器接管了用户数据清理（Inno 的卸载器会弹窗询问）。</summary>
    public static bool DataCleanupHandledByUninstaller { get; private set; }

    /// <summary>Inno Setup 的卸载器（unins000.exe 等）；不是 Inno 安装时为 null。</summary>
    private static string? FindInnoUninstaller()
    {
        try
        {
            string dir = AppContext.BaseDirectory;

            if (!Directory.Exists(dir))
            {
                return null;
            }

            return Directory.GetFiles(dir, "unins*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>是否具备自动卸载条件；不具备时用 <paramref name="reason"/> 说明原因。</summary>
    public static bool CanUninstall(out string reason)
    {
        reason = string.Empty;

        if (FindInnoUninstaller() is not null)
        {
            return true;
        }

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
    /// 拉起卸载器开始卸载（Inno 的 unins000.exe / 老版本的 Update.exe --uninstall）。
    /// 成功后调用方应立即退出本进程，让卸载器在文件解锁后再删除安装目录并清理快捷方式。
    /// </summary>
    public static bool TryUninstall(out string error)
    {
        error = string.Empty;
        DataCleanupHandledByUninstaller = false;

        // Inno Setup 安装：卸载器自己会弹窗询问是否删除用户数据
        if (FindInnoUninstaller() is { } inno)
        {
            try
            {
                Process.Start(new ProcessStartInfo(inno)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(inno),
                });

                DataCleanupHandledByUninstaller = true;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

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
