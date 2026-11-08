using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace ArtkTools.Services;

public enum ToastKind
{
    Success,
    Error,
    Info,
}

/// <summary>
/// 窗口右上角的浮动通知（原手机工具页那套样式）。
/// 启动时在主窗口上 <see cref="Attach"/> 一次，之后任何页面 / 任何地方都能直接：
/// <code>
/// Toast.Success("安装成功", "微信");
/// Toast.Error("下载失败", ex.Message);
/// Border? busy = Toast.Busy("正在安装", "微信");   // 不自动消失，用 Toast.Dismiss(busy) 收尾
/// </code>
/// </summary>
public static class Toast
{
    private static readonly List<(Border Element, DispatcherQueueTimer Timer)> Items = new();
    private static Panel? _host;

    /// <summary>挂到主窗口的宿主面板上（一个窗口只需调用一次）。</summary>
    public static void Attach(Panel host)
    {
        _host = host;
    }

    /// <summary>绿勾通知，默认 4 秒消失。</summary>
    public static Border? Success(string title, string message = "", int autoCloseSeconds = 4)
        => Show(title, message, ToastKind.Success, autoCloseSeconds);

    /// <summary>红色失败通知，默认 8 秒消失（内容通常更长）。</summary>
    public static Border? Error(string title, string message = "", int autoCloseSeconds = 8)
        => Show(title, message, ToastKind.Error, autoCloseSeconds);

    /// <summary>普通提示，默认 4 秒消失。</summary>
    public static Border? Info(string title, string message = "", int autoCloseSeconds = 4)
        => Show(title, message, ToastKind.Info, autoCloseSeconds);

    /// <summary>进行中提示：不自动消失，办完记得 <see cref="Dismiss"/>。</summary>
    public static Border? Busy(string title, string message = "")
        => Show(title, message, ToastKind.Info, 0);

    /// <summary>弹出一条通知；样式由 <paramref name="kind"/> 决定。autoCloseSeconds 传 0 表示不自动关。</summary>
    public static Border? Show(string title, string message, ToastKind kind, int autoCloseSeconds)
    {
        Panel? host = _host;

        if (host is null)
        {
            return null;
        }

        DispatcherQueue queue = host.DispatcherQueue;

        if (queue is null)
        {
            return null;
        }

        if (!queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => Show(title, message, kind, autoCloseSeconds));
            return null;
        }

        (string glyph, Windows.UI.Color color) = kind switch
        {
            ToastKind.Success => ("\uE930", Windows.UI.Color.FromArgb(255, 82, 196, 126)),
            ToastKind.Error => ("\uEA39", Windows.UI.Color.FromArgb(255, 232, 94, 94)),
            _ => ("\uE946", Windows.UI.Color.FromArgb(255, 120, 170, 255)),
        };

        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(color),
        };

        var texts = new StackPanel
        {
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(message))
        {
            var body = new TextBlock
            {
                Text = message,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300,
                Margin = new Thickness(0, 2, 0, 0),
                Opacity = 0.85,
            };

            // 长文本（比如 reg 应用结果）限制高度、内部可滚动，别把通知撑满整个屏幕
            texts.Children.Add(new ScrollViewer
            {
                Content = body,
                MaxHeight = 132,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Auto,
            });
        }

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        layout.Children.Add(icon);
        Grid.SetColumn(texts, 1);
        layout.Children.Add(texts);

        var border = new Border
        {
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush", Windows.UI.Color.FromArgb(255, 32, 32, 32)),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush", Windows.UI.Color.FromArgb(255, 70, 70, 70)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 8, 10),
            MinWidth = 220,
            MaxWidth = 380,
            Opacity = 0,
            Child = layout,
        };

        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 9 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(7, 3, 7, 3),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 0, 0, 0),
        };

        close.Click += (_, _) => Dismiss(border);
        Grid.SetColumn(close, 2);
        layout.Children.Add(close);

        host.Children.Insert(0, border);
        AnimateOpacity(border, 0, 1, 160);

        if (autoCloseSeconds > 0)
        {
            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(autoCloseSeconds);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => Dismiss(border);
            timer.Start();
            Items.Add((border, timer));
        }

        return border;
    }

    /// <summary>关掉一条通知（传 null 安全）。</summary>
    public static void Dismiss(Border? toast)
    {
        if (toast is null || _host is null || !_host.Children.Contains(toast))
        {
            return;
        }

        foreach ((Border Element, DispatcherQueueTimer Timer) item in Items.Where(t => t.Element == toast).ToList())
        {
            item.Timer.Stop();
            Items.Remove(item);
        }

        AnimateOpacity(toast, 1, 0, 140, () => _host.Children.Remove(toast));
    }

    /// <summary>一次清空所有通知（切换页面时用得上）。</summary>
    public static void Clear()
    {
        if (_host is null)
        {
            return;
        }

        foreach ((Border Element, DispatcherQueueTimer Timer) item in Items.ToList())
        {
            item.Timer.Stop();
            Items.Remove(item);
        }

        _host.Children.Clear();
    }

    private static void AnimateOpacity(UIElement element, double from, double to, int milliseconds, Action? completed = null)
    {
        element.Opacity = from;

        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EnableDependentAnimation = true,
        };

        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);

        if (completed is not null)
        {
            storyboard.Completed += (_, _) => completed();
        }

        storyboard.Begin();
    }

    private static Brush ThemeBrush(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }
}
