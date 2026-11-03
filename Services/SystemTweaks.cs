using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinUIDemo.Services;

/// <summary>
/// 通过 user32!SystemParametersInfo 读写当前用户的输入 / 界面设置。
/// 所有写入都带 SPIF_UPDATEINIFILE | SPIF_SENDCHANGE：立即生效、写入用户配置、并广播给其它程序。
/// </summary>
public static class SystemTweaks
{
    private const uint SpiGetMouse = 0x0003;
    private const uint SpiSetMouse = 0x0004;
    private const uint SpiGetWheelScrollLines = 0x0068;
    private const uint SpiSetWheelScrollLines = 0x0069;
    private const uint SpiGetMouseSpeed = 0x0070;
    private const uint SpiSetMouseSpeed = 0x0071;
    private const uint SpiGetClientAreaAnimation = 0x1042;
    private const uint SpiSetClientAreaAnimation = 0x1043;

    private const uint SpifUpdateIniFile = 0x0001;
    private const uint SpifSendChange = 0x0002;

    // 只写用户配置：毫秒级完成。鼠标速度/滚轮行数/指针精度由系统输入栈直接读取，无需广播。
    private const uint SaveOnly = SpifUpdateIniFile;

    // 额外广播 WM_SETTINGCHANGE：其它程序能立刻感知，但会同步等待各窗口响应，
    // 实测在装有大型应用的机器上可达 200ms 以上，只能用于低频操作。
    private const uint SaveAndBroadcast = SpifUpdateIniFile | SpifSendChange;

    private const string Library = "user32.dll";
    private const string EntryPoint = "SystemParametersInfoW";

    [DllImport(Library, EntryPoint = EntryPoint, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfoInt(uint action, uint param, ref int value, uint flags);

    [DllImport(Library, EntryPoint = EntryPoint, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfoIntArray(uint action, uint param, int[] value, uint flags);

    [DllImport(Library, EntryPoint = EntryPoint, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfoPtr(uint action, uint param, IntPtr value, uint flags);

    // ---------------- 提高指针精度（鼠标加速）----------------
    // 三元组：[阈值1, 阈值2, 加速标志]；开启为 6/10/1，关闭为 0/0/0。
    public static bool TryGetPointerPrecision(out bool enabled)
    {
        var values = new int[3];
        if (SystemParametersInfoIntArray(SpiGetMouse, 0, values, 0))
        {
            enabled = values[2] != 0;
            return true;
        }

        enabled = false;
        return false;
    }

    public static bool SetPointerPrecision(bool enabled)
    {
        var values = enabled ? new[] { 6, 10, 1 } : new[] { 0, 0, 0 };
        return SystemParametersInfoIntArray(SpiSetMouse, 0, values, SaveOnly);
    }

    // ---------------- 指针速度（1–20，默认 10）----------------
    public static bool TryGetMouseSpeed(out int speed)
    {
        int value = 0;
        bool ok = SystemParametersInfoInt(SpiGetMouseSpeed, 0, ref value, 0);
        speed = ok ? value : 10;
        return ok;
    }

    public static bool SetMouseSpeed(int speed)
    {
        int clamped = Math.Clamp(speed, 1, 20);
        return SystemParametersInfoPtr(SpiSetMouseSpeed, 0, new IntPtr(clamped), SaveOnly);
    }

    // ---------------- 滚轮一次滚动行数（-1 表示整页）----------------
    public static bool TryGetWheelScrollLines(out int lines)
    {
        int value = 0;
        bool ok = SystemParametersInfoInt(SpiGetWheelScrollLines, 0, ref value, 0);
        lines = ok ? value : 3;
        return ok;
    }

    public static bool SetWheelScrollLines(int lines)
    {
        // SPI_SETWHEELSCROLLLINES 的值由 uiParam 传递，pvParam 必须为空
        int clamped = Math.Clamp(lines, 1, 100);
        return SystemParametersInfoPtr(SpiSetWheelScrollLines, (uint)clamped, IntPtr.Zero, SaveOnly);
    }

    // ---------------- 界面动画 ----------------
    // 注意：该接口的写入值直接当指针传入；用 ref int 传指针会被静默忽略。
    public static bool TryGetAnimations(out bool enabled)
    {
        int value = 0;
        bool ok = SystemParametersInfoInt(SpiGetClientAreaAnimation, 0, ref value, 0);
        enabled = !ok || value != 0;
        return ok;
    }

    public static bool SetAnimations(bool enabled)
    {
        return SystemParametersInfoPtr(SpiSetClientAreaAnimation, 0, new IntPtr(enabled ? 1 : 0), SaveAndBroadcast);
    }

    // ---------------- 电源计划：高性能 / 平衡 ----------------
    // 这两个 GUID 是 Windows 内置方案的固定标识，即使方案被隐藏也能直接激活。
    private static readonly Guid BalancedScheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformanceScheme = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    // 子组 / 设置 GUID：睡眠、关闭显示器。数值单位是秒。
    private static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    private static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    /// <summary>开高性能时统一设成 30 分钟。</summary>
    private const uint IdleTimeoutSeconds = 30 * 60;

    private const string PowerLibrary = "powrprof.dll";

    [DllImport(PowerLibrary)]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport(PowerLibrary)]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport(PowerLibrary)]
    private static extern uint PowerWriteACValueIndex(
        IntPtr userRootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport(PowerLibrary)]
    private static extern uint PowerWriteDCValueIndex(
        IntPtr userRootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>当前活动方案是否为「高性能」。其它任意方案（平衡 / 节能 / 自定义）都算关闭。</summary>
    public static bool TryGetHighPerformance(out bool enabled)
    {
        enabled = false;

        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr buffer) != 0 || buffer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var active = Marshal.PtrToStructure<Guid>(buffer);
            enabled = active == HighPerformanceScheme;
            return true;
        }
        finally
        {
            LocalFree(buffer);
        }
    }

    /// <summary>
    /// 开启切到「高性能」，同时把睡眠 / 关闭显示器都设成 30 分钟（交流与直流都设）。
    /// 关闭只切回「平衡」，不回滚时间——所以平衡方案也一并设置成 30 分钟，
    /// 否则切到平衡会带回它自己的 15 / 5 分钟，看起来就像被「恢复」了。
    /// </summary>
    public static bool SetHighPerformance(bool enabled)
    {
        var target = enabled ? HighPerformanceScheme : BalancedScheme;

        if (PowerSetActiveScheme(IntPtr.Zero, ref target) != 0)
        {
            return false;
        }

        if (enabled)
        {
            var high = HighPerformanceScheme;
            var balanced = BalancedScheme;
            ApplyIdleTimeouts(high);
            ApplyIdleTimeouts(balanced);
            PowerSetActiveScheme(IntPtr.Zero, ref high);
        }

        return true;
    }

    /// <summary>把方案里的睡眠 / 关闭显示器改成 30 分钟，然后重新激活让改动立即生效。</summary>
    private static void ApplyIdleTimeouts(Guid scheme)
    {
        var sleepGroup = SubSleep;
        var sleepSetting = StandbyIdle;
        PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sleepGroup, ref sleepSetting, IdleTimeoutSeconds);
        PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sleepGroup, ref sleepSetting, IdleTimeoutSeconds);

        var videoGroup = SubVideo;
        var videoSetting = VideoIdle;
        PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref videoGroup, ref videoSetting, IdleTimeoutSeconds);
        PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref videoGroup, ref videoSetting, IdleTimeoutSeconds);

        PowerSetActiveScheme(IntPtr.Zero, ref scheme);
    }

    // ---------------- 个性化：背景 / 颜色模式 / 任务栏对齐 ----------------
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private const string WallpapersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers";
    private const string DesktopKey = @"Control Panel\Desktop";

    // Windows 11 的桌面聚焦有自己的总开关，只写 BackgroundType 不会生效
    private const string SpotlightKey = @"Software\Microsoft\Windows\CurrentVersion\DesktopSpotlight\Settings";

    private const int BackgroundPicture = 0;
    private const int BackgroundSpotlight = 3;
    private const string FallbackWallpaper = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";

    private const uint SpiSetDeskWallpaper = 0x0014;

    [DllImport(Library, EntryPoint = EntryPoint, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfoText(uint action, uint param, string value, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    /// <summary>背景是否为「图片」。判断依据是聚焦总开关：关掉聚焦就是图片模式。</summary>
    public static bool TryGetBackgroundPicture(out bool picture)
    {
        int? spotlightEnabled = ReadDword(SpotlightKey, "EnabledState");

        // 没有这个键就是压根没开聚焦（老系统 / 精简镜像），按「图片」显示，别把开关置灰
        picture = spotlightEnabled is null || spotlightEnabled == 0;
        return true;
    }

    /// <summary>开启切到「图片」，关闭切到「Windows 聚焦」。</summary>
    public static bool SetBackgroundPicture(bool picture)
    {
        // 两个键都要写：EnabledState 控制聚焦特性开关，BackgroundType 是设置界面读的模式
        bool ok = WriteDword(SpotlightKey, "EnabledState", picture ? 0 : 1);
        ok &= WriteDword(WallpapersKey, "BackgroundType", picture ? BackgroundPicture : BackgroundSpotlight);

        // 切回图片要重新套一次壁纸，否则桌面还停在聚焦那张
        if (picture)
        {
            SystemParametersInfoText(SpiSetDeskWallpaper, 0, LastPictureWallpaper(), SaveAndBroadcast);
        }

        Broadcast("Desktop");
        return ok;
    }

    /// <summary>从历史记录里挑一张真正的图片当壁纸（跳过聚焦自带的素材）。</summary>
    private static string LastPictureWallpaper()
    {
        for (int i = 0; i < 5; i++)
        {
            string? path = ReadString(WallpapersKey, "BackgroundHistoryPath" + i);

            if (string.IsNullOrEmpty(path) || path.Contains("DesktopSpotlight", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (System.IO.File.Exists(path))
            {
                return path;
            }
        }

        return FallbackWallpaper;
    }

    /// <summary>颜色模式是否为深色（AppsUseLightTheme == 0）。</summary>
    public static bool TryGetDarkMode(out bool dark)
    {
        int? value = ReadDword(PersonalizeKey, "AppsUseLightTheme");
        dark = value == 0;
        return value is not null;
    }

    /// <summary>开启切到深色，关闭切到浅色；应用和系统外壳一起改。</summary>
    public static bool SetDarkMode(bool dark)
    {
        int light = dark ? 0 : 1;

        bool ok = WriteDword(PersonalizeKey, "AppsUseLightTheme", light);
        ok &= WriteDword(PersonalizeKey, "SystemUsesLightTheme", light);

        Broadcast("ImmersiveColorSet");
        return ok;
    }

    /// <summary>任务栏图标是否靠左（TaskbarAl == 0 表示靠左）。</summary>
    public static bool TryGetTaskbarLeft(out bool left)
    {
        // 键值不存在（精简镜像 / 没动过任务栏设置的虚拟机）就是系统默认的「居中」，
        // 这里必须返回 true：返回 false 会让界面把这个开关整个置灰
        int? value = ReadDword(AdvancedKey, "TaskbarAl");
        left = value == 0;
        return true;
    }

    /// <summary>开启切到靠左，关闭切到居中；explorer 会自己监听这个键，无需重启。</summary>
    public static bool SetTaskbarLeft(bool left)
    {
        bool ok = WriteDword(AdvancedKey, "TaskbarAl", left ? 0 : 1);
        Broadcast("TraySettings");
        return ok;
    }

    // ---------------- 注册表 / 广播小工具 ----------------

    private static int? ReadDword(string path, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue(name) as int?;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadString(string path, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool WriteDword(string path, string name, int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue(name, value, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>通知其它程序设置变了（不阻塞等待，避免拖慢 UI）。</summary>
    private static void Broadcast(string area)
    {
        SendMessageTimeout((IntPtr)0xFFFF, 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, area, 0x0002, 1000, out _);
    }

    /// <summary>取最后一次 Win32 错误，用于界面提示。</summary>
    public static string LastError()
    {
        int code = Marshal.GetLastWin32Error();
        return code == 0 ? string.Empty : new Win32Exception(code).Message;
    }
}
