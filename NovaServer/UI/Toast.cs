using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NovaServer;

/// <summary>窗口右上角的浮动通知，和客户端 Artk Tools 一致。</summary>
internal static class Toast
{
    private static Panel? _host;

    public static void Attach(Panel host) => _host = host;

    public static void Success(string title, string message = "", int seconds = 4)
        => Show(title, message, Color.FromArgb(255, 0x52, 0xC4, 0x7E), seconds);

    public static void Error(string title, string message = "", int seconds = 7)
        => Show(title, message, Color.FromArgb(255, 0xE8, 0x5E, 0x5E), seconds);

    private static void Show(string title, string message, Color accent, int seconds)
    {
        Panel? host = _host;

        if (host is null || host.DispatcherQueue is null)
        {
            return;
        }

        host.DispatcherQueue.TryEnqueue(() => ShowCore(host, title, message, accent, seconds));
    }

    private static void ShowCore(Panel host, string title, string message, Color accent, int seconds)
    {
        bool dark = host.ActualTheme == ElementTheme.Dark;

        Color text = dark ? Color.FromArgb(255, 0xEE, 0xF1, 0xF7) : Color.FromArgb(255, 0x14, 0x16, 0x1C);
        Color subtle = dark ? Color.FromArgb(255, 0xA2, 0xA9, 0xB8) : Color.FromArgb(255, 0x5D, 0x64, 0x72);

        var icon = new FontIcon
        {
            Glyph = "\uE930",
            FontSize = 15,
            Foreground = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var texts = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(10, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 300,
        };

        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(text),
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(message))
        {
            texts.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 12,
                Foreground = new SolidColorBrush(subtle),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var layout = new Grid { ColumnSpacing = 0 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        layout.Children.Add(icon);
        Grid.SetColumn(texts, 1);
        layout.Children.Add(texts);

        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 9 },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(7, 3, 7, 3),
            VerticalAlignment = VerticalAlignment.Top,
        };

        Grid.SetColumn(close, 2);
        layout.Children.Add(close);

        var border = new Border
        {
            Background = new SolidColorBrush(dark
                ? Color.FromArgb(0xE6, 0x20, 0x22, 0x28)
                : Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(dark
                ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x1A, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(1, 1, 1, 3),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(13, 10, 5, 10),
            MinWidth = 220,
            MaxWidth = 360,
            Child = layout,
        };

        // 左侧用强调色描边，成功 / 失败一眼能分开
        border.BorderBrush = new SolidColorBrush(accent);

        close.Click += (_, _) => Dismiss(border);
        host.Children.Insert(0, border);

        if (seconds > 0)
        {
            DispatcherQueueTimer timer = host.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(seconds);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Dismiss(border);
            };
            timer.Start();
        }
    }

    private static void Dismiss(Border border)
    {
        Panel? host = _host;

        if (host is not null && host.Children.Contains(border))
        {
            host.Children.Remove(border);
        }
    }
}
