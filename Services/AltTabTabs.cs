using System;
using Microsoft.Win32;

namespace WinUIDemo.Services;

/// <summary>
/// 「按 Alt+Tab 时显示应用中的标签页」开关。
/// 对应「设置 → 系统 → 多任务处理 → 对齐或按 Alt+Tab 时显示应用中的标签页」下拉：
///   HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced
///   MultiTaskingAltTabFilter (REG_DWORD)
///       0 = 不显示选项卡（仅窗口）
///       3 = 显示 3 个最新选项卡
///       5 = 显示 5 个最新选项卡
/// 本开关语义：开 = 3 个最新选项卡；关 = 不显示选项卡。
/// </summary>
public static class AltTabTabs
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ValueName = "MultiTaskingAltTabFilter";

    /// <summary>「不显示选项卡」，只显示窗口。</summary>
    private const int WindowsOnly = 0;

    /// <summary>「3 个最新选项卡」，也是 Windows 的默认值。</summary>
    private const int ThreeTabs = 3;

    /// <summary>
    /// 当前是否显示应用内的标签页。值不为 0 即视为开启（3 / 5 都算）；
    /// 键或值缺失时按系统默认的「3 个最新选项卡」处理，返回 true。
    /// </summary>
    public static bool TryGetEnabled(out bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
            int value = ReadValue(key?.GetValue(ValueName));

            enabled = value != WindowsOnly;
            return true;
        }
        catch (Exception)
        {
            enabled = false;
            return false;
        }
    }

    /// <summary>开启写 3（3 个最新选项卡）；关闭写 0（不显示选项卡）。</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AdvancedKey, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue(ValueName, enabled ? ThreeTabs : WindowsOnly, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>把可能的 int / long / 字符串统一成 int；缺失时回落到系统默认的 3。</summary>
    private static int ReadValue(object? raw) => raw switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, out int parsed) => parsed,
        _ => ThreeTabs,
    };
}
