using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using ArtkTools.Helpers;
using ArtkTools.Services;
using ArtkTools.Views;
using Windows.Graphics;
using Windows.UI;

namespace ArtkTools;

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

    /// <summary>已经进入退出流程，重复点 X 直接吞掉。</summary>
    private bool _closingStarted;

    /// <summary>收尾已经做完，此时我们自己调的 Close() 不再拦截。</summary>
    private bool _teardownDone;

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

        // 每次切页统一走一套「淡入 + 上滑」，缓存的页面也不会出现有的动有的不动
        ContentFrame.Navigated += OnContentNavigated;
        NavigateToSection(AppSettings.Current.StartupSection);

        // 托盘图标要等窗口真正创建出来（有 HWND）才能挂
        Activated += (_, _) => EnsureTrayIcon();

        // 有下载在跑时，关窗 / 托盘退出前先弹确认，别把下载偷偷掐了
        AppWindow.Closing += OnAppWindowClosing;

        Closed += (_, _) =>
        {
            // 正常退出时收尾已经在 CloseSequenceAsync 里做完了，这里只做兜底
            // （比如系统关机直接销毁窗口）：关掉托盘和弹窗，否则它们会把进程吊着
            if (!_teardownDone)
            {
                _trayIcon?.Dispose();
                _trayIcon = null;
                ToolWindow.CloseAll();
                AddSoftwareWindow.CloseAll();
            }

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

    /// <summary>
    /// 关窗前先拦一次：有下载就确认，然后走退出流程。
    /// 关键点：窗口先播 160ms 退场动画并立刻隐藏，收托盘 / 关弹窗 / 停 adb server
    /// 这些会卡的活全部放在窗口消失之后，用户看不到任何停顿。
    /// </summary>
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // 收尾做完后我们自己的 Close() 放行
        if (_teardownDone)
        {
            return;
        }

        // 第一次关闭先拦住，退出流程走完再调 Close()
        args.Cancel = true;

        if (_closingStarted)
        {
            return;
        }

        int active = DownloadTracker.ActiveCount;
        if (!_closeConfirmed && active > 0)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "下载尚未完成",
                Content = $"当前还有 {active} 个安装包正在下载，退出会中断。确定要退出吗？",
                PrimaryButtonText = "退出并取消下载",
                CloseButtonText = "继续下载",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            DownloadTracker.CancelAll();
            _closeConfirmed = true;
        }

        _closingStarted = true;
        await CloseSequenceAsync();
    }

    /// <summary>
    /// 退出顺序：退场动画 → 窗口隐藏 → 窗口之外做收尾 → 真正关闭。
    /// adb kill-server 最多要等 2 秒，放在窗口隐藏之后跑，不再表现为「点 X 卡一下」。
    /// </summary>
    private async Task CloseSequenceAsync()
    {
        await PlayCloseAnimationAsync();

        // 动画播完直接把窗口从屏幕上收走（任务栏 / Alt+Tab 里也一起消失）
        AppWindow.Hide();

        // 下面几步都可能卡：托盘、工具弹窗、添加软件弹窗
        _trayIcon?.Dispose();
        _trayIcon = null;
        ToolWindow.CloseAll();
        AddSoftwareWindow.CloseAll();

        // 结束 adb / fastboot 子进程并 kill-server，最多等 2 秒，交给线程池
        await Task.Run(PhoneTools.Shutdown);

        _teardownDone = true;
        Close();
    }

    /// <summary>退场动画：淡出并轻微缩小，160ms。</summary>
    private async Task PlayCloseAnimationAsync()
    {
        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(RootGrid);
            Compositor compositor = visual.Compositor;
            var easing = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.3f, 0f), new Vector2(0.8f, 0.5f));

            visual.CenterPoint = new Vector3(
                (float)RootGrid.ActualWidth / 2,
                (float)RootGrid.ActualHeight / 2,
                0f);

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 1f);
            fade.InsertKeyFrame(1f, 0f, easing);
            fade.Duration = TimeSpan.FromMilliseconds(160);

            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0f, Vector3.One);
            scale.InsertKeyFrame(1f, new Vector3(0.97f, 0.97f, 1f), easing);
            scale.Duration = TimeSpan.FromMilliseconds(160);

            visual.StartAnimation("Opacity", fade);
            visual.StartAnimation("Scale", scale);

            await Task.Delay(170);
        }
        catch
        {
            // 动画失败也要继续关闭
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

    private void OnContentNavigated(object sender, NavigationEventArgs e)
    {
        if (e.Content is FrameworkElement page)
        {
            UiMotion.PageEnter(page);
        }
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
            "clean" => typeof(JunkCleanPage),
            "software" => typeof(SoftwarePage),
            "game" => typeof(GamePage),
            "safe" => typeof(SafePage),
            "phone" => typeof(PhoneToolsPage),
            "tools" => typeof(ComputerToolsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(WindowsPage),
        };

        if (ContentFrame.CurrentSourcePageType == page)
        {
            return;
        }

        ContentFrame.Navigate(page);
    }

    /// <summary>
    /// 点击已经选中的「电脑工具」时 SelectionChanged 不会触发，
    /// 这里处理「正在某个工具子页 → 点电脑工具回到卡片页」。
    /// </summary>
    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item || item.Tag is not string tag || tag != "tools")
        {
            return;
        }

        var current = ContentFrame.CurrentSourcePageType;

        if (current == typeof(RuntimeRepairPage) ||
            current == typeof(ContextMenuPage) ||
            current == typeof(DotnetPage) ||
            current == typeof(DiskHealthPage))
        {
            ContentFrame.Navigate(typeof(ComputerToolsPage));
        }
    }
}
