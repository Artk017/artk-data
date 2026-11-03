using System;
using System.Security.Principal;
using Microsoft.Win32;

namespace WinUIDemo.Services;

/// <summary>
/// 「应用运行通知」开关：控制「用户账户控制设置」里滑块的位置。
/// <para>
/// 开 = 中间那档「仅当应用尝试更改我的计算机时通知我（默认）」；关 = 最下面那档
/// 「出现以下情况时始终不要通知我：应用尝试安装软件或更改我的计算机 / 我更改了 Windows 设置」。
/// </para>
/// <para>
/// 落盘位置是 HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System 的四个值，
/// 普通用户可读、改要管理员，所以走和防火墙一样的 runas 自提权。EnableLUA 始终留在 1
/// （改成 0 是另一档「从不通知」，要重启，不是这个开关的范围）。
/// </para>
/// </summary>
public static class UacNotify
{
    /// <summary>提权实例的命令行开关，后面跟 <c>0</c>（关）/ <c>1</c>（开）。</summary>
    public const string ElevationSwitch = "--set-uac-notify";

    private const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    private const string EnableLuaValue = "EnableLUA";
    private const string AdminPromptValue = "ConsentPromptBehaviorAdmin";
    private const string SecureDesktopValue = "PromptOnSecureDesktop";

    /// <summary>
    /// 当前是否「只通知应用改动」（默认档）。读不到或停在其他档（比如始终通知）返回 null。
    /// </summary>
    public static bool? TryGetEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: false);
            if (key is null)
            {
                return null;
            }

            int lua = ReadDword(key, EnableLuaValue, 1);
            if (lua == 0)
            {
                // 从不通知（要重启那一档），也归到「关」
                return false;
            }

            return ReadDword(key, AdminPromptValue, -1) switch
            {
                5 => true,
                0 => false,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>开 / 关。未提权时拉起 runas 提权实例去改。</summary>
    public static bool Apply(bool enabled, out string error)
    {
        error = string.Empty;

        return IsElevated
            ? SetDirect(enabled, out error)
            : ApplyViaElevatedSelf(enabled, out error);
    }

    /// <summary>提权状态下真正改设置：四个值一次写完。</summary>
    private static bool SetDirect(bool enabled, out string error)
    {
        error = string.Empty;

        try
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
            if (key is null)
            {
                error = "打不开 UAC 策略键";
                return false;
            }

            // 两档的共同点：LUA 保持开启
            key.SetValue(EnableLuaValue, 1, RegistryValueKind.DWord);

            if (enabled)
            {
                key.SetValue(AdminPromptValue, 5, RegistryValueKind.DWord);      // 仅当应用尝试更改计算机时通知
                key.SetValue(SecureDesktopValue, 1, RegistryValueKind.DWord);   // 弹窗时压暗屏幕
            }
            else
            {
                key.SetValue(AdminPromptValue, 0, RegistryValueKind.DWord);      // 提升时不通知
                key.SetValue(SecureDesktopValue, 0, RegistryValueKind.DWord);
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "没有管理员权限";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static int ReadDword(RegistryKey key, string name, int fallback)
    {
        object? value = key.GetValue(name);
        return value is int number ? number : fallback;
    }

    /// <summary>当前进程是否已经是管理员。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>用 runas 拉起自身，让提权实例去改；返回这次操作是否成功。</summary>
    private static bool ApplyViaElevatedSelf(bool enabled, out string error)
    {
        error = string.Empty;

        try
        {
            string exe = Environment.ProcessPath ?? string.Empty;
            if (exe.Length == 0)
            {
                error = "拿不到程序路径";
                return false;
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{ElevationSwitch} {(enabled ? "1" : "0")}",
            };

            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                error = "无法启动提权实例";
                return false;
            }

            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return true;
            }

            error = "提权实例返回 " + process.ExitCode;
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
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
}
