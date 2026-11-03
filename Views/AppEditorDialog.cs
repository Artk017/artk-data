using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using WinUIDemo.Services;

namespace WinUIDemo.Views;

/// <summary>
/// 编辑一款软件（添加走「添加软件」窗口：在网页里点下载就自动抓直链和图标）。
/// 只让用户改名称 / 官网 / 直链；从网页抓到的图标可以在这里挑一个。
/// </summary>
public static class AppEditorDialog
{
    /// <summary>弹出编辑框；取消返回 null。</summary>
    public static async Task<AppEntry?> ShowAsync(
        XamlRoot root,
        AppEntry? existing,
        IReadOnlyList<string>? iconCandidates = null)
    {
        var name = new TextBox { Header = "名称 *", Text = existing?.Name ?? string.Empty, MaxLength = 60 };
        var home = new TextBox
        {
            Header = "官网地址 *",
            Text = existing?.HomeUrl ?? string.Empty,
            PlaceholderText = "https://",
        };

        var direct = new TextBox
        {
            Header = "下载直链（可空）",
            Text = existing?.DirectUrl ?? string.Empty,
            PlaceholderText = "https://.../setup.exe",
        };

        string selectedIcon = existing?.IconUrl ?? string.Empty;
        var iconTiles = new List<ToggleButton>();

        void SelectIcon(ToggleButton tile, string url)
        {
            selectedIcon = url;

            foreach (ToggleButton item in iconTiles)
            {
                item.IsChecked = ReferenceEquals(item, tile);
            }
        }

        var iconPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };

        if (iconCandidates is { Count: > 0 })
        {
            iconPanel.Visibility = Visibility.Visible;

            var tiles = new VariableSizedWrapGrid
            {
                ItemWidth = 64,
                ItemHeight = 64,
                MaximumRowsOrColumns = 5,
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            // 第一个选项：不用图片，继续用首字母
            var letter = new Grid { Width = 56, Height = 56 };
            letter.Children.Add(new Rectangle
            {
                RadiusX = 10,
                RadiusY = 10,
                Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x6C, 0x4C, 0xF1)),
            });
            letter.Children.Add(new TextBlock
            {
                Text = existing?.Name is { Length: > 0 } n ? n[..1] : "软",
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var letterTile = new ToggleButton
            {
                Width = 64,
                Height = 64,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                Content = letter,
                Tag = string.Empty,
            };
            ToolTipService.SetToolTip(letterTile, "不用图片，用名称首字母");
            letterTile.Click += (_, _) => SelectIcon(letterTile, string.Empty);
            iconTiles.Add(letterTile);
            tiles.Children.Add(letterTile);

            foreach (string url in iconCandidates)
            {
                var picture = new Image { Stretch = Stretch.UniformToFill };

                var tile = new ToggleButton
                {
                    Width = 64,
                    Height = 64,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(10),
                    Content = picture,
                    Tag = url,
                };

                ToolTipService.SetToolTip(tile, url);
                tile.Click += (_, _) => SelectIcon(tile, url);
                iconTiles.Add(tile);
                tiles.Children.Add(tile);

                _ = LoadTileAsync(tile, picture, url);
            }

            // 默认选第一个抓到的图标；用户也可以点首字母那块换掉
            selectedIcon = iconCandidates[0];
            iconTiles[1].IsChecked = true;

            iconPanel.Children.Add(new TextBlock
            {
                Text = "图标（从当前网页抓到的，点一下选用）",
                Style = (Style)Application.Current.Resources["MetaStyle"],
            });
            iconPanel.Children.Add(tiles);
        }

        var hint = new TextBlock
        {
            Text = "直链为空时，卡片上的按钮会直接打开官网下载页。",
            Style = (Style)Application.Current.Resources["MetaStyle"],
            TextWrapping = TextWrapping.Wrap,
        };

        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 10, Width = 420 };
        panel.Children.Add(name);
        panel.Children.Add(home);
        panel.Children.Add(direct);
        panel.Children.Add(iconPanel);
        panel.Children.Add(hint);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = existing?.Id is null ? "添加软件" : "编辑：" + existing.Name,
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 560,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };

        AppEntry? result = null;

        dialog.PrimaryButtonClick += (_, args) =>
        {
            var problems = new List<string>();

            string nameText = name.Text.Trim();
            string homeText = home.Text.Trim();
            string directText = direct.Text.Trim();

            if (nameText.Length == 0)
            {
                problems.Add("名称不能为空");
            }

            if (!IsHttpUrl(homeText))
            {
                problems.Add("官网地址要以 http:// 或 https:// 开头");
            }

            if (directText.Length > 0 && !IsHttpUrl(directText))
            {
                problems.Add("下载直链要以 http:// 或 https:// 开头");
            }

            if (problems.Count > 0)
            {
                error.Text = string.Join("\n", problems);
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            string category = existing?.Category ?? string.Empty;
            if (category is not ("通讯" or "游戏" or "工具"))
            {
                category = "工具";
            }

            result = new AppEntry
            {
                Id = existing?.Id,
                Name = nameText,
                Vendor = existing?.Vendor ?? string.Empty,
                Summary = existing?.Summary ?? string.Empty,
                Category = category,
                Color = AppCatalog.FixedColor,
                IconUrl = selectedIcon.Length > 0 ? selectedIcon : null,
                HomeUrl = homeText,
                DirectUrl = directText.Length > 0 ? directText : null,
                Resolve = existing?.Resolve ?? ResolveKind.None,
                SizeHint = existing?.SizeHint,
                Note = existing?.Note,
                Sort = existing?.Sort ?? 0,
            };
        };

        ContentDialogResult shown = await dialog.ShowAsync();
        return shown == ContentDialogResult.Primary ? result : null;
    }

    private static async System.Threading.Tasks.Task LoadTileAsync(
        ToggleButton tile, Image picture, string url)
    {
        BitmapImage? bitmap = await AppIconLoader.GetAsync(url);

        if (bitmap is null)
        {
            tile.IsEnabled = false;
            ToolTipService.SetToolTip(tile, "图片加载失败：" + url);
            return;
        }

        picture.Source = bitmap;
    }

    private static bool IsHttpUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
