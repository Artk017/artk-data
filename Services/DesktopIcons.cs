using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ArtkTools.Services;

/// <summary>
/// 桌面图标（此电脑 / 用户的文件 / 网络 / 回收站 / 控制面板）的显示开关。
/// 对应「设置 → 个性化 → 主题 → 桌面图标设置」，写 HideDesktopIcons 键并刷新桌面。
/// </summary>
public static class DesktopIcons
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    private const string ClassicKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu";
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    // SHCNE_ASSOCCHANGED / SHCNF_IDLIST：让资源管理器立刻重画桌面
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;
    private const uint ShcnfFlush = 0x1000;

    public sealed record Item(string Id, string Name);

    /// <summary>桌面图标设置里的五项（顺序与系统设置一致）。</summary>
    public static readonly IReadOnlyList<Item> All = new[]
    {
        new Item("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "此电脑"),
        new Item("{59031A47-3F72-44A7-89C5-5595FE6B30EE}", "用户的文件"),
        new Item("{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "网络"),
        new Item("{645FF040-5081-101B-9F08-00AA002F954E}", "回收站"),
        new Item("{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", "控制面板"),
    };

    /// <summary>
    /// 一次性读出五项当前是否真的显示在桌面上。
    /// 以注册表实际状态为准（<c>HideDesktopIcons</c> + 「显示桌面图标」总开关 <c>Advanced\HideIcons</c>）。
    /// 注意不能用 Shell.Application 枚举桌面命名空间：它会把这些特殊图标（此电脑 / 回收站…）
    /// 无条件列出来，哪怕已经被隐藏，导致读出来的永远「显示」。
    /// </summary>
    public static IReadOnlyDictionary<string, bool> QueryVisibility()
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in All)
        {
            map[item.Id] = IsVisibleByRegistry(item.Id);
        }

        return map;
    }

    /// <summary>某个图标当前是否显示（按注册表实际状态判断）。</summary>
    public static bool IsVisible(string id)
    {
        return IsVisibleByRegistry(id);
    }

    /// <summary>显示 / 隐藏某个桌面图标，并立即刷新桌面。</summary>
    public static bool SetVisible(string id, bool visible)
    {
        try
        {
            // 勾选某项时要先确保「显示桌面图标」总开关是打开的，
            // 否则写再多单项也无法让图标露面（读回来还会是不显示，看起来像点了没用）。
            if (visible)
            {
                using var adv = Registry.CurrentUser.CreateSubKey(AdvancedKey, writable: true);
                adv?.SetValue("HideIcons", 0, RegistryValueKind.DWord);
            }

            using (var key = Registry.CurrentUser.CreateSubKey(Key, writable: true))
            {
                if (key is null)
                {
                    return false;
                }

                key.SetValue(id, visible ? 0 : 1, RegistryValueKind.DWord);
            }

            // 老式开始菜单布局读的是 ClassicStartMenu，一并写掉，避免两种布局表现不一致
            using (var classic = Registry.CurrentUser.CreateSubKey(ClassicKey, writable: true))
            {
                classic?.SetValue(id, visible ? 0 : 1, RegistryValueKind.DWord);
            }
        }
        catch (Exception)
        {
            return false;
        }

        Refresh();
        return true;
    }

    // ---------------- 注册表读取 ----------------

    /// <summary>读注册表判断是否显示。值缺失按默认（显示）处理。</summary>
    private static bool IsVisibleByRegistry(string id)
    {
        try
        {
            // 「显示桌面图标」总开关关掉时，桌面上一个图标都不显示
            using (var adv = Registry.CurrentUser.OpenSubKey(AdvancedKey))
            {
                if (adv?.GetValue("HideIcons") is object rawMaster && ToInt(rawMaster) != 0)
                {
                    return false;
                }
            }

            // 优先看新版布局键；没有这个值时再看旧版布局键
            using (var key = Registry.CurrentUser.OpenSubKey(Key))
            {
                if (key?.GetValue(id) is object raw)
                {
                    return ToInt(raw) == 0;
                }
            }

            using (var classic = Registry.CurrentUser.OpenSubKey(ClassicKey))
            {
                if (classic?.GetValue(id) is object rawClassic)
                {
                    return ToInt(rawClassic) == 0;
                }
            }

            // 两处都没写值：Windows 默认显示
            return true;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>把可能的 int / long / 字符串统一成 int；其它类型按 0 处理。</summary>
    private static int ToInt(object raw) => raw switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, out int parsed) => parsed,
        _ => 0,
    };

    private static void Refresh()
    {
        // SHCNF_FLUSH：不缓冲，立刻投递
        SHChangeNotify(ShcneAssocChanged, ShcnfIdList | ShcnfFlush, IntPtr.Zero, IntPtr.Zero);

        // 只有上面那条时，桌面经常要再点一次才重画图标（改了注册表但看着没反应），
        // 所以补一次 shell 的「刷新」命令：Progman 的 WM_COMMAND 0x7402
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            SendMessageTimeout(progman, 0x0111 /* WM_COMMAND */, (IntPtr)0x7402, IntPtr.Zero, 0x0002, 1000, out _);
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
