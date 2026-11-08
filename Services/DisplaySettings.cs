using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ArtkTools.Services;

/// <summary>
/// 当前显示器的刷新率。枚举走 EnumDisplaySettings，切换走 ChangeDisplaySettingsEx，
/// 和「设置 → 系统 → 屏幕 → 高级显示器设置」里的下拉框是同一套东西。
/// </summary>
public static class DisplaySettings
{
    private const int EnumCurrentSettings = -1;
    private const uint DmDisplayFrequency = 0x400000;
    private const uint CdsUpdateRegistry = 0x00000001;
    private const int DispChangeSuccessful = 0;

    /// <summary>当前刷新率（Hz）。</summary>
    public static int CurrentRefreshRate()
    {
        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(null, EnumCurrentSettings, ref mode) ? mode.dmDisplayFrequency : 0;
    }

    /// <summary>当前分辨率下可选的刷新率，升序去重。</summary>
    public static IReadOnlyList<int> AvailableRefreshRates()
    {
        var current = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(null, EnumCurrentSettings, ref current))
        {
            return Array.Empty<int>();
        }

        var rates = new SortedSet<int>();

        for (int index = 0; index < 512; index++)
        {
            var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(null, index, ref mode))
            {
                break;
            }

            // 只保留当前分辨率 / 位深下真实可用的模式，0 和 1 是「默认/硬件默认」，跳过
            if (mode.dmPelsWidth == current.dmPelsWidth &&
                mode.dmPelsHeight == current.dmPelsHeight &&
                mode.dmBitsPerPel == current.dmBitsPerPel &&
                mode.dmDisplayFrequency > 1)
            {
                rates.Add(mode.dmDisplayFrequency);
            }
        }

        return new List<int>(rates);
    }

    /// <summary>切换刷新率；分辨率等其它参数保持不变。</summary>
    public static bool SetRefreshRate(int hertz)
    {
        if (hertz <= 1)
        {
            return false;
        }

        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(null, EnumCurrentSettings, ref mode))
        {
            return false;
        }

        mode.dmDisplayFrequency = hertz;
        mode.dmFields |= (int)DmDisplayFrequency;

        int result = ChangeDisplaySettingsEx(null, ref mode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);

        return result == DispChangeSuccessful;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;

        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;

        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ChangeDisplaySettingsEx(
        string? deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr param);
}
