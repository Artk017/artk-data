using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIDemo.Services;

namespace WinUIDemo.Views;

/// <summary>
/// 「文件位置」弹窗：把 下载 / 文档 / 图片 / 视频 的保存位置改到别的磁盘，
/// 效果等同资源管理器里该文件夹 属性 →「位置 → 移动」。
/// </summary>
public static class FileLocationsDialog
{
    public static async Task ShowAsync(XamlRoot root)
    {
        var panel = new StackPanel { Spacing = 14, Width = 480 };

        panel.Children.Add(new TextBlock
        {
            Text = "把「下载 / 文档 / 图片 / 视频」的保存位置改到别的磁盘，效果等同资源管理器里该文件夹 属性 →「位置 → 移动」。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            FontSize = 12.5,
        });

        var moveCheck = new CheckBox
        {
            Content = "同时把现有文件一起移动到新位置",
            IsChecked = true,
        };

        var status = new TextBlock
        {
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        };

        foreach (KnownFolderKind kind in Enum.GetValues<KnownFolderKind>())
        {
            panel.Children.Add(BuildRow(kind, moveCheck, status));
        }

        panel.Children.Add(moveCheck);
        panel.Children.Add(status);

        var dialog = new ContentDialog
        {
            Title = "文件位置",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            PrimaryButtonText = "完成",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };

        await dialog.ShowAsync();
    }

    private static UIElement BuildRow(KnownFolderKind kind, CheckBox moveCheck, TextBlock status)
    {
        var label = new TextBlock
        {
            Text = KnownFolderLocations.DisplayName(kind),
            FontSize = 13.5,
            Width = 56,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var value = new TextBlock
        {
            Text = KnownFolderLocations.GetPath(kind),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var change = new Button { Content = "更改…", MinWidth = 72, CornerRadius = new CornerRadius(8) };
        var reset = new Button { Content = "默认", MinWidth = 60, CornerRadius = new CornerRadius(8) };

        change.Click += async (_, _) =>
        {
            string? picked = await PickFolderAsync();
            if (picked is not null)
            {
                Apply(kind, picked, moveCheck.IsChecked == true, value, status);
            }
        };

        reset.Click += (_, _) =>
        {
            string def = KnownFolderLocations.GetDefaultPath(kind);
            if (def.Length == 0)
            {
                status.Text = "拿不到默认位置。";
                return;
            }

            Apply(kind, def, false, value, status);
        };

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(label, 0);
        Grid.SetColumn(value, 1);
        Grid.SetColumn(change, 2);
        Grid.SetColumn(reset, 3);

        grid.Children.Add(label);
        grid.Children.Add(value);
        grid.Children.Add(change);
        grid.Children.Add(reset);

        var wrap = new StackPanel { Spacing = 8 };
        wrap.Children.Add(grid);
        wrap.Children.Add(BuildDivider());

        return wrap;
    }

    private static Border BuildDivider()
    {
        var line = new Border { Height = 1, Opacity = 0.6 };

        if (Application.Current.Resources.TryGetValue("GridLineBrush", out object? brush) &&
            brush is Brush resolved)
        {
            line.Background = resolved;
        }
        else
        {
            line.Background = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.25 };
        }

        return line;
    }

    private static void Apply(KnownFolderKind kind, string path, bool moveFiles, TextBlock value, TextBlock status)
    {
        string name = KnownFolderLocations.DisplayName(kind);

        if (KnownFolderLocations.SetPath(kind, path, moveFiles, out string error, out bool moved))
        {
            string now = KnownFolderLocations.GetPath(kind);
            value.Text = now;
            string tail = moved ? "（已移动现有文件）" : string.Empty;
            status.Text = error.Length == 0
                ? $"{name} 已改到：{now}{tail}"
                : $"{name} 已改到：{now}{tail}；{error}";
        }
        else
        {
            status.Text = $"{name} 修改失败：{error}";
        }
    }

    private static async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };

        picker.FileTypeFilter.Add("*");

        if (MainWindow.Instance is not null)
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder is null || string.IsNullOrEmpty(folder.Path) ? null : folder.Path;
    }
}
