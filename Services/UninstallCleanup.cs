using System;
using System.IO;
using Microsoft.Win32;

namespace ArtkTools.Services;

/// <summary>
/// 卸载前的清理：删除 Nova 自身数据目录（%LOCALAPPDATA%\Nova）和 Windows 开始菜单
/// 磁贴备份注册表里残留的 NovaWorkbench 项。
/// 通过 Velopack 的 OnBeforeUninstallFastCallback 调用，所以无论从「应用和功能」
/// 还是应用内卸载，都会走到。
/// </summary>
public static class UninstallCleanup
{
    public static void PurgeAll()
    {
        try
        {
            RemoveTileBackups();
        }
        catch
        {
            // 注册表清理失败不影响卸载
        }

        try
        {
            PurgeDataFolder();
        }
        catch
        {
            // 数据目录清理失败不影响卸载
        }
    }

    private static void PurgeDataFolder()
    {
        string folder = AppSettings.DataFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch
        {
            // 有文件占用就退回延时强删（进程退出后再删）
            try
            {
                AppUninstaller.ScheduleDataCleanup();
            }
            catch
            {
                // 忽略
            }
        }
    }

    private static void RemoveTileBackups()
    {
        const string root = @"Software\Microsoft\Windows\CurrentVersion\AppListBackup";

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(root, writable: true);
        if (key is null)
        {
            return;
        }

        foreach (string name in key.GetValueNames())
        {
            string data = key.GetValue(name)?.ToString() ?? string.Empty;
            if (name.IndexOf("NovaWorkbench", StringComparison.OrdinalIgnoreCase) >= 0 ||
                data.IndexOf("NovaWorkbench", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                }
                catch
                {
                    // 忽略
                }
            }
        }
    }
}
