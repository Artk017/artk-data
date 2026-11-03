using System.Runtime.InteropServices;

namespace NovaServer;

/// <summary>
/// 任务栏托盘图标（Win32 Shell_NotifyIcon + 一个仅消息窗口）。
/// 窗口关闭 / 最小化时把界面藏起来，服务继续在后台跑；双击图标恢复窗口，右键菜单可以退出。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint WM_APP = 0x8000;
    private const uint CallbackMessage = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_NULL = 0x0000;
    private const uint HWND_MESSAGE = unchecked((uint)-3);

    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;
    private const uint NIF_INFO = 0x10;

    private const uint MF_STRING = 0x0;
    private const uint MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x100;
    private const uint TPM_RIGHTBUTTON = 0x2;

    private const uint LR_LOADFROMFILE = 0x10;
    private const uint LR_DEFAULTSIZE = 0x40;
    private const uint IMAGE_ICON = 1;

    private const uint NIIF_INFO = 0x1;

    private readonly string _tooltip;
    private readonly Action _onOpen;
    private readonly Action _onExit;
    private readonly WndProc _wndProc;
    private readonly string _className;
    private readonly IntPtr _hInstance;
    private readonly uint _taskbarCreatedMessage;

    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _visible;
    private bool _disposed;

    public TrayIcon(string tooltip, Action onOpen, Action onExit)
    {
        _tooltip = tooltip;
        _onOpen = onOpen;
        _onExit = onExit;

        _wndProc = WindowProc;
        _className = "ArtkServerTray_" + Guid.NewGuid().ToString("N");
        _hInstance = GetModuleHandle(null);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = _className,
        };

        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(0, _className, string.Empty, 0, 0, 0, 0, 0,
            new IntPtr(unchecked((int)HWND_MESSAGE)), IntPtr.Zero, _hInstance, IntPtr.Zero);

        _icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"),
            IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);

        if (_icon == IntPtr.Zero)
        {
            _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        }
    }

    /// <summary>把图标放到托盘里（已经在就不动）。</summary>
    public void Show()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_visible)
        {
            return;
        }

        Notify(NIM_ADD);
        _visible = true;
    }

    /// <summary>从托盘移除（恢复窗口 / 退出时）。</summary>
    public void Hide()
    {
        if (_disposed || !_visible)
        {
            return;
        }

        Notify(NIM_DELETE);
        _visible = false;
    }

    /// <summary>气泡提示，只在窗口藏起来的时候用。</summary>
    public void ShowBalloon(string title, string message)
    {
        if (_disposed || !_visible)
        {
            return;
        }

        var data = NewData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = NIIF_INFO;
        data.uTimeoutOrVersion = 5000;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void Notify(uint message)
    {
        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = Truncate(_tooltip, 127);
        Shell_NotifyIcon(message, ref data);
    }

    private NotifyIconData NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max];

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)lParam)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    Invoke(_onOpen);
                    return IntPtr.Zero;

                case WM_RBUTTONUP:
                    ShowMenu();
                    return IntPtr.Zero;
            }
        }
        else if (msg == _taskbarCreatedMessage)
        {
            // 资源管理器重启后托盘图标会丢，重新加回去
            if (_visible)
            {
                _visible = false;
                Show();
            }

            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        AppendMenu(menu, MF_STRING, 1, "打开 Artk Server");
        AppendMenu(menu, MF_SEPARATOR, 0, string.Empty);
        AppendMenu(menu, MF_STRING, 2, "退出");

        GetCursorPos(out Point point);

        // 不先抢前台，点菜单外面菜单不会自己关掉
        SetForegroundWindow(_hwnd);

        int command = (int)TrackPopupMenu(
            menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, 0, _hwnd, IntPtr.Zero);

        DestroyMenu(menu);
        PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

        if (command == 1)
        {
            Invoke(_onOpen);
        }
        else if (command == 2)
        {
            Invoke(_onExit);
        }
    }

    private static void Invoke(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // 窗口已经关掉就算了
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Hide();

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        try
        {
            UnregisterClass(_className, _hInstance);
        }
        catch
        {
            // 忽略
        }
    }

    // ── Win32 ──────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
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

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type,
        int width, int height, uint loadFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y,
        int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
