using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using ArtkTools.Helpers;
using ArtkTools.Services;

namespace ArtkTools.Views;

public sealed partial class GamePage : Page
{
    /// <summary>要检测的软件：名字用于扫描卸载注册表，RegFile 是服务器 data 目录里的配置文件名。</summary>
    private static readonly Candidate[] Candidates =
    {
        new("崩坏：星穹铁道", "崩坏：星穹铁道.reg", "米哈游 · 回合制 RPG", "#5B6CF9"),
        new("PotPlayer 64 bit", "PotPlayer 64 bit.reg", "视频播放器 · 个性化设置", "#F57C00"),
    };

    /// <summary>刷新序号：每次刷新自增，异步任务回来后若已过期就丢弃，避免重复叠加。</summary>
    private int _refreshToken;

    public GamePage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        int token = ++_refreshToken;

        var installed = new List<Candidate>();

        foreach (Candidate candidate in Candidates)
        {
            bool ok = await Task.Run(() => AppInstall.IsInstalled(candidate.Name));

            if (token != _refreshToken)
            {
                return;
            }

            if (ok)
            {
                installed.Add(candidate);
            }
        }

        // 先取图标（取不到就退回彩色首字母块）
        var icons = new List<BitmapImage?>();

        foreach (Candidate candidate in installed)
        {
            string? exe = await Task.Run(() => AppInstall.FindExecutable(candidate.Name));
            BitmapImage? image = exe is null ? null : await ProcessIconLoader.GetAsync(exe);

            if (token != _refreshToken)
            {
                return;
            }

            icons.Add(image);
        }

        if (token != _refreshToken)
        {
            return;
        }

        CardsHost.Children.Clear();
        CardsHost.RowDefinitions.Clear();

        for (int i = 0; i < installed.Count; i++)
        {
            int row = i / 2;
            int column = i % 2;

            if (row >= CardsHost.RowDefinitions.Count)
            {
                CardsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            FrameworkElement card = BuildCard(installed[i], icons[i]);
            Grid.SetRow(card, row);
            Grid.SetColumn(card, column);
            CardsHost.Children.Add(card);
        }

        CountText.Text = installed.Count > 0 ? $"已检测到 {installed.Count} 项" : string.Empty;
        EmptyState.Visibility = installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement BuildCard(Candidate candidate, BitmapImage? icon)
    {
        FrameworkElement iconElement = BuildIcon(candidate, icon);

        var info = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };

        info.Children.Add(new TextBlock
        {
            Text = candidate.Name,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        info.Children.Add(new TextBlock
        {
            Text = candidate.Desc,
            FontSize = 12,
            Opacity = 0.72,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var upload = new Button
        {
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeBrush("ControlFillColorTransparentBrush", Microsoft.UI.Colors.Transparent),
            Content = new FontIcon { Glyph = "\uE898", FontSize = 14 },
        };

        ToolTipService.SetToolTip(upload, "上传 .reg 配置（可多选）");
        upload.Click += async (_, _) => await UploadRegAsync(candidate);

        var gear = new Button
        {
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeBrush("ControlFillColorTransparentBrush", Microsoft.UI.Colors.Transparent),
            Content = new FontIcon { Glyph = "\uE713", FontSize = 14 },
        };

        ToolTipService.SetToolTip(gear, "应用配置");
        gear.Click += async (_, _) => await ShowSettingsAsync(candidate);

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(iconElement, 0);
        Grid.SetColumn(info, 1);
        Grid.SetColumn(upload, 2);
        Grid.SetColumn(gear, 3);

        grid.Children.Add(iconElement);
        grid.Children.Add(info);
        grid.Children.Add(upload);
        grid.Children.Add(gear);

        var card = new Border
        {
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush", Microsoft.UI.Colors.DimGray),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush", Microsoft.UI.Colors.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(),
            Child = grid,
        };

        // 悬停轻微放大，给“可点击”的反馈（只动 RenderTransform，不触发布局）
        card.PointerEntered += (_, _) => UiMotion.ScaleTo(card, 1.015);
        card.PointerExited += (_, _) => UiMotion.ScaleTo(card, 1.0);

        return card;
    }

    private static FrameworkElement BuildIcon(Candidate candidate, BitmapImage? icon)
    {
        if (icon is not null)
        {
            return new Rectangle
            {
                Width = 44,
                Height = 44,
                RadiusX = 12,
                RadiusY = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new ImageBrush { ImageSource = icon, Stretch = Stretch.UniformToFill },
            };
        }

        var border = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(12),
            Background = HexBrush(candidate.Color),
            VerticalAlignment = VerticalAlignment.Center,
        };

        border.Child = new TextBlock
        {
            Text = candidate.Name[..1],
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        return border;
    }

    private async Task ShowSettingsAsync(Candidate candidate)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = candidate.Name,
            Content = $"点「应用」会从服务器依次应用该软件的全部 reg 配置（服务器上没有时用固定文件名：{candidate.RegFile}）。",
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        (bool ok, string message) = await RegApplier.ApplyAllFromServerAsync(candidate.Name, candidate.RegFile);

        ShowResult(ok ? "应用完成" : "应用失败", message);
    }

    /// <summary>选择本地 .reg（可多选）上传到服务器该软件名下，并立即应用服务器上的全部配置。</summary>
    private async Task UploadRegAsync(Candidate candidate)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add(".reg");

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0)
        {
            return;
        }

        var uploaded = new List<string>();
        var errors = new List<string>();

        foreach (var file in files)
        {
            (bool ok, string message) = await RegApplier.UploadAsync(candidate.Name, file.Path);
            if (ok)
            {
                uploaded.Add(file.Name);
            }
            else
            {
                errors.Add($"{file.Name}：{message}");
            }
        }

        if (uploaded.Count == 0)
        {
            ShowResult("上传失败", string.Join("\n", errors));
            return;
        }

        // 上传成功后应用服务器上的全部配置（含刚上传的）
        (bool applyOk, string applyMessage) = await RegApplier.ApplyAllFromServerAsync(candidate.Name, candidate.RegFile);

        string summary = $"已上传 {uploaded.Count} 个配置：{string.Join("、", uploaded)}\n{applyMessage}";
        if (errors.Count > 0)
        {
            summary += "\n上传失败：\n" + string.Join("\n", errors);
        }

        await RefreshAsync();

        ShowResult(applyOk && errors.Count == 0 ? "已上传并应用" : "已上传，部分失败", summary);
    }

    /// <summary>结果用右上角的 Toast 提示（标题里带「失败」就红色，否则绿色）。</summary>
    private static void ShowResult(string title, string message)
    {
        if (title.Contains("失败", StringComparison.Ordinal))
        {
            Toast.Error(title, message, 10);
        }
        else
        {
            Toast.Success(title, message, 8);
        }
    }

    private static Brush ThemeBrush(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }

    private static SolidColorBrush HexBrush(string hex)
    {
        hex = hex.TrimStart('#');

        byte r = Convert.ToByte(hex[..2], 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);

        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
    }

    private sealed record Candidate(string Name, string RegFile, string Desc, string Color);
}
