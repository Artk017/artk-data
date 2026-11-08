using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ArtkTools.Services;

/// <summary>
/// 「关闭防火墙」开关：控制「控制面板 → Windows Defender 防火墙 → 自定义设置」里的
/// 专用网络 / 公用网络两个「关闭 Windows Defender 防火墙」勾选项。
/// <para>
/// 落盘位置是 HKLM 下 SharedAccess\Parameters\FirewallPolicy 的
/// <c>StandardProfile\EnableFirewall</c>（专用）和 <c>PublicProfile\EnableFirewall</c>（公用），
/// 1 = 开启，0 = 关闭。改这里必须管理员，所以走和「添加权限」一样的 runas 自提权：
/// 提权实例只做这一件事，做完立刻退出。
/// </para>
/// </summary>
public static class FirewallControl
{
    /// <summary>提权实例的命令行开关，后面跟 <c>0</c>（关）/ <c>1</c>（开）。</summary>
    public const string ElevationSwitch = "--set-firewall";

    private const string BasePath = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
    private const string PrivateKey = BasePath + @"\StandardProfile";
    private const string PublicKey = BasePath + @"\PublicProfile";
    private const string ValueName = "EnableFirewall";

    // 只请求读 / 写值本身：不碰 READ_CONTROL，普通用户也能读到当前状态
    private const int KeyQueryValue = 0x0001;
    private const int KeySetValue = 0x0002;

    // INetFwPolicy2 的 profile 常量
    private const int ProfilePrivate = 2;
    private const int ProfilePublic = 4;

    /// <summary>
    /// 防火墙是否处于「关闭」状态：两个 profile 都关才算关。
    /// 读不到返回 null（界面会把开关置灰）。
    /// </summary>
    public static bool? TryGetDisabled()
    {
        int? privateValue = ReadValue(PrivateKey);
        int? publicValue = ReadValue(PublicKey);

        if (privateValue is null || publicValue is null)
        {
            return null;
        }

        return privateValue == 0 && publicValue == 0;
    }

    /// <summary>关 / 开防火墙（专用 + 公用）。未提权时会拉起 UAC 提权实例去改。</summary>
    public static bool Apply(bool disabled, out string error)
    {
        error = string.Empty;

        return IsElevated
            ? SetDirect(disabled, out error)
            : ApplyViaElevatedSelf(disabled, out error);
    }

    /// <summary>提权状态下真正改设置：优先走防火墙 COM API，失败再直接写注册表。</summary>
    private static bool SetDirect(bool disabled, out string error)
    {
        error = string.Empty;

        if (SetViaPolicyApi(disabled))
        {
            return true;
        }

        return SetViaRegistry(disabled, out error);
    }

    /// <summary>用 INetFwPolicy2 改，和「控制面板」走的是同一套接口，改完立刻生效。</summary>
    private static bool SetViaPolicyApi(bool disabled)
    {
        object? policy = null;

        try
        {
            Type? type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null)
            {
                return false;
            }

            policy = Activator.CreateInstance(type);
            if (policy is null)
            {
                return false;
            }

            foreach (int profile in new[] { ProfilePrivate, ProfilePublic })
            {
                type.InvokeMember(
                    "FirewallEnabled",
                    BindingFlags.SetProperty,
                    binder: null,
                    target: policy,
                    args: new object[] { profile, !disabled });
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (policy is not null && Marshal.IsComObject(policy))
            {
                Marshal.ReleaseComObject(policy);
            }
        }
    }

    /// <summary>COM 不通时兜底：直接写两个 profile 的 EnableFirewall。</summary>
    private static bool SetViaRegistry(bool disabled, out string error)
    {
        error = string.Empty;
        bool ok = true;

        foreach (string path in new[] { PrivateKey, PublicKey })
        {
            try
            {
                using RegistryKey? key = HklmKey.Open(path, KeySetValue);
                if (key is null)
                {
                    ok = false;
                    continue;
                }

                key.SetValue(ValueName, disabled ? 0 : 1, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                ok = false;
            }
        }

        return ok;
    }

    private static int? ReadValue(string subKeyPath)
    {
        try
        {
            using RegistryKey? key = HklmKey.Open(subKeyPath, KeyQueryValue);
            return key?.GetValue(ValueName) is int value ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>当前进程是否已经是管理员。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>用 runas 拉起自身，让提权实例去改；返回这次操作是否成功。</summary>
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
                Arguments = $"{ElevationSwitch} {(disabled ? "0" : "1")}",
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
