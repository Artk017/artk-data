using System;
using System.Runtime.InteropServices;

namespace WinUIDemo.Helpers;

/// <summary>
/// 窗口摆位用的屏幕工作区查询。
/// <para>
/// 不要用 <c>DisplayArea.WorkArea</c> 来做夹取：它返回的是显示器<b>原生</b>像素
/// （这台机器 2560×1440），而 <c>AppWindow.Position</c> / <c>GetWindowRect</c> 用的是
/// 当前 DPI 缩放后的<b>有效</b>像素（2048×1152）。两者混用会让贴边窗口跑出屏幕。
/// 这里统一走 Win32 的 <c>rcWork</c>，与窗口坐标同源。
/// </para>
/// </summary>
internal static class ScreenPlacement
{
    private const uint MonitorDefaultToNearest = 0x00000002;

    public static WorkArea GetWorkArea(int x, int y)
    {
        var point = new POINT { X = x, Y = y };
        IntPtr monitor = MonitorFromPoint(point, MonitorDefaultToNearest);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            RECT r = info.rcWork;
            return new WorkArea(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        return new WorkArea(0, 0, 0, 0);
    }

    internal readonly record struct WorkArea(int Left, int Top, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
