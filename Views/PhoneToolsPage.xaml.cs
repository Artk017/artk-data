using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinUIDemo.Services;

namespace WinUIDemo.Views;

public sealed partial class PhoneToolsPage : Page
{
    private DispatcherQueueTimer? _timer;
    private CancellationTokenSource? _cts;
    private PhoneInfo? _info;
    private bool _busy;

    public PhoneToolsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_timer is null)
        {
            _timer = DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(4);
            _timer.Tick += async (_, _) => await RefreshAsync();
        }

        _timer.Start();
        _ = RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        _cts?.Cancel();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OpenConsole_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CommandConsoleDialog(_info)
        {
            XamlRoot = XamlRoot,
        };

        await dialog.ShowAsync();
    }

    private async void InstallApk_Click(object sender, RoutedEventArgs e)
    {
        if (_info is null)
        {
            return;
        }

        if (_info.Serial == "—")
        {
            Toast.Error("未连接设备", "请先连接手机并打开 USB 调试。");
            return;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            ViewMode = PickerViewMode.List,
        };

        picker.FileTypeFilter.Add(".apk");

        if (App.MainWindow is not null)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        }

        StorageFile? file = await picker.PickSingleFileAsync();

        if (file is null)
        {
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "安装应用",
            Content = $"把「{file.Name}」安装到设备 {_info.Serial} 吗？\n\n已安装过同一应用会覆盖安装并保留数据。",
            PrimaryButtonText = "安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        InstallApkButton.IsEnabled = false;
        Border? busyToast = Toast.Busy("正在安装", file.Name);

        try
        {
            string apkPath = file.Path;

            // 从非文件系统的位置选中的文件可能没有本地路径，先复制到临时目录
            if (string.IsNullOrEmpty(apkPath))
            {
                apkPath = Path.Combine(Path.GetTempPath(), file.Name);

                using Stream source = await file.OpenStreamForReadAsync();
                using FileStream destination = File.Create(apkPath);
                await source.CopyToAsync(destination);
            }

            (bool ok, string output) = await PhoneTools.InstallApkAsync(_info, apkPath);
            string text = PhoneCommandText.LocalizeInstall(output).Trim();

            if (ok && text.Contains("安装成功", StringComparison.Ordinal))
            {
                Toast.Dismiss(busyToast);
                Toast.Success("安装成功", file.Name);
                await Task.Delay(1200);
                await RefreshAsync();
            }
            else
            {
                Toast.Dismiss(busyToast);
                Toast.Error("安装失败", $"{file.Name}\n{(text.Length > 0 ? text : "未知错误")}");
            }
        }
        catch (Exception ex)
        {
            Toast.Dismiss(busyToast);
            Toast.Error("安装失败", ex.Message);
        }
        finally
        {
            InstallApkButton.IsEnabled = true;
        }
    }

    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        BusyRing.IsActive = true;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            PhoneInfo info = await PhoneTools.QueryAsync(_cts.Token);
            Apply(info);
        }
        catch (OperationCanceledException)
        {
            // 页面已离开
        }
        catch (Exception ex)
        {
            StateText.Text = "检测失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
        }
    }

    private void Apply(PhoneInfo info)
    {
        _info = info;

        StateText.Text = info.StateText;
        SerialText.Text = "设备序列号：" + info.Serial;

        if (info.ToolsMissing)
        {
            ToolText.Text = "未找到 adb / fastboot";
        }
        else
        {
            ToolText.Text = string.IsNullOrEmpty(info.ToolPath) ? string.Empty : "工具：" + info.ToolPath;
        }

        NameValue.Text = info.DeviceName;
        CodenameValue.Text = info.Codename;
        AndroidValue.Text = info.AndroidVersion;
        BlValue.Text = info.BootloaderLock;
        SystemValue.Text = info.SystemVersion;
        KernelValue.Text = info.KernelVersion;
        AbValue.Text = info.AbPartition;
        SelinuxValue.Text = info.Selinux;
        SerialValue.Text = info.Serial;

        BatteryStatusValue.Text = info.BatteryStatus;
        BatteryLevelValue.Text = info.BatteryLevel;
        BatteryVoltageValue.Text = info.BatteryVoltage;
        BatteryTempValue.Text = info.BatteryTemp;
        BatteryLifeValue.Text = info.BatteryLife;

        double level = ParsePercent(info.BatteryLevel);
        BatteryFill.Width = 38 * level / 100;

        MemoryRing.Value = info.HasMemory ? info.MemoryPercent : 0;
        MemoryPercentText.Text = info.HasMemory
            ? info.MemoryPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : "—";
        MemoryText.Text = info.MemoryText;

        StorageRing.Value = info.HasStorage ? info.StoragePercent : 0;
        StoragePercentText.Text = info.HasStorage
            ? info.StoragePercent.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : "—";
        StorageText.Text = info.StorageText;

        bool hasDevice = info.Serial != "—";
        bool adbMode = info.State is PhoneConnectionState.System
            or PhoneConnectionState.Recovery
            or PhoneConnectionState.Sideload;

        RebootButton.IsEnabled = hasDevice;
        SoftRebootButton.IsEnabled = adbMode;
        FastbootButton.IsEnabled = adbMode;
        FastbootdButton.IsEnabled = adbMode;
        InstallApkButton.IsEnabled = adbMode;

        ConsoleButton.IsEnabled = !info.ToolsMissing;

        HintText.Visibility = info.ToolsMissing ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = "没有找到 adb / fastboot：把 platform-tools 放到程序目录的 tools\\platform-tools 下，"
            + "或设置环境变量 NOVA_ADB / NOVA_FASTBOOT 指向 adb.exe 与 fastboot.exe。";
    }

    private static double ParsePercent(string text)
    {
        string digits = new(text.Where(char.IsDigit).ToArray());

        return double.TryParse(digits, out double value) ? Math.Clamp(value, 0, 100) : 0;
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag || _info is null)
        {
            return;
        }

        (PhoneAction action, string title, string message) = tag switch
        {
            "soft" => (PhoneAction.SoftReboot, "软重启", "确定要软重启吗？只重启用户空间，不动内核。"),
            "fastboot" => (PhoneAction.EnterFastboot, "进入 Fastboot", "确定要重启到 fastboot 模式吗？"),
            "fastbootd" => (PhoneAction.EnterFastbootd, "进入 Fastbootd", "确定要重启到 fastbootd 吗？"),
            _ => (PhoneAction.Reboot, "重启系统", "确定要重启设备吗？"),
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = "执行",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        Border? busyToast = Toast.Busy(title, "正在执行…");

        try
        {
            (bool ok, string result) = await PhoneTools.RunActionAsync(_info, action);
            Toast.Dismiss(busyToast);

            if (ok)
            {
                Toast.Success(title, result);
                await Task.Delay(2000);
                await RefreshAsync();
            }
            else
            {
                Toast.Error(title + "失败", result);
            }
        }
        catch (Exception ex)
        {
            Toast.Dismiss(busyToast);
            Toast.Error(title + "失败", ex.Message);
        }
    }
}
