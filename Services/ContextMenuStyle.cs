using System;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace WinUIDemo.Services;

/// <summary>
/// 「右键菜单样式」：在 Windows 11 的新式精简菜单和 Windows 10 的完整经典菜单之间切换。
/// <para>
/// 原理是给 CLSID 注册一个空的 InprocServer32，让 shell 走旧版菜单：
/// 有 <c>{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32</c> 这个键就是 Windows 10 样式，
/// 删掉它就回到 Windows 11 样式。键在 HKCU 下，普通权限就能改，不需要管理员；
/// 万一被策略锁住，再退回管理员 PowerShell 跑 <c>reg add</c> / <c>reg delete</c>。
/// </para>
/// </summary>
public static class ContextMenuStyle
{
    private const string ClsidRoot = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const string InprocKey = ClsidRoot + @"\InprocServer32";

    /// <summary>当前是否为 Windows 10 样式（InprocServer32 键存在）。读不到返回 false。</summary>
    public static bool TryGetIsWindows10(out bool windows10)
    {
        windows10 = false;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(InprocKey);
            windows10 = key is not null;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 切换样式：Windows 10 = 建空 InprocServer32，Windows 11 = 删掉整个 CLSID 键。
    /// 改完要重启资源管理器才看得到效果。
    /// </summary>
    public static bool Apply(bool windows10, out string error)
    {
        error = string.Empty;

        try
        {
            if (windows10)
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(InprocKey, writable: true);
                key.SetValue(string.Empty, string.Empty, RegistryValueKind.String);
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(ClsidRoot, throwOnMissingSubKey: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 键被策略锁住：交给管理员 PowerShell 用 reg 命令处理
            return ApplyElevated(windows10, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>用管理员 Windows PowerShell 跑等价的 reg 命令，用于普通权限改不动的情况。</summary>
    private static bool ApplyElevated(bool windows10, out string error)
    {
        error = string.Empty;

        string command = windows10
            ? $"reg add \"HKCU\\{InprocKey}\" /f /ve"
            : $"reg delete \"HKCU\\{ClsidRoot}\" /f";

        try
        {
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + command + "\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                error = "无法启动管理员 PowerShell";
                return false;
            }

            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return true;
            }

            error = "reg 命令返回 " + process.ExitCode;
            return false;
        }
        catch (Win32Exception)
        {
            // 用户在 UAC 上点了「否」
            error = "已取消提权";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>重启资源管理器，让菜单样式立刻生效。</summary>
    public static bool RestartExplorer(out string error)
    {
        error = string.Empty;

        try
        {
            foreach (Process process in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                    // 有的会话进程动不了，忽略
                }
                finally
                {
                    process.Dispose();
                }
            }

            // explorer.exe 退出后系统通常会自动拉起；这里补一次，没退出干净也能回来
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
