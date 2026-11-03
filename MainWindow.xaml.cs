using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIDemo.Services;
using WinUIDemo.Views;
using Windows.Graphics;
using Windows.UI;

namespace WinUIDemo;

public sealed partial class MainWindow : Window
{
    /// <summary>当前程序版本：安装版读 exe 里写进去的版本号，开发时退回程序集版本。</summary>
    public static string CurrentVersion
    {
        get
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(info.ProductVersion))
                {
                    return info.ProductVersion.Split('+')[0];
                }

                if (!string.IsNullOrWhiteSpace(info.FileVersion))
                {
                    return info.FileVersion;
                }
            }
            catch
            {
                // 读不到就用程序集版本
            }

            Version? version = Assembly.GetEntryAssembly()?.GetName().Version;
            return version is null ? "—" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    /// <summary>当前窗口实例，供设置页等界面调用外壳能力。</summary>
    public static MainWindow? Instance { get; private set; }

    private TrayIcon? _trayIcon;

    /// <summary>用户已确认带着未完成的下载退出，放行最后一次关闭。</summary>
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;

        Title = "Artk Tools";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        VersionBadgeText.Text = "v" + CurrentVersion;

        // 全局通知宿主，任何页面都能直接 Toast.Success(...) / Toast.Error(...)
        Toast.Attach(ToastHost);

        ApplyWindowIcon();

        if (Content is FrameworkElement root)
        {
            // 主题变化（系统切换或应用内强制）时重新着色标题栏按钮
            root.ActualThemeChanged += (_, _) => ConfigureCaptionButtons();
        }

        ResizeAndCenter(1420, 1040);

        ApplyAppearance();
        NavigateToSection(AppSettings.Current.StartupSection);

        // 托盘图标要等窗口真正创建出来（有 HWND）才能挂
        Activated += (_, _) => EnsureTrayIcon();

        // 有下载在跑时，关窗 / 托盘退出前先弹确认，别把下载偷偷掐了
        AppWindow.Closing += OnAppWindowClosing;

        Closed += (_, _) =>
        {
            // 关掉主窗口就彻底退出：先收掉所有弹窗，否则它们会把进程吊着
            _trayIcon?.Dispose();
            _trayIcon = null;
            ToolWindow.CloseAll();
            Application.Current.Exit();
        };

        // 启动时探一次清单服务器：连上了就右上角提示（连不上不打扰）
        _ = CheckServerOnStartupAsync();
    }

    /// <summary>启动后做一次服务器连通性检查，成功时给一条右上角通知。</summary>
    private async Task CheckServerOnStartupAsync()
    {
        try
        {
            await Task.Delay(900);
            (bool ok, string message) = await CatalogServer.PingAsync();

            if (!ok)
            {
                return;
            }

            string where = Uri.TryCreate(CatalogServer.Url, UriKind.Absolute, out Uri? uri)
                ? uri.Authority
                : CatalogServer.Url;

            string detail = message.Replace("连接成功 · ", string.Empty, StringComparison.Ordinal);
            Toast.Success("已连接服务器", $"{where} · {detail}", 5);
        }
        catch
        {
            // 网络异常就当没连上，不弹任何东西
        }
    }

    /// <summary>关窗前检查是否有正在进行的下载；有就拦下并确认。</summary>
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
        {
            return;
        }

        int active = DownloadTracker.ActiveCount;
        if (active <= 0)
        {
            return;
        }

        // 先拦下这次关闭，等确认后再真正关
        args.Cancel = true;

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "下载尚未完成",
            Content = $"当前还有 {active} 个安装包正在下载，退出会中断。确定要退出吗？",
            PrimaryButtonText = "退出并取消下载",
            CloseButtonText = "继续下载",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            DownloadTracker.CancelAll();
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>任务栏 / Alt+Tab / 标题栏用的应用图标。</summary>
    private void ApplyWindowIcon()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

        if (System.IO.File.Exists(path))
        {
            AppWindow.SetIcon(path);
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null)
        {
            return;
        }

        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (!System.IO.File.Exists(path))
        {
            return;
        }

        _trayIcon = TrayIcon.Create(this, path, "Artk Tools", ShowFromTray, Close);
    }

    /// <summary>托盘「打开」：还原并前置主窗口。</summary>
    private void ShowFromTray()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>按当前设置应用主题、背景材质与标题栏配色。</summary>
    public void ApplyAppearance()
    {
        var settings = AppSettings.Current;

        RootGrid.RequestedTheme = settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        if (settings.UseMica)
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            SolidBackground.Visibility = Visibility.Collapsed;
        }
        else
        {
            SystemBackdrop = null;
            SolidBackground.Visibility = Visibility.Visible;
        }

        ConfigureCaptionButtons();
    }

    /// <summary>选中指定模块（windows / game / safe / settings）。</summary>
    public void NavigateToSection(string tag)
    {
        var item = FindNavItem(tag);
        if (item is null || ReferenceEquals(Nav.SelectedItem, item))
        {
            return;
        }

        Nav.SelectedItem = item;
    }

    private NavigationViewItem? FindNavItem(string tag)
    {
        foreach (object entry in Nav.MenuItems.Concat(Nav.FooterMenuItems))
        {
            if (entry is NavigationViewItem item && item.Tag is string itemTag && itemTag == tag)
            {
                return item;
            }
        }

        return null;
    }

    private void ConfigureCaptionButtons()
    {
        var yesDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var tb = AppWindow.TitleBar;

        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonHoverBackgroundColor = yesDark
            ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x12, 0x00, 0x00, 0x00);
        tb.ButtonPressedBackgroundColor = yesDark
            ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x1E, 0x00, 0x00, 0x00);
        tb.ButtonForegroundColor = yesDark ? Colors.White : Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);
        tb.ButtonInactiveForegroundColor = yesDark
            ? Color.FromArgb(0xFF, 0x8A, 0x8A, 0x96)
            : Color.FromArgb(0xFF, 0x70, 0x70, 0x76);
    }

    private void ResizeAndCenter(int width, int height)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;

        width = Math.Min(width, work.Width - 60);
        height = Math.Min(height, work.Height - 60);

        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2)));
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        Type page = tag switch
        {
            "windows" => typeof(WindowsPage),
            "software" => typeof(SoftwarePage),
            "game" => typeof(GamePage),
            "safe" => typeof(SafePage),
            "phone" => typeof(PhoneToolsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(WindowsPage),
        };

        if (ContentFrame.CurrentSourcePageType == page)
        {
            return;
        }

        ContentFrame.Navigate(page);
    }
}
