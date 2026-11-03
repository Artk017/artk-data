using System;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace WinUIDemo.Helpers;

/// <summary>
/// 给弹窗加最小 / 最大尺寸限制。
/// WinUI 的 AppWindow 只有初始尺寸，没有 MinSize / MaxSize，所以只能接管窗口过程：
/// 拖动边框时系统会先发 WM_GETMINMAXINFO 问「能缩到多小、能放到多大」。
/// 最小尺寸按 DIP 给（界面用 DIP 布局），回答系统时乘缩放比换成物理像素；
/// 最大尺寸直接跟着所在屏幕的工作区走，免得窗口被拉到工作区外面。
/// </summary>
internal sealed class WindowSizeLimits : IDisposable
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int GwlWndProc = -4;
    private const uint MonitorDefaultToNearest = 2;

    private readonly IntPtr _hwnd;
    private readonly SubclassProc? _proc;
    private readonly IntPtr _previous;
    private readonly int _minWidthDip;
    private readonly int _minHeightDip;
    private bool _disposed;

    private WindowSizeLimits(IntPtr hwnd, int minWidthDip, int minHeightDip)
    {
        _hwnd = hwnd;
        _minWidthDip = minWidthDip;
        _minHeightDip = minHeightDip;

        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // 委托必须一直活着：被 GC 回收后窗口过程回调到已释放的存根会直接崩
        _proc = WndProc;
        _previous = SetWindowLongPtr(hwnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_proc));
    }

    /// <summary>最小尺寸用 DIP；最大尺寸取屏幕工作区。</summary>
    public static WindowSizeLimits Apply(IntPtr hwnd, int minWidthDip, int minHeightDip)
    {
        return new WindowSizeLimits(hwnd, minWidthDip, minHeightDip);
    }

    /// <summary>最小尺寸换算成物理像素（初始尺寸也按它夹一次）。</summary>
    public SizeInt32 MinPixels => new(ToPixels(_minWidthDip), ToPixels(_minHeightDip));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_previous != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, GwlWndProc, _previous);
        }
    }

    private int ToPixels(int dip)
    {
        uint dpi = _hwnd == IntPtr.Zero ? 96 : GetDpiForWindow(_hwnd);
        return (int)Math.Round(dip * (dpi == 0 ? 96 : dpi) / 96.0);
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        // 先让原来的窗口过程把 MINMAXINFO 填好（最大尺寸、最大化位置都在里面）
        IntPtr result = CallWindowProc(_previous, hwnd, message, wParam, lParam);

        if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero)
        {
            return result;
        }

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        info.MinTrackSize = new NativePoint(
            Math.Max(info.MinTrackSize.X, ToPixels(_minWidthDip)),
            Math.Max(info.MinTrackSize.Y, ToPixels(_minHeightDip)));

        if (TryGetWorkArea(hwnd, out int workWidth, out int workHeight))
        {
            info.MaxTrackSize = new NativePoint(
                Math.Min(info.MaxTrackSize.X, workWidth),
                Math.Min(info.MaxTrackSize.Y, workHeight));
        }

        Marshal.StructureToPtr(info, lParam, false);
        return IntPtr.Zero;
    }

    private static bool TryGetWorkArea(IntPtr hwnd, out int width, out int height)
    {
        width = 0;
        height = 0;

        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        width = info.Work.Right - info.Work.Left;
        height = info.Work.Bottom - info.Work.Top;
        return true;
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
