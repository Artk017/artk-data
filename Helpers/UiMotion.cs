using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace WinUIDemo.Helpers;

/// <summary>
/// 轻量动画辅助。原则：只动 Opacity 和 RenderTransform（两者都在合成层，不改布局），
/// 尽量不触发重新 Measure/Arrange，避免动画期间整页重排。
/// </summary>
public static class UiMotion
{
    // 每个元素的“当前动画代”，用于丢弃被后续调用抢占的收尾回调，避免展开/收起快速切换时闪烁
    private static readonly ConditionalWeakTable<FrameworkElement, StrongBox<int>> Tokens = new();

    /// <summary>展开或收起一个区块：淡入淡出 + 轻微上下滑动；收起在动画结束后才真正 Collapsed。</summary>
    public static void Reveal(FrameworkElement target, bool show, double slide = 10)
    {
        if (target is null)
        {
            return;
        }

        if (target.RenderTransform is not TranslateTransform translate)
        {
            translate = new TranslateTransform();
            target.RenderTransform = translate;
        }

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
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, target);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var move = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
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
                Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            Storyboard.SetTarget(fade, target);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var move = new DoubleAnimation
            {
                To = -slide,
                Duration = new Duration(TimeSpan.FromMilliseconds(160)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
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

    /// <summary>悬停缩放：只改 RenderTransform，不触发布局。元素需有 ScaleTransform（没有会自动补一个，并以中心缩放）。</summary>
    public static void ScaleTo(FrameworkElement target, double scale, int milliseconds = 130)
    {
        if (target is null)
        {
            return;
        }

        if (target.RenderTransform is not ScaleTransform transform)
        {
            transform = new ScaleTransform();
            target.RenderTransform = transform;
            target.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        }

        var storyboard = new Storyboard();

        var scaleX = new DoubleAnimation
        {
            To = scale,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(scaleX, transform);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");

        var scaleY = new DoubleAnimation
        {
            To = scale,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(scaleY, transform);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");

        storyboard.Children.Add(scaleX);
        storyboard.Children.Add(scaleY);
        storyboard.Begin();
    }
}
