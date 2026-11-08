using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ArtkTools.Helpers;

namespace ArtkTools.Views;

/// <summary>
/// 电脑工具入口：五个工具卡片，前四个进入对应工具页面（外层 ContentFrame 导航），
/// 最后一张「Geek 卸载」直接启动随程序分发的 geek.exe。
/// </summary>
public sealed partial class ComputerToolsPage : Page
{
    private bool _geekIconLoaded;

    /// <summary>工具卡片集合，XAML 用 ItemsRepeater + UniformGridLayout 按窗口宽度自动换列。</summary>
    public ObservableCollection<ComputerToolCard> Cards { get; } = new()
    {
        new ComputerToolCard("runtime", "运行库修复",
            "补全 VC++、.NET 与 DirectX", "\uE74C"),
        new ComputerToolCard("context", "右键菜单管理",
            "按场景管理菜单与来源软件图标", "\uE8BD"),
        new ComputerToolCard("dotnet", ".NET 环境补全",
            "检测并安装缺失的 .NET 运行时", "\uE943"),
        new ComputerToolCard("disk", "磁盘健康",
            "读取 SMART 温度与健康度", "\uEDA2"),
        new ComputerToolCard("geek", "Geek 卸载",
            "深度卸载并清理软件残留", "\uE7B8"),
    };

    public ComputerToolsPage()
    {
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_geekIconLoaded)
        {
            return;
        }

        _geekIconLoaded = true;

        var geekPath = FindGeekPath();
        if (geekPath is null)
        {
            return;
        }

        var icon = await ProcessIconLoader.GetIconAsync(geekPath);
        if (icon is null)
        {
            return;
        }

        var geekCard = Cards.FirstOrDefault(card => card.Id == "geek");
        geekCard?.SetIcon(icon);
    }

    private async void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string id)
        {
            return;
        }

        // 点击后立刻回到原始大小，避免页面缓存再回来时卡片仍是放大状态
        UiMotion.ScaleTo(element, 1.0, UiMotion.PressDuration);

        switch (id)
        {
            case "runtime":
                Frame.Navigate(typeof(RuntimeRepairPage));
                break;
            case "context":
                Frame.Navigate(typeof(ContextMenuPage));
                break;
            case "dotnet":
                Frame.Navigate(typeof(DotnetPage));
                break;
            case "disk":
                Frame.Navigate(typeof(DiskHealthPage));
                break;
            case "geek":
                await LaunchGeekAsync();
                break;
        }
    }

    /// <summary>卡片入场：淡入 + 上滑，按列序错开一点，避免五个卡片同时弹出。</summary>
    private void Cards_Prepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement element)
        {
            return;
        }

        var translate = new TranslateTransform();
        element.RenderTransform = translate;
        element.Opacity = 0;
        translate.Y = 14;

        var storyboard = new Storyboard
        {
            BeginTime = TimeSpan.FromMilliseconds(Math.Min(args.Index, 8) * 40),
        };

        var duration = new Duration(TimeSpan.FromMilliseconds(UiMotion.PageDuration));
        var easing = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 };

        var fade = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var move = new DoubleAnimation { To = 0, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(move, translate);
        Storyboard.SetTargetProperty(move, "Y");

        storyboard.Children.Add(fade);
        storyboard.Children.Add(move);
        storyboard.Begin();
    }

    /// <summary>悬停轻微放大；按下回缩，松开再放大，只在 RenderTransform 上动。</summary>
    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            UiMotion.ScaleTo(element, 1.015);
        }
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            UiMotion.ScaleTo(element, 1.0);
        }
    }

    private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            UiMotion.ScaleTo(element, 0.985, UiMotion.PressDuration);
        }
    }

    private void Card_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            UiMotion.ScaleTo(element, 1.015, UiMotion.PressDuration);
        }
    }

    private async Task LaunchGeekAsync()
    {
        var geekPath = FindGeekPath();
        if (geekPath is null)
        {
            await ShowMessageAsync("未找到 geek.exe", "请确认 geek.exe 与程序放在同一目录，然后重试。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = geekPath,
                WorkingDirectory = Path.GetDirectoryName(geekPath)!,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("无法启动 Geek 卸载", ex.Message);
        }
    }

    /// <summary>geek.exe 随程序发布到输出目录；开发时也可能直接从仓库根目录运行。</summary>
    private static string? FindGeekPath()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var candidate = Path.Combine(dir, "geek.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot,
        };

        await dialog.ShowAsync();
    }
}

/// <summary>电脑工具卡片数据：图标默认用字体图标，Geek 卡加载到 exe 图标后替换。</summary>
public sealed class ComputerToolCard : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public ComputerToolCard(string id, string title, string description, string glyph)
    {
        Id = id;
        Title = title;
        Description = description;
        Glyph = glyph;
    }

    public string Id { get; }

    public string Title { get; }

    public string Description { get; }

    public string Glyph { get; }

    public ImageSource? Icon => _icon;

    public Visibility GlyphVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IconVisibility => _icon is null ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetIcon(ImageSource icon)
    {
        _icon = icon;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlyphVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconVisibility)));
    }
}
