using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ArtkTools.Services.ComputerTools;

namespace ArtkTools.Views;

/// <summary>.NET 环境补全页：列出缺少的 Runtime / SDK / Framework 并一键安装。</summary>
public sealed partial class DotnetPage : Page
{
    private bool _started;

    public DotnetPage()
    {
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        await LoadAsync(force: false);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(ComputerToolsPage));
        }
    }

    private async System.Threading.Tasks.Task LoadAsync(bool force)
    {
        DotnetRing.IsActive = true;
        DotnetRefreshButton.IsEnabled = false;
        DotnetInstallAllButton.IsEnabled = false;
        DotnetStatusText.Text = "正在检测 .NET 环境…";

        try
        {
            if (force || !DotnetCompletionService.IsLoaded)
            {
                await DotnetCompletionService.LoadAsync();
            }
            else
            {
                await DotnetCompletionService.RefreshInstalledAsync();
            }

            var all = DotnetCompletionService.Installables;
            var missing = all
                .Where(i => i.Status != DotnetInstallStatus.Installed)
                .OrderByDescending(i => i.ChannelVersion)
                .ToList();

            DotnetList.ItemsSource = missing;

            DotnetStatusText.Text =
                $"当前架构 {DotnetCompletionService.CurrentArch}；已安装 {all.Count - missing.Count} 项，可补全 {missing.Count} 项。";
        }
        catch (Exception ex)
        {
            DotnetStatusText.Text = "检测失败：" + ex.Message +
                                    "（获取 .NET 版本列表需要访问 dotnetcli.blob.core.windows.net）";
        }
        finally
        {
            DotnetRing.IsActive = false;
            DotnetRefreshButton.IsEnabled = true;
            DotnetInstallAllButton.IsEnabled = true;
        }
    }

    private async void DotnetRefresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(force: true);

    private async void DotnetInstall_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DotnetInstallableItem item || !item.CanInstall)
        {
            return;
        }

        DotnetList.IsEnabled = false;
        DotnetRefreshButton.IsEnabled = false;
        DotnetInstallAllButton.IsEnabled = false;
        DotnetProgressPanel.Visibility = Visibility.Visible;
        DotnetProgress.Value = 0;
        DotnetProgressText.Text = "准备中…";

        try
        {
            await DotnetCompletionService.InstallAsync(item,
                text => DispatcherQueue.TryEnqueue(() => DotnetProgressText.Text = text),
                new Progress<double>(value =>
                    DispatcherQueue.TryEnqueue(() => DotnetProgress.Value = value)));

            DotnetStatusText.Text = "安装结束，正在刷新已安装版本…";
        }
        catch (Exception ex)
        {
            DotnetStatusText.Text = "安装失败：" + ex.Message;
        }
        finally
        {
            DotnetProgressPanel.Visibility = Visibility.Collapsed;
            DotnetList.IsEnabled = true;
            DotnetRefreshButton.IsEnabled = true;
            DotnetInstallAllButton.IsEnabled = true;
        }

        await LoadAsync(force: false);
    }

    /// <summary>一键安装当前列表里所有可安装项（逐个下载安装，实时显示总体进度）。</summary>
    private async void DotnetInstallAll_Click(object sender, RoutedEventArgs e)
    {
        var items = DotnetList.ItemsSource is IEnumerable<DotnetInstallableItem> source
            ? source.Where(i => i.CanInstall).ToList()
            : new List<DotnetInstallableItem>();

        if (items.Count == 0)
        {
            DotnetStatusText.Text = "当前没有可安装的项目。";
            return;
        }

        DotnetInstallAllButton.IsEnabled = false;
        DotnetRefreshButton.IsEnabled = false;
        DotnetList.IsEnabled = false;
        DotnetProgressPanel.Visibility = Visibility.Visible;
        DotnetProgress.Value = 0;
        int failed = 0;

        try
        {
            for (int index = 0; index < items.Count; index++)
            {
                DotnetInstallableItem item = items[index];
                int order = index + 1;

                DotnetStatusText.Text = $"正在安装 {order}/{items.Count}：{item.DisplayName}";

                try
                {
                    await DotnetCompletionService.InstallAsync(item,
                        text => DispatcherQueue.TryEnqueue(() =>
                            DotnetProgressText.Text = $"[{order}/{items.Count}] {text}"),
                        new Progress<double>(value => DispatcherQueue.TryEnqueue(() =>
                            DotnetProgress.Value = (index + value / 100.0) / items.Count * 100)));
                }
                catch (Exception ex)
                {
                    failed++;
                    DotnetProgressText.Text = $"[{order}/{items.Count}] 安装失败：{ex.Message}";
                }
            }
        }
        finally
        {
            DotnetProgressPanel.Visibility = Visibility.Collapsed;
            DotnetList.IsEnabled = true;
            DotnetRefreshButton.IsEnabled = true;
            DotnetInstallAllButton.IsEnabled = true;
        }

        await LoadAsync(force: false);

        DotnetStatusText.Text = failed == 0
            ? "一键安装完成，所有可安装项已处理。"
            : $"一键安装完成，其中 {failed} 项失败，可稍后重试。";
    }
}
