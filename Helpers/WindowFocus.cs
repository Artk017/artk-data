using System;
using System.Runtime.InteropServices;

namespace WinUIDemo.Helpers;

/// <summary>
/// 窗口置前的小工具。WinUI 的 <c>Window.Activate()</c> 在刚创建、
/// 位置又被改过的情况下不保证压过调用方窗口，这里补一次 Win32 的前置。
/// </summary>
internal static class WindowFocus
{
    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
