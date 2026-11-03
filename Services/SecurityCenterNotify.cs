using System;
using Microsoft.Win32;

namespace WinUIDemo.Services;

/// <summary>
/// 「防火墙通知」开关：控制 Windows 安全中心服务（wscsvc）的启动类型。
/// 就是 wscsvc 键下的 <c>Start</c>（REG_DWORD）：2 = 自动启动（通知开启），4 = 禁用（通知关闭）。
/// 键在 HKLM 下，默认只有管理员能写，所以界面只在当前用户已拿到该键权限时才给出这个开关。
/// </summary>
public static class SecurityCenterNotify
{
    private const string ValueName = "Start";

    /// <summary>自动启动：安全中心在跑，防火墙相关通知照常弹。</summary>
    public const int StartAuto = 2;

    /// <summary>禁用：安全中心不启动，防火墙通知不再弹。</summary>
    public const int StartDisabled = 4;

    // 只请求读 / 写值本身，不碰 READ_CONTROL：
    // 这样即使还没拿到 ACL 权限，也能读到当前值
    private const int KeyQueryValue = 0x0001;
    private const int KeySetValue = 0x0002;

    /// <summary>
    /// 读当前值：2 = 开启，4 = 关闭；其它值（手动 / 延迟启动等）或读不到返回 null。
    /// </summary>
    public static bool? TryGetEnabled()
    {
        try
        {
            using RegistryKey? key = HklmKey.Open(RegistryAcl.TargetSubKey, KeyQueryValue);
            if (key?.GetValue(ValueName) is not int start)
            {
                return null;
            }

            return start switch
            {
                StartAuto => true,
                StartDisabled => false,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>写 Start：开启 = 2，关闭 = 4。需要对该键有写权限，失败返回 false。</summary>
    public static bool TrySetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey? key = HklmKey.Open(RegistryAcl.TargetSubKey, KeySetValue);
            if (key is null)
            {
                return false;
            }

            key.SetValue(ValueName, enabled ? StartAuto : StartDisabled, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
