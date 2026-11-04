using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace WinUIDemo.Helpers;

/// <summary>
/// 统一动效入口。原则：
/// 1. 只动 Opacity 和 RenderTransform（合成层，不触发 Measure/Arrange）；
/// 2. 全局同一套时长与缓动，进场短、退场更短，悬停/按压近乎即时；
/// 3. 页面只做一次整体进入动画，不再给每个子元素排入场过渡，避免忽快忽慢和掉帧。
/// </summary>
public static class UiMotion
{
    /// <summary>页面进入：淡入 + 轻微上滑。</summary>
    public const int PageDuration = 180;

    /// <summary>区块展开。</summary>
    public const int RevealInDuration = 160;

    /// <summary>区块收起，退场要比进场快。</summary>
    public const int RevealOutDuration = 110;

    /// <summary>悬停缩放。</summary>
    public const int HoverDuration = 110;

    /// <summary>按下缩放，最快。</summary>
    public const int PressDuration = 70;

    private static readonly TimeSpan PageTime = TimeSpan.FromMilliseconds(PageDuration);
    private static readonly TimeSpan RevealInTime = TimeSpan.FromMilliseconds(RevealInDuration);
    private static readonly TimeSpan RevealOutTime = TimeSpan.FromMilliseconds(RevealOutDuration);

    // 每个元素的“当前动画代”，用于丢弃被后续调用抢占的收尾回调，避免展开/收起快速切换时闪烁
    private static readonly ConditionalWeakTable<FrameworkElement, StrongBox<int>> Tokens = new();

    /// <summary>页面进入：整页淡入 + 从下方 12px 滑入。每次切页调用一次，缓存的页面也能重放。</summary>
    public static void PageEnter(FrameworkElement target, double slide = 12)
    {
        if (target is null)
        {
            return;
        }

        TranslateTransform translate = EnsureTranslate(target);

        target.Opacity = 0;
        translate.Y = slide;

        var storyboard = new Storyboard();

        var fade = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(PageTime),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 },
        };
        Storyboard.SetTarget(fade, target);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var move = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(PageTime),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 },
        };
        Storyboard.SetTarget(move, translate);
        Storyboard.SetTargetProperty(move, "Y");

        storyboard.Children.Add(fade);
        storyboard.Children.Add(move);
        storyboard.Begin();
    }

    /// <summary>展开或收起一个区块：淡入淡出 + 轻微上下滑动；收起在动画结束后才真正 Collapsed。</summary>
    public static void Reveal(FrameworkElement target, bool show, double slide = 8)
    {
        if (target is null)
        {
            return;
        }

        TranslateTransform translate = EnsureTranslate(target);

        StrongBox<int> token = Tokens.GetValue(target, static _ => new StrongBox<int>(0));
        int current = ++token.Value;

        var storyboard = new Storyboard();

        if (show)
        {
            target.Visibility = Visibility.Visible;
            target.Opacity = 0;
            translate.Y = -slide;

            var fade = new DoubleAnimation
            {
                To = 1,
                Duration = new Duration(RevealInTime),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 },
            };
            Storyboard.SetTarget(fade, target);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var move = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(RevealInTime),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 },
            };
            Storyboard.SetTarget(move, translate);
            Storyboard.SetTargetProperty(move, "Y");

            storyboard.Children.Add(fade);
            storyboard.Children.Add(move);
        }
        else
        {
            translate.Y = 0;

            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(RevealOutTime),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            Storyboard.SetTarget(fade, target);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var move = new DoubleAnimation
            {
                To = -slide,
                Duration = new Duration(RevealOutTime),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            Storyboard.SetTarget(move, translate);
            Storyboard.SetTargetProperty(move, "Y");

            fade.Completed += (_, _) =>
            {
                // 期间若已被新的展开/收起抢占，就别再收尾了
                if (token.Value != current)
                {
                    return;
                }

                target.Visibility = Visibility.Collapsed;
                target.Opacity = 1;
                translate.Y = 0;
            };

            storyboard.Children.Add(fade);
            storyboard.Children.Add(move);
        }

        storyboard.Begin();
    }

    public static void ScaleTo(FrameworkElement target, double scale) =>
        ScaleTo(target, scale, HoverDuration);

    /// <summary>缩放反馈：只改 RenderTransform，不触发布局。元素需有 ScaleTransform（没有会自动补一个，并以中心缩放）。</summary>
    public static void ScaleTo(FrameworkElement target, double scale, int milliseconds)
    {
        if (target is null)
        {
            return;
        }

        ScaleTransform transform = EnsureScale(target);

        var storyboard = new Storyboard();

        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        var scaleX = new DoubleAnimation
        {
            To = scale,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = easing,
        };
        Storyboard.SetTarget(scaleX, transform);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");

        var scaleY = new DoubleAnimation
        {
            To = scale,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = easing,
        };
        Storyboard.SetTarget(scaleY, transform);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");

        storyboard.Children.Add(scaleX);
        storyboard.Children.Add(scaleY);
        storyboard.Begin();
    }

    private static TranslateTransform EnsureTranslate(FrameworkElement target)
    {
        if (target.RenderTransform is TranslateTransform translate)
        {
            return translate;
        }

        translate = new TranslateTransform();
        target.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        target.RenderTransform = translate;
        return translate;
    }

    private static ScaleTransform EnsureScale(FrameworkElement target)
    {
        if (target.RenderTransform is ScaleTransform scale)
        {
            return scale;
        }

        scale = new ScaleTransform();
        target.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        target.RenderTransform = scale;
        return scale;
    }
}
