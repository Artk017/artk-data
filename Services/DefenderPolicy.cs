using System;
using System.Security.Principal;
using Microsoft.Win32;

namespace ArtkTools.Services;

/// <summary>
/// 「关闭防病毒」开关：同时管组策略里
/// 「计算机配置 → 管理模板 → Windows 组件 → Microsoft Defender 防病毒」下的三条策略：
/// <list type="bullet">
///   <item>关闭 Microsoft Defender 防病毒 → <c>DisableAntiSpyware</c>（开时 = 已启用 = 1）</item>
///   <item>允许反恶意软件服务使用普通优先级启动 → <c>AllowFastServiceStartup</c>（开时 = 已禁用 = 0）</item>
///   <item>允许反恶意软件服务始终保持运行状态 → <c>ServiceKeepAlive</c>（开时 = 已禁用 = 0）</item>
/// </list>
/// 全部落在 <c>HKLM\SOFTWARE\Policies\Microsoft\Windows Defender</c>。开关关掉时三个值都删掉，
/// 即恢复「未配置」（写 0 是「已禁用」，和未配置不是一回事）。
/// <para>
/// 改 HKLM 要管理员，走和防火墙一样的 runas 自提权。策略立刻写进注册表（gpedit 里能看到），
/// 但 Defender 服务是开机读策略的，真正生效要重启。
/// </para>
/// </summary>
public static class DefenderPolicy
{
    /// <summary>提权实例的命令行开关，后面跟 <c>0</c>（未配置）/ <c>1</c>（已启用）。</summary>
    public const string ElevationSwitch = "--set-defender-off";

    private const string KeyPath = @"SOFTWARE\Policies\Microsoft\Windows Defender";

    private const string TurnOffName = "DisableAntiSpyware";
    private const string NormalPriorityName = "AllowFastServiceStartup";
    private const string KeepAliveName = "ServiceKeepAlive";

    /// <summary>开关打开时要写成的样子：关闭 Defender + 两项「已禁用」。</summary>
    private static readonly (string Name, int Value)[] DisabledState =
    {
        (TurnOffName, 1),          // 关闭 Microsoft Defender 防病毒 = 已启用
        (NormalPriorityName, 0),   // 允许反恶意软件服务使用普通优先级启动 = 已禁用
        (KeepAliveName, 0),        // 允许反恶意软件服务始终保持运行状态 = 已禁用
    };

    /// <summary>
    /// 是否处于「已启用」状态：三条策略都在位才算（其中任何一条被人单独改过就当没开，
    /// 再点一下开关会一次性写回三条）。
    /// </summary>
    public static bool TryGetDisabled()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: false);
            if (key is null)
            {
                return false;
            }

            foreach ((string name, int value) in DisabledState)
            {
                if (key.GetValue(name) is not int actual || actual != value)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>开 = 三条策略都设成「已启用 / 已禁用」，关 = 三条全部恢复「未配置」。</summary>
    public static bool Apply(bool disabled, out string error)
    {
        error = string.Empty;

        return IsElevated
            ? SetDirect(disabled, out error)
            : ApplyViaElevatedSelf(disabled, out error);
    }

    private static bool SetDirect(bool disabled, out string error)
    {
        error = string.Empty;

        try
        {
            if (disabled)
            {
                using RegistryKey key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
                if (key is null)
                {
                    error = "打不开 Defender 策略键";
                    return false;
                }

                foreach ((string name, int value) in DisabledState)
                {
                    key.SetValue(name, value, RegistryValueKind.DWord);
                }
            }
            else
            {
                // 未配置 = 这三个值都不存在
                using RegistryKey? existing = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
                if (existing is not null)
                {
                    foreach ((string name, _) in DisabledState)
                    {
                        existing.DeleteValue(name, throwOnMissingValue: false);
                    }
                }
            }

            // 同步本地组策略：gpedit 显示的是 Registry.pol，不写它的话那边永远显示「未配置」，
            // 而且下次组策略刷新（开机 / gpupdate）会按旧文件把注册表改回去或删掉
            bool gpoOk = true;
            foreach ((string name, int value) in DisabledState)
            {
                gpoOk &= disabled
                    ? LocalGroupPolicy.SetDword(KeyPath, name, value)
                    : LocalGroupPolicy.Remove(KeyPath, name);
            }

            if (!gpoOk)
            {
                error = "注册表已写入，但本地组策略文件没改成（gpedit 里可能仍显示「未配置」）";
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

    private static bool ApplyViaElevatedSelf(bool disabled, out string error)
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
                Arguments = $"{ElevationSwitch} {(disabled ? "1" : "0")}",
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
