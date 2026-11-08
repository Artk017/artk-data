using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ArtkTools.Helpers;

namespace ArtkTools.Views;

/// <summary>
/// 电脑工具入口：五个工具卡片，前四个进入对应工具页面（外层 ContentFrame 导航），
/// 最后一张「Geek 卸载」直接启动随程序分发的 geek.exe。
/// </summary>
public sealed partial class ComputerToolsPage : Page
{
    private bool _geekIconLoaded;

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

        GeekIcon.Source = icon;
        GeekIcon.Visibility = Visibility.Visible;
        GeekFallbackIcon.Visibility = Visibility.Collapsed;
    }

    private void OpenRuntime_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(RuntimeRepairPage));

    private void OpenContext_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(ContextMenuPage));

    private void OpenDotnet_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(DotnetPage));

    private void OpenDisk_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(DiskHealthPage));

    private async void OpenGeek_Click(object sender, RoutedEventArgs e)
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
