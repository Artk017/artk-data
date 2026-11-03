using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinUIDemo.Helpers;
using WinUIDemo.Services;
using Windows.Foundation;
using Windows.Graphics;

namespace WinUIDemo.Views;

public sealed partial class WindowsPage : Page
{
    private const int ChartSampleCount = 60;

    // 悬停提示相对光标的偏移，避免盖住指针本身
    private const double HintOffsetX = 14;
    private const double HintOffsetY = 18;

    // 每秒采样一次，保留最近 60 个点
    private readonly List<double> _cpuHistory = new();

    private SystemMonitor? _monitor;
    private DispatcherQueueTimer? _metricsTimer;

    // 三个弹窗共用同一套外壳，这里各自占一个槽位（同一时刻最多一个）
    private readonly ToolWindowSlot _detail = new(
        "进程占用", "\uE9D9", new SizeInt32(420, 320), () => new ProcessDetailView(),
        new SizeInt32(360, 260));

    private readonly ToolWindowSlot _settings = new(
        "系统设置", "\uE713", new SizeInt32(470, 660), () => new SystemSettingsView(),
        new SizeInt32(380, 340));

    private readonly ToolWindowSlot _info = new(
        "系统信息", "\uE946", new SizeInt32(480, 285), () => new SystemInfoView(),
        new SizeInt32(400, 220));

    private readonly ToolWindowSlot _vocab = new(
        "微软词汇", "\uE82D", new SizeInt32(470, 380), () => new PinyinVocabView(),
        new SizeInt32(400, 320));

    public WindowsPage()
    {
        InitializeComponent();

        // 进程占用弹窗自己在采样，开着时主页面停表，关掉后交还
        _detail.Closed += (_, _) => StartMetrics();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        StartMetrics();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        HideHint();
        StopMetrics();
    }

    // ---------------- 图表悬停 / 点击 ----------------

    /// <summary>只要指针在图表内移动就更新提示位置；离开图表则隐藏。</summary>
    private void ChartHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(ChartHost).Position;
        ShowHint(position);
    }

    private void ChartHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        HideHint();
    }

    /// <summary>左键单击图表打开进程详情窗口，窗口出现在点击处。</summary>
    private void ChartHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ChartHost);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        HideHint();

        OpenDetailWindow(GetScreenPoint(point.Position));
    }

    /// <summary>把图表内坐标换算成屏幕物理像素，供弹窗定位。</summary>
    private PointInt32? GetScreenPoint(Point positionInChart)
    {
        var window = MainWindow.Instance;
        if (window is null || XamlRoot?.Content is not UIElement root)
        {
            return null;
        }

        var inRoot = ChartHost.TransformToVisual(root).TransformPoint(positionInChart);
        double scale = XamlRoot.RasterizationScale;
        var origin = window.AppWindow.Position;

        return new PointInt32(
            origin.X + (int)Math.Round(inRoot.X * scale),
            origin.Y + (int)Math.Round(inRoot.Y * scale));
    }

    private void ShowHint(Point position)
    {
        HoverHint.Visibility = Visibility.Visible;

        double hostWidth = ChartHost.ActualWidth;
        double hostHeight = ChartHost.ActualHeight;

        // 用已排布好的实际尺寸；首帧尚未布局时按 0 处理，下一次移动会自动校正
        double hintWidth = HoverHint.ActualWidth;
        double hintHeight = HoverHint.ActualHeight;

        double x = position.X + HintOffsetX;
        double y = position.Y + HintOffsetY;

        // 越界时贴边收敛即可。不做“整块翻到光标另一侧”，否则光标微动跨过阈值时提示会突然跳很远。
        if (hintWidth > 0 && x + hintWidth > hostWidth)
        {
            x = hostWidth - hintWidth - 4;
        }

        if (hintHeight > 0 && y + hintHeight > hostHeight)
        {
            y = hostHeight - hintHeight - 4;
        }

        // RenderTransform 只改合成层，不触发 Measure/Arrange，位置严格跟随光标
        HintTransform.X = Math.Round(Math.Max(x, 0));
        HintTransform.Y = Math.Round(Math.Max(y, 0));
    }

    private void HideHint()
    {
        HoverHint.Visibility = Visibility.Collapsed;
    }

    private void OpenDetailWindow(PointInt32? anchor)
    {
        // 已开着：提到前面并按新的点击位置重新摆放
        bool wasOpen = _detail.IsOpen;
        var window = _detail.Show(anchor);

        if (wasOpen)
        {
            return;
        }

        // 弹窗自己也在采样，主窗口先停表，避免两处重复枚举进程
        StopMetrics();
        window.Activate();
    }

    // ---------------- 实时指标 ----------------

    private void StartMetrics()
    {
        StopMetrics();

        _monitor = new SystemMonitor();
        _monitor.Sample(); // 建立 CPU / 带宽基线

        _metricsTimer = DispatcherQueue.CreateTimer();
        _metricsTimer.Interval = TimeSpan.FromSeconds(1);
        _metricsTimer.IsRepeating = true;
        _metricsTimer.Tick += (_, _) => RefreshMetrics();
        _metricsTimer.Start();

        RefreshMetrics();
    }

    private void StopMetrics()
    {
        _metricsTimer?.Stop();
        _metricsTimer = null;

        _monitor?.Dispose();
        _monitor = null;
    }

    private void RefreshMetrics()
    {
        if (_monitor is null)
        {
            return;
        }

        var snapshot = _monitor.Sample();

        CpuValueText.Text = $"{snapshot.CpuPercent:0}%";
        CpuBar.Value = snapshot.CpuPercent;
        CpuMetaText.Text = $"{Environment.ProcessorCount} 个逻辑处理器";

        MemoryValueText.Text = $"{snapshot.MemoryPercent:0}%";
        MemoryBar.Value = snapshot.MemoryPercent;
        MemoryMetaText.Text = $"{snapshot.MemoryUsedGb:0.0} GB / {snapshot.MemoryTotalGb:0.0} GB";

        GpuValueText.Text = $"{snapshot.GpuPercent:0}%";
        GpuBar.Value = snapshot.GpuPercent;
        GpuMetaText.Text = string.IsNullOrEmpty(snapshot.GpuBusiestEngine)
            ? "没有可用的 GPU 计数器"
            : $"最忙：{snapshot.GpuBusiestEngine} 引擎";

        double down = snapshot.NetworkDownBytesPerSecond;
        double up = snapshot.NetworkUpBytesPerSecond;

        var (total, unit) = ToBitRate(down + up);
        double scale = unit == "兆比特/秒" ? 1_000_000 : 1_000;

        NetworkValueText.Text = Number(total);
        NetworkUnitText.Text = unit;
        NetworkMetaText.Text = $"↑ {Number(up * 8 / scale)} · ↓ {Number(down * 8 / scale)} {unit}";

        NetworkBar.Value = snapshot.NetworkLinkBitsPerSecond > 0
            ? Math.Clamp((down + up) * 8 / snapshot.NetworkLinkBitsPerSecond * 100, 0, 100)
            : 0;

        // CPU 曲线：滚动窗口，最近 60 个采样点
        _cpuHistory.Add(snapshot.CpuPercent);
        if (_cpuHistory.Count > ChartSampleCount)
        {
            _cpuHistory.RemoveAt(0);
        }

        DrawChart();
    }

    /// <summary>把字节/秒换算成中文单位的比特率。</summary>
    private static (double Value, string Unit) ToBitRate(double bytesPerSecond)
    {
        double bits = bytesPerSecond * 8;
        return bits >= 1_000_000
            ? (bits / 1_000_000, "兆比特/秒")
            : (bits / 1_000, "千比特/秒");
    }

    private static string Number(double value)
    {
        return value >= 100 ? value.ToString("0") : value.ToString("0.0");
    }


    // ---------------- 分区折叠 ----------------

    private bool _chartExpanded = true;

    private void ChartSection_Click(object sender, RoutedEventArgs e)
    {
        _chartExpanded = !_chartExpanded;
        UiMotion.Reveal(ChartBody, _chartExpanded);
        RotateChevron(ChartChevronRotate, _chartExpanded);
    }

    /// <summary>折叠指示箭头：收起时朝下（0°），展开时翻转 180°。</summary>
    private static void RotateChevron(RotateTransform transform, bool expanded)
    {
        var animation = new DoubleAnimation
        {
            To = expanded ? 180 : 0,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };

        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "Angle");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    // ---------------- 系统工具 ----------------

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => _settings.Show();

    private void OpenSystemInfo_Click(object sender, RoutedEventArgs e) => _info.Show();

    private void OpenPinyinVocab_Click(object sender, RoutedEventArgs e) => _vocab.Show();

    private async void FileLocations_Click(object sender, RoutedEventArgs e)
    {
        // 更改 下载 / 文档 / 图片 / 视频 的存放位置（等同属性里的「位置 → 移动」）
        await FileLocationsDialog.ShowAsync(XamlRoot);
    }

    // ---------------- 图表 ----------------

    private void ChartHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawChart();
    }

    private void DrawChart()
    {
        double width = ChartHost.ActualWidth;
        double height = ChartHost.ActualHeight;

        if (_cpuHistory.Count < 2 || width < 20 || height < 20)
        {
            ChartLinePath.Data = null;
            ChartAreaPath.Data = null;
            LastPointDot.Visibility = Visibility.Collapsed;
            return;
        }

        var points = ChartGeometry.Project(_cpuHistory, width, height, min: 0, max: 100);

        ChartLinePath.Data = ChartGeometry.BuildCurve(points);
        ChartAreaPath.Data = ChartGeometry.BuildCurve(points, height);

        LastPointDot.Visibility = Visibility.Visible;
        LastPointDot.Margin = new Thickness(points[^1].X - 4.5, points[^1].Y - 4.5, 0, 0);
    }
}
