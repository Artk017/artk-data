using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIDemo.Services;

namespace WinUIDemo.Views;

/// <summary>
/// 「系统设置」弹窗的内容：只放外观 / 交互类的开关（写系统配置，但不动安全设置）。
/// 安全相关的（防火墙 / 防病毒 / 激活 / 注册表权限）已经搬到 Safe 页。
/// 外壳由 <see cref="ToolWindow"/> 负责，操作结果一律用右上角的 Toast 通知。
/// </summary>
public sealed partial class SystemSettingsView : UserControl
{
    private bool _loading = true;

    public SystemSettingsView()
    {
        InitializeComponent();

        BuildDesktopIconMenu();
        Loaded += (_, _) => LoadTweaks();
    }

    private void LoadTweaks()
    {
        _loading = true;

        PointerPrecisionToggle.IsOn = Read(SystemTweaks.TryGetPointerPrecision, PointerPrecisionToggle);
        HighPerformanceToggle.IsOn = Read(SystemTweaks.TryGetHighPerformance, HighPerformanceToggle);
        BackgroundToggle.IsOn = Read(SystemTweaks.TryGetBackgroundPicture, BackgroundToggle);
        DarkModeToggle.IsOn = Read(SystemTweaks.TryGetDarkMode, DarkModeToggle);
        TaskbarLeftToggle.IsOn = Read(SystemTweaks.TryGetTaskbarLeft, TaskbarLeftToggle);
        AltTabTabsToggle.IsOn = Read(AltTabTabs.TryGetEnabled, AltTabTabsToggle);

        LoadRefreshRates();
        LoadDesktopIcons();
        LoadContextMenuStyle();
        LoadUacNotifyState();
        LoadQuietHoursState();

        _loading = false;
    }

    // ---------------- 关闭请勿打扰 ----------------

    /// <summary>把开关对齐系统里真实的请勿打扰配置。</summary>
    private void LoadQuietHoursState()
    {
        bool wasLoading = _loading;
        _loading = true;

        if (QuietHours.TryGetDisabled(out bool disabled))
        {
            DisableDndToggle.IsEnabled = true;
            DisableDndToggle.IsOn = disabled;
        }
        else
        {
            DisableDndToggle.IsEnabled = false;
            DisableDndToggle.IsOn = false;
            ToolTipService.SetToolTip(DisableDndToggle, "系统里读不到请勿打扰的设置，已停用");
        }

        _loading = wasLoading;
    }

    private async void DisableDnd_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded)
        {
            return;
        }

        bool wanted = DisableDndToggle.IsOn;
        DisableDndToggle.IsEnabled = false;
        Border? busy = Toast.Busy(
            wanted ? "正在关闭请勿打扰" : "正在恢复自动勿扰",
            "等待通知服务刷新…");

        var (ok, error) = await Task.Run(() =>
        {
            bool succeeded = QuietHours.SetDisabled(wanted, out string message);
            return (succeeded, message);
        });

        DisableDndToggle.IsEnabled = true;
        Toast.Dismiss(busy);

        if (!ok)
        {
            Toast.Error("切换失败", error);
        }
        else
        {
            Toast.Success(
                wanted ? "已关闭请勿打扰" : "已恢复自动勿扰",
                wanted ? "投影 / 全屏 / 功能更新后一小时都不再自动开启，玩游戏时仍会开启" : "自动勿扰规则已恢复");
        }

        // 不管成功失败，都以系统里的真实状态为准
        LoadQuietHoursState();
    }

    // ---------------- 右键菜单样式 ----------------

    /// <summary>把下拉对齐注册表里的真实状态：有 InprocServer32 键就是 Windows 10 样式。</summary>
    private void LoadContextMenuStyle()
    {
        bool wasLoading = _loading;
        _loading = true;

        if (ContextMenuStyle.TryGetIsWindows10(out bool windows10))
        {
            ContextMenuStyleBox.IsEnabled = true;
            ContextMenuStyleBox.SelectedIndex = windows10 ? 0 : 1;
        }
        else
        {
            ContextMenuStyleBox.IsEnabled = false;
            ContextMenuStyleBox.SelectedIndex = -1;
        }

        _loading = wasLoading;
    }

    private void ContextMenuStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded || ContextMenuStyleBox.SelectedIndex < 0)
        {
            return;
        }

        bool wanted = ContextMenuStyleBox.SelectedIndex == 0;
        bool ok = ContextMenuStyle.Apply(wanted, out string error);

        if (!ok)
        {
            Toast.Error("切换失败", error);

            // 失败就回到系统真实状态
            LoadContextMenuStyle();
            return;
        }

        Toast.Success(
            wanted ? "已换成 Windows 10 右键菜单" : "已换成 Windows 11 右键菜单",
            "重启资源管理器后生效");
    }

    // ---------------- 应用运行通知（UAC 滑块） ----------------

    /// <summary>把开关对齐 UAC 策略键里的真实档位。</summary>
    private void LoadUacNotifyState()
    {
        bool wasLoading = _loading;
        _loading = true;

        if (UacNotify.TryGetEnabled() is bool enabled)
        {
            UacNotifyToggle.IsEnabled = true;
            UacNotifyToggle.IsOn = enabled;
        }
        else
        {
            // 停在「始终通知」这种别的档位上：开关置灰，不去猜
            UacNotifyToggle.IsEnabled = false;
            UacNotifyToggle.IsOn = false;
        }

        _loading = wasLoading;
    }

    private async void UacNotify_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded)
        {
            return;
        }

        bool wanted = UacNotifyToggle.IsOn;

        if (UacNotify.TryGetEnabled() == wanted)
        {
            return;
        }

        UacNotifyToggle.IsEnabled = false;
        Border? busy = Toast.Busy(wanted ? "正在开启应用运行通知" : "正在关闭应用运行通知", "等待管理员确认…");

        var (ok, error) = await Task.Run(() =>
        {
            bool succeeded = UacNotify.Apply(wanted, out string message);
            return (succeeded, message);
        });

        if (UacNotify.TryGetEnabled() is bool actual && actual != wanted)
        {
            _loading = true;
            UacNotifyToggle.IsOn = actual;
            _loading = false;
        }

        UacNotifyToggle.IsEnabled = true;
        Toast.Dismiss(busy);

        if (!ok)
        {
            Toast.Error("切换失败", error);
            return;
        }

        Toast.Success(
            wanted ? "应用运行通知已开启" : "应用运行通知已关闭",
            wanted ? "仅当应用尝试更改计算机时通知" : "更改 Windows 设置和应用安装都不再通知");
    }

    // ---------------- 刷新率 ----------------

    private sealed record RefreshOption(int Hertz, string Label);

    private void LoadRefreshRates()
    {
        var options = new List<RefreshOption>();
        foreach (int hz in DisplaySettings.AvailableRefreshRates())
        {
            options.Add(new RefreshOption(hz, hz + " Hz"));
        }

        RefreshRateBox.ItemsSource = options;

        int current = DisplaySettings.CurrentRefreshRate();
        RefreshRateBox.SelectedItem = options.Find(o => o.Hertz == current);

        if (RefreshRateBox.SelectedItem is null)
        {
            RefreshRateBox.IsEnabled = options.Count > 0;
        }
    }

    private void RefreshRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded || RefreshRateBox.SelectedItem is not RefreshOption option)
        {
            return;
        }

        bool ok = DisplaySettings.SetRefreshRate(option.Hertz);
        Report(ok);

        // 切换失败时回到系统真实值
        if (!ok)
        {
            int actual = DisplaySettings.CurrentRefreshRate();
            _loading = true;
            RefreshRateBox.SelectedItem = (RefreshRateBox.ItemsSource as List<RefreshOption>)?.Find(o => o.Hertz == actual);
            _loading = false;
        }
    }

    // ---------------- 桌面图标 ----------------

    private void BuildDesktopIconMenu()
    {
        foreach (var item in DesktopIcons.All)
        {
            var entry = new CheckBox
            {
                Content = item.Name,
                Tag = item.Id,
                FontSize = 12.5,
                MinWidth = 150,
            };

            entry.Click += DesktopIcon_Click;
            DesktopIconPanel.Children.Add(entry);
        }

        // 每次展开都从系统重读一遍，避免外部改过之后状态不同步
        DesktopIconFlyout.Opening += (_, _) => LoadDesktopIcons();

        // 页面构建时先按真实状态填一次，展开前摘要/勾选就是对的
        LoadDesktopIcons();
    }

    private void LoadDesktopIcons()
    {
        var states = DesktopIcons.QueryVisibility();

        foreach (object entry in DesktopIconPanel.Children)
        {
            if (entry is CheckBox box && box.Tag is string id && states.TryGetValue(id, out bool visible))
            {
                box.IsChecked = visible;
            }
        }

        UpdateDesktopIconSummary();
    }

    private void UpdateDesktopIconSummary()
    {
        int total = DesktopIconPanel.Children.Count;
        int on = 0;

        foreach (object entry in DesktopIconPanel.Children)
        {
            if (entry is CheckBox box && box.IsChecked == true)
            {
                on++;
            }
        }

        DesktopIconSummary.Text = total == 0
            ? "无"
            : on == total ? "全部显示" : $"{on} / {total} 项";
    }

    private void DesktopIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox box || box.Tag is not string id)
        {
            return;
        }

        bool visible = box.IsChecked == true;
        bool ok = DesktopIcons.SetVisible(id, visible);
        Report(ok);

        if (!ok)
        {
            box.IsChecked = DesktopIcons.IsVisible(id);
        }

        UpdateDesktopIconSummary();
    }

    private static bool Read(Reader reader, Control control)
    {
        if (reader(out bool value))
        {
            return value;
        }

        // 读不到就不猜：置灰并说明原因，别再让人以为是点击无反应
        control.IsEnabled = false;
        ToolTipService.SetToolTip(control, "系统里读不到这一项，已停用");
        return false;
    }

    private delegate bool Reader(out bool value);

    // ---------------- 各个开关 ----------------

    private void PointerPrecision_Toggled(object sender, RoutedEventArgs e)
    {
        Apply(PointerPrecisionToggle, SystemTweaks.SetPointerPrecision, SystemTweaks.TryGetPointerPrecision);
    }

    private void HighPerformance_Toggled(object sender, RoutedEventArgs e)
    {
        Apply(HighPerformanceToggle, SystemTweaks.SetHighPerformance, SystemTweaks.TryGetHighPerformance);
    }

    private void Background_Toggled(object sender, RoutedEventArgs e)
    {
        Apply(BackgroundToggle, SystemTweaks.SetBackgroundPicture, SystemTweaks.TryGetBackgroundPicture);
    }

    private void DarkMode_Toggled(object sender, RoutedEventArgs e)
    {
        Apply(DarkModeToggle, SystemTweaks.SetDarkMode, SystemTweaks.TryGetDarkMode);
    }

    private void TaskbarLeft_Toggled(object sender, RoutedEventArgs e)
    {
        Apply(TaskbarLeftToggle, SystemTweaks.SetTaskbarLeft, SystemTweaks.TryGetTaskbarLeft);
    }

    private void AltTabTabs_Toggled(object sender, RoutedEventArgs e)
    {
        Apply(AltTabTabsToggle, AltTabTabs.SetEnabled, AltTabTabs.TryGetEnabled);
    }

    // ---------------- 公共流程 ----------------

    /// <summary>写入 → 回读 → 以系统实际值纠正开关 → 反馈结果。</summary>
    private void Apply(ToggleSwitch toggle, Writer write, Reader read)
    {
        if (_loading)
        {
            return;
        }

        bool wanted = toggle.IsOn;
        bool ok = write(wanted);

        if (read(out bool actual) && actual != wanted)
        {
            _loading = true;
            toggle.IsOn = actual;
            _loading = false;
        }

        Report(ok);
    }

    private void Report(bool ok)
    {
        if (ok)
        {
            Toast.Success("已应用到系统");
            return;
        }

        Toast.Error("写入失败", SystemTweaks.LastError());
    }

    private delegate bool Writer(bool value);
}
