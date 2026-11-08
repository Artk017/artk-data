using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ArtkTools.Services;
using ArtkTools.Services.ComputerTools;

namespace ArtkTools.Views;

/// <summary>
/// 磁盘健康页：SMART 直读。程序以管理员身份启动（app.manifest requireAdministrator），
/// ATA 硬盘直接用 DFP_RECEIVE_DRIVE_DATA 读属性与阈值，NVMe 用 IOCTL_STORAGE_QUERY_PROPERTY；
/// 万一个别控制器不返回，才显示「以管理员身份读取」再试一次。
/// </summary>
public sealed partial class DiskHealthPage : Page
{
    private bool _started;

    public DiskHealthPage()
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
        DiskRing.IsActive = true;
        DiskRefreshButton.IsEnabled = false;
        DiskElevatedButton.IsEnabled = false;
        DiskStatusText.Text = "正在读取磁盘信息…";

        try
        {
            ShowResponse(await DiskHealthService.GetHealthAsync());
        }
        catch (Exception ex)
        {
            DiskStatusText.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            DiskRing.IsActive = false;
            DiskRefreshButton.IsEnabled = true;
            DiskElevatedButton.IsEnabled = true;
        }
    }

    private async void DiskElevated_Click(object sender, RoutedEventArgs e)
    {
        DiskRing.IsActive = true;
        DiskRefreshButton.IsEnabled = false;
        DiskElevatedButton.IsEnabled = false;
        DiskStatusText.Text = "正在请求管理员权限…";

        try
        {
            ShowResponse(await DiskHealthService.GetHealthElevatedAsync());
        }
        catch (Exception ex)
        {
            DiskStatusText.Text = ex.HResult == unchecked((int)0x800704C7)
                ? "已取消管理员授权。"
                : "读取失败：" + ex.Message;
        }
        finally
        {
            DiskRing.IsActive = false;
            DiskRefreshButton.IsEnabled = true;
            DiskElevatedButton.IsEnabled = true;
        }
    }

    private async void DiskRefresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void ShowResponse(DiskHealthResponse response)
    {
        DiskList.ItemsSource = response.Disks;

        int unknown = response.Disks.Count(d => !d.HasSmart);
        bool elevated = RegistryAcl.IsElevated;
        DiskElevatedButton.Visibility = unknown > 0 && !elevated ? Visibility.Visible : Visibility.Collapsed;

        DiskStatusText.Text =
            $"共 {response.TotalCount} 块硬盘：健康 {response.HealthyCount}，警告 {response.WarningCount}，" +
            $"异常 {response.UnhealthyCount}，未读取 SMART {unknown}。" +
            (unknown == 0
                ? string.Empty
                : elevated
                    ? " 未返回的硬盘可能未启用 SMART、使用 USB 硬盘盒或在 RAID 模式下。"
                    : " 未返回的硬盘可能未启用 SMART、使用 USB 硬盘盒或在 RAID 模式下；也可点右上角「以管理员身份读取」再试一次。");
    }
}
