using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace WinUIDemo.Services;

/// <summary>
/// 任务栏通知区域（系统托盘）图标。WinUI 3 没有自带托盘 API，
/// 这里直接用 shell32!Shell_NotifyIcon，消息挂到主窗口的 WndProc 上。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint NIM_ADD = 0x0000;
    private const uint NIM_MODIFY = 0x0001;
    private const uint NIM_DELETE = 0x0002;

    private const uint NIF_MESSAGE = 0x0001;
    private const uint NIF_ICON = 0x0002;
    private const uint NIF_TIP = 0x0004;

    private const uint WM_APP = 0x8000;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_NULL = 0x0000;

    private const int GWLP_WNDPROC = -4;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;

    private const uint MF_STRING = 0x0000;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int MenuOpen = 1;
    private const int MenuExit = 2;

    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly IntPtr _previousWndProc;
    private readonly WndProcDelegate _wndProc;
    private readonly Action _open;
    private readonly Action _exit;
    private readonly uint _callbackMessage = WM_APP + 1;

    private bool _disposed;

    private TrayIcon(IntPtr hwnd, IntPtr icon, Action open, Action exit, string tooltip)
    {
        _hwnd = hwnd;
        _icon = icon;
        _open = open;
        _exit = exit;

        // 子类化窗口过程，接收托盘回调消息
        _wndProc = WndProc;
        _previousWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));

        var data = CreateData();
        data.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
        data.uCallbackMessage = _callbackMessage;
        data.hIcon = _icon;
        data.szTip = tooltip;

        Shell_NotifyIcon(NIM_ADD, ref data);
    }

    /// <summary>在指定窗口上挂一个托盘图标。</summary>
    public static TrayIcon Create(Window window, string iconPath, string tooltip, Action open, Action exit)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        IntPtr icon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);

        return new TrayIcon(hwnd, icon, open, exit, tooltip);
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _callbackMessage)
        {
            uint mouse = (uint)lParam.ToInt64();

            switch (mouse)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    _open();
                    return IntPtr.Zero;

                case WM_RBUTTONUP:
                    ShowMenu();
                    return IntPtr.Zero;
            }
        }

        return CallWindowProc(_previousWndProc, hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        AppendMenu(menu, MF_STRING, MenuOpen, "打开");
        AppendMenu(menu, 0x0800 /* MF_SEPARATOR */, UIntPtr.Zero, string.Empty);
        AppendMenu(menu, MF_STRING, MenuExit, "退出");

        GetCursorPos(out POINT point);

        // 菜单弹出前要先把自己设为前台，否则点别处菜单不会消失
        SetForegroundWindow(_hwnd);
        int command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);

        switch (command)
        {
            case MenuOpen:
                _open();
                break;
            case MenuExit:
                Dispose();
                _exit();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var data = CreateData();
        Shell_NotifyIcon(NIM_DELETE, ref data);

        if (_previousWndProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _previousWndProc);
        }
    }

    // ---------------- Win32 ----------------

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr item, string text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
