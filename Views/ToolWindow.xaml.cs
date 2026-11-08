using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ArtkTools.Helpers;
using Windows.Graphics;
using Windows.UI;

namespace ArtkTools.Views;

/// <summary>
/// 所有工具弹窗共用的外壳：自绘标题栏 + 图钉（置顶）+ 可缩放 + 记住尺寸 + 打开时置前。
/// 内容由调用方以 <see cref="UIElement"/> 传入，通常是一个 UserControl。
///
/// 用法（页面里只需要一个槽位字段 + 一次 Show）：
/// <code>
/// private readonly ToolWindowSlot _info = new("系统信息", "\uE946", new SizeInt32(480, 285), () => new SystemInfoView());
/// _info.Show();                       // 居中
/// _info.Show(screenPoint);            // 贴着点击处
/// </code>
/// </summary>
public sealed partial class ToolWindow : Window
{
    // 同一个标题记住上次调整过的尺寸；置顶状态全局共用
    private static readonly Dictionary<string, SizeInt32> RememberedSizes = new();

    // 所有活着的弹窗：主窗口关闭时要一起收掉，否则进程会被它们吊着不退出
    private static readonly List<ToolWindow> Live = new();

    private static bool _lastPinned;

    private readonly string _sizeKey;
    private readonly PointInt32? _anchor;
    private readonly WindowSizeLimits _limits;
    private bool _placed;
    private bool _pinned;

    /// <summary>调用方没指定时的最小尺寸（DIP）。</summary>
    private static readonly SizeInt32 DefaultMinSize = new(320, 200);

    /// <summary>窗口关闭后触发，调用方据此释放引用 / 恢复主窗口。</summary>
    public event EventHandler? WindowClosed;

    /// <param name="anchor">左上角的目标位置（屏幕物理像素）；为空则居中于主窗口。</param>
    /// <param name="minSize">最小尺寸（DIP）；为空时用 <see cref="DefaultMinSize"/>。</param>
    public ToolWindow(string title, string glyph, UIElement content, SizeInt32 size, PointInt32? anchor = null, SizeInt32? minSize = null)
    {
        InitializeComponent();

        Live.Add(this);

        _sizeKey = title;
        _anchor = anchor;

        Title = title;
        TitleText.Text = title;
        TitleIcon.Glyph = glyph;
        Host.Content = content;

        // 最小 / 最大尺寸只能在窗口过程里限制，要在 Resize 之前挂上
        SizeInt32 min = minSize ?? DefaultMinSize;
        _limits = WindowSizeLimits.Apply(WinRT.Interop.WindowNative.GetWindowHandle(this), min.Width, min.Height);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(InitialSize(size));

        // 和「进程占用」一致：可缩放，但没有最大化按钮
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
        }

        ApplyPin(_lastPinned);

        // XamlRoot 到位后才知道缩放比，标题栏留白要在 Loaded 和尺寸变化时再校正
        RootGrid.Loaded += (_, _) =>
        {
            UpdateTitleBarInsets();
            DispatcherQueue.TryEnqueue(EnsureOnScreen);
        };

        ApplyCaptionColors();
        RootGrid.ActualThemeChanged += (_, _) => ApplyCaptionColors();
        UpdateTitleBarInsets();

        // 首次激活时系统会按默认位置摆放窗口，所以定位必须放在激活之后
        Activated += (_, _) =>
        {
            if (_placed)
            {
                return;
            }

            _placed = true;
            Place();
            DispatcherQueue.TryEnqueue(EnsureOnScreen);

            // 刚出现时压到最前
            WindowFocus.BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(this));
        };

        AppWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange)
            {
                DispatcherQueue.TryEnqueue(EnsureOnScreen);
            }

            // DPI 变化或标题栏按钮数量变化都会改变 RightInset
            UpdateTitleBarInsets();
        };

        AppWindow.Closing += (_, _) =>
        {
            RememberedSizes[_sizeKey] = AppWindow.Size;
            _limits.Dispose();
            Live.Remove(this);
            WindowClosed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>关掉所有还开着的弹窗（主窗口关闭 / 应用退出时调用）。</summary>
    public static void CloseAll()
    {
        foreach (var window in Live.ToArray())
        {
            window.Close();
        }

        Live.Clear();
    }

    /// <summary>打开或复用弹窗：已开着就提到前面，否则新建。返回当前实例。</summary>
    public static ToolWindow Show(ToolWindowSlot slot, PointInt32? anchor = null)
    {
        return slot.Show(anchor);
    }

    // ---------------- 尺寸 / 定位 ----------------

    private SizeInt32 InitialSize(SizeInt32 fallback)
    {
        var work = ScreenPlacement.GetWorkArea(0, 0);
        var size = RememberedSizes.TryGetValue(_sizeKey, out var remembered) ? remembered : fallback;

        // 记下来的尺寸可能来自加限制之前（比如被拖得很窄），这里先夹回最小尺寸
        SizeInt32 min = _limits.MinPixels;
        if (work.Width <= 0)
        {
            return new SizeInt32(Math.Max(size.Width, min.Width), Math.Max(size.Height, min.Height));
        }

        return new SizeInt32(
            Math.Clamp(size.Width, min.Width, Math.Max(min.Width, work.Width)),
            Math.Clamp(size.Height, min.Height, Math.Max(min.Height, work.Height)));
    }

    /// <summary>有锚点就贴着它，否则居中于主窗口。</summary>
    private void Place()
    {
        if (_anchor is PointInt32 point)
        {
            MoveNear(point);
            return;
        }

        if (MainWindow.Instance is null)
        {
            return;
        }

        var size = AppWindow.Size;
        var host = MainWindow.Instance.AppWindow;

        AppWindow.Move(new PointInt32(
            host.Position.X + Math.Max(0, (host.Size.Width - size.Width) / 2),
            host.Position.Y + Math.Max(0, (host.Size.Height - size.Height) / 2)));
    }

    /// <summary>把窗口移到指定屏幕坐标旁边（已显示时也可调用）。</summary>
    public void MoveNear(PointInt32 point)
    {
        var work = ScreenPlacement.GetWorkArea(point.X, point.Y);
        if (work.Width <= 0)
        {
            return;
        }

        var size = AppWindow.Size;
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        int offset = (int)Math.Round(14 * scale);

        AppWindow.Move(new PointInt32(
            Math.Clamp(point.X + offset, work.Left, Math.Max(work.Left, work.Left + work.Width - size.Width)),
            Math.Clamp(point.Y + offset, work.Top, Math.Max(work.Top, work.Top + work.Height - size.Height))));
    }

    /// <summary>保证窗口完整落在当前屏幕工作区内。</summary>
    private void EnsureOnScreen()
    {
        var size = AppWindow.Size;
        var position = AppWindow.Position;
        var work = ScreenPlacement.GetWorkArea(position.X + size.Width / 2, position.Y + size.Height / 2);

        if (work.Width <= 0)
        {
            return;
        }

        int maxX = Math.Max(work.Left, work.Left + work.Width - size.Width);
        int maxY = Math.Max(work.Top, work.Top + work.Height - size.Height);

        int x = Math.Clamp(position.X, work.Left, maxX);
        int y = Math.Clamp(position.Y, work.Top, maxY);

        if (x != position.X || y != position.Y)
        {
            AppWindow.Move(new PointInt32(x, y));
        }
    }

    // ---------------- 标题栏外观 ----------------

    /// <summary>
    /// 按系统 caption button 的实际宽度给标题栏留出右侧空间。
    /// RightInset 是物理像素，要除以缩放比换成布局用的 DIP。
    /// </summary>
    private void UpdateTitleBarInsets()
    {
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 0;
        if (scale <= 0 || !AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        double inset = AppWindow.TitleBar.RightInset;
        if (inset <= 0)
        {
            return;
        }

        double right = (inset / scale) + 10;

        if (Math.Abs(TitleBar.Padding.Right - right) > 0.5)
        {
            TitleBar.Padding = new Thickness(14, 0, right, 0);
        }
    }

    private void ApplyCaptionColors()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var bar = AppWindow.TitleBar;
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;

        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonHoverBackgroundColor = dark
            ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x12, 0x00, 0x00, 0x00);
        bar.ButtonPressedBackgroundColor = dark
            ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x1E, 0x00, 0x00, 0x00);
        bar.ButtonForegroundColor = dark ? Colors.White : Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);
        bar.ButtonInactiveForegroundColor = dark
            ? Color.FromArgb(0xFF, 0x8A, 0x8A, 0x96)
            : Color.FromArgb(0xFF, 0x70, 0x70, 0x76);
    }

    // ---------------- 固定在最前 ----------------

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        ApplyPin(!_pinned);
    }

    private void ApplyPin(bool pinned)
    {
        _pinned = pinned;
        _lastPinned = pinned;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = pinned;
        }

        PinIcon.Glyph = pinned ? "\uE77A" : "\uE718";
        ToolTipService.SetToolTip(PinButton, pinned ? "取消固定" : "固定在最前");

        if (pinned && RootGrid.Resources.TryGetValue("PinActiveBrush", out object resource)
            && resource is Brush accent)
        {
            PinButton.Background = accent;
            PinIcon.Foreground = new SolidColorBrush(Colors.White);
        }
        else
        {
            PinButton.ClearValue(Control.BackgroundProperty);
            PinIcon.ClearValue(IconElement.ForegroundProperty);
        }
    }
}
