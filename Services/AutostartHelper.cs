using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace WinUIDemo.Services;

/// <summary>
/// 清理某个已安装软件的开机自启动项：HKCU/HKLM 的 Run / RunOnce 值，
/// 以及用户 / 公共「启动」文件夹里指向该软件的快捷方式。
/// 只匹配指向该程序安装目录或主程序 exe 的项，不动其它软件。
/// </summary>
public static class AutostartHelper
{
    private static readonly string[] KeyPaths =
    {
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
        @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce",
    };

    public static void RemoveFor(string? exePath, string? installDir)
    {
        var needles = new List<string>();

        if (!string.IsNullOrWhiteSpace(exePath))
        {
            needles.Add(exePath!.Trim());
            string? dir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                needles.Add(dir!);
            }
        }

        if (!string.IsNullOrWhiteSpace(installDir))
        {
            needles.Add(installDir!.TrimEnd('\\', '/'));
        }

        if (needles.Count == 0)
        {
            return;
        }

        RemoveRunValues(Registry.CurrentUser, needles);
        RemoveRunValues(Registry.LocalMachine, needles);
        RemoveStartupShortcuts(needles);
    }

    private static bool Matches(string? value, List<string> needles)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (string needle in needles)
        {
            if (value!.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveRunValues(RegistryKey hive, List<string> needles)
    {
        foreach (string path in KeyPaths)
        {
            try
            {
                using RegistryKey? key = hive.OpenSubKey(path, writable: true);
                if (key is null)
                {
                    continue;
                }

                foreach (string name in key.GetValueNames())
                {
                    string data = key.GetValue(name)?.ToString() ?? string.Empty;
                    if (Matches(data, needles))
                    {
                        try
                        {
                            key.DeleteValue(name, throwOnMissingValue: false);
                        }
                        catch
                        {
                            // 没权限就跳过（HKLM 通常需要管理员）
                        }
                    }
                }
            }
            catch
            {
                // 访问被拒等忽略
            }
        }
    }

    private static void RemoveStartupShortcuts(List<string> needles)
    {
        foreach (Environment.SpecialFolder folder in new[]
                 {
                     Environment.SpecialFolder.Startup,
                     Environment.SpecialFolder.CommonStartup,
                 })
        {
            string dir;
            try
            {
                dir = Environment.GetFolderPath(folder);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            foreach (string lnk in Directory.EnumerateFiles(dir, "*.lnk"))
            {
                if (Matches(TryGetTarget(lnk), needles))
                {
                    try
                    {
                        File.Delete(lnk);
                    }
                    catch
                    {
                        // 忽略
                    }
                }
            }
        }
    }

    private static string? TryGetTarget(string lnkPath)
    {
        try
        {
            Type? type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            return (string)shortcut.TargetPath;
        }
        catch
        {
            return null;
        }
    }
}
