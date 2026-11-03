using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIDemo.Services;

namespace WinUIDemo.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly string[] Sections = { "windows", "software", "game", "safe", "phone" };

    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();

        LoadFromSettings();

        VersionText.Text = $"版本 {MainWindow.CurrentVersion} · WinUI 3 · Windows App SDK 2.5";

        _loading = false;

        ConfigureUninstall();
    }

    private void LoadFromSettings()
    {
        var settings = AppSettings.Current;

        ThemeCombo.SelectedIndex = settings.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };

        MicaToggle.IsOn = settings.UseMica;

        int sectionIndex = Array.IndexOf(Sections, settings.StartupSection);
        StartupCombo.SelectedIndex = sectionIndex >= 0 ? sectionIndex : 0;

        InstallDirBox.Text = settings.InstallDir;

        // 服务器地址写死在客户端里，这里只读展示，不给改
        ServerAddressText.Text = CatalogServer.Url;
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettings.Current.Theme = ThemeCombo.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System",
        };

        ApplyChanges();
    }

    private void MicaToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettings.Current.UseMica = MicaToggle.IsOn;
        ApplyChanges();
    }

    private void StartupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        int index = StartupCombo.SelectedIndex;
        AppSettings.Current.StartupSection = index >= 0 && index < Sections.Length ? Sections[index] : "windows";
        ApplyChanges();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.Reset();

        _loading = true;
        LoadFromSettings();
        _loading = false;

        ApplyChanges();
    }

    private void ApplyChanges()
    {
        AppSettings.Current.Save();
        MainWindow.Instance?.ApplyAppearance();
    }

    // ---------------- 软件安装位置 ----------------

    private void InstallDirBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettings.Current.InstallDir = InstallDirBox.Text.Trim();
        AppSettings.Current.Save();
    }

    private async void BrowseInstallDir_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is null)
        {
            return;
        }

        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };

        // FolderPicker 至少要有一个文件类型过滤器，否则会抛异常
        picker.FileTypeFilter.Add("*");

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null || string.IsNullOrEmpty(folder.Path))
        {
            return;
        }

        InstallDirBox.Text = folder.Path;
        AppSettings.Current.InstallDir = folder.Path;
        AppSettings.Current.Save();
    }

    private void ResetInstallDir_Click(object sender, RoutedEventArgs e)
    {
        InstallDirBox.Text = string.Empty;
        AppSettings.Current.InstallDir = string.Empty;
        AppSettings.Current.Save();
    }

    // ---------------- 清单服务器 ----------------

    private async void CheckServer_Click(object sender, RoutedEventArgs e)
    {
        ServerStatusText.Text = "正在连接…";

        (bool ok, string message) = await CatalogServer.PingAsync();

        ServerStatusText.Text = message;
        ServerStatusText.Foreground = new SolidColorBrush(ok
            ? Windows.UI.Color.FromArgb(255, 0x0F, 0x9D, 0x58)
            : Windows.UI.Color.FromArgb(255, 0xDC, 0x26, 0x26));

        if (ok)
        {
            string where = Uri.TryCreate(CatalogServer.Url, UriKind.Absolute, out Uri? uri)
                ? uri.Authority
                : CatalogServer.Url;

            Toast.Success("已连接服务器", $"{where} · {message.Replace("连接成功 · ", string.Empty, StringComparison.Ordinal)}", 5);
        }
        else
        {
            Toast.Error("连接失败", message);
        }
    }

    // ---------------- 卸载 ----------------

    /// <summary>不是安装版（便携 / 开发直跑）时置灰按钮并说明原因，别点了没反应。</summary>
    private void ConfigureUninstall()
    {
        if (AppUninstaller.CanUninstall(out string reason))
        {
            UninstallButton.IsEnabled = true;
        }
        else
        {
            UninstallButton.IsEnabled = false;
            UninstallHint.Text = reason;
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "卸载 Artk Tools",
            Content = "确定要卸载 Artk Tools 吗？程序会立即关闭，并从本机移除安装文件、开始菜单 / 桌面快捷方式，"
                    + "同时删除 %LOCALAPPDATA%\\Nova 里的设置、清单缓存与下载记录（已下载的安装包本身不会被删除）。",
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (!AppUninstaller.TryUninstall(out string error))
        {
            Toast.Error("无法卸载", error, 10);
            return;
        }

        // 退出后删除 Nova 自己的数据目录（设置 / 缓存 / 下载记录）
        AppUninstaller.ScheduleDataCleanup();

        // Update.exe --uninstall 已拉起，立刻退出自身，让它在文件解锁后删除安装目录。
        Environment.Exit(0);
    }
}
