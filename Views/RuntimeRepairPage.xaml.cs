using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ArtkTools.Services.ComputerTools;

namespace ArtkTools.Views;

/// <summary>运行库修复页：检测 VC++ / .NET Framework / DirectX 并逐项修复。</summary>
public sealed partial class RuntimeRepairPage : Page
{
    private bool _started;

    public RuntimeRepairPage()
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
        await LoadAsync();
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

    private async System.Threading.Tasks.Task LoadAsync()
    {
        RuntimeRing.IsActive = true;
        RuntimeRefreshButton.IsEnabled = false;
        RuntimeStatusText.Text = "正在检测运行库…";

        try
        {
            IReadOnlyList<RuntimeStatus> list = await RuntimeRepairService.DetectAsync();
            RuntimeList.ItemsSource = list;

            int ok = list.Count(s => s.Installed);
            RuntimeStatusText.Text = $"{ok}/{list.Count} 项正常。VC++ / .NET 从微软官网下载安装；DirectX 缺失 DLL 用随包数据直接补全。";
        }
        catch (Exception ex)
        {
            RuntimeStatusText.Text = "检测失败：" + ex.Message;
        }
        finally
        {
            RuntimeRing.IsActive = false;
            RuntimeRefreshButton.IsEnabled = true;
        }
    }

    private async void RuntimeRefresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void RuntimeRepair_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RuntimeStatus status || status.Installed)
        {
            return;
        }

        RuntimeRefreshButton.IsEnabled = false;
        RuntimeList.IsEnabled = false;
        RuntimeProgressPanel.Visibility = Visibility.Visible;
        RuntimeProgress.Value = 0;
        RuntimeProgressText.Text = "准备中…";

        try
        {
            string message = await RuntimeRepairService.RepairAsync(status.Id, progress =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    RuntimeProgress.Value = progress.Percent;
                    RuntimeProgressText.Text = $"{PhaseLabel(progress.Phase)} · {progress.Detail}";
                });
            });

            RuntimeStatusText.Text = message;
        }
        catch (Exception ex)
        {
            RuntimeStatusText.Text = "修复失败：" + ex.Message;
        }
        finally
        {
            RuntimeProgressPanel.Visibility = Visibility.Collapsed;
            RuntimeList.IsEnabled = true;
            RuntimeRefreshButton.IsEnabled = true;
        }

        await LoadAsync();
    }

    private static string PhaseLabel(string phase) => phase switch
    {
        RuntimeRepairService.PhaseDownloading => "下载中",
        RuntimeRepairService.PhaseVerifying => "校验签名",
        RuntimeRepairService.PhaseInstalling => "安装中",
        RuntimeRepairService.PhaseComplete => "完成",
        _ => phase,
    };
}
