using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIDemo.Services;

namespace WinUIDemo.Views;

/// <summary>
/// Safe 页：上面是防护状态展示，最下面的「系统与权限」卡片是真实生效的开关
/// （关闭防火墙 / 关闭防病毒 / 关闭防病毒(注册表) / 系统激活 / 注册表权限 / 防火墙通知）。
/// 结果一律走右上角的 Toast，不再在卡片里留一行状态文字。
/// </summary>
public sealed partial class SafePage : Page
{
    private bool _loading = true;

    public SafePage()
    {
        InitializeComponent();

        Loaded += (_, _) => LoadAll();
    }

    private void LoadAll()
    {
        _loading = true;

        LoadFirewallState();
        LoadDefenderState();
        LoadDefenderRegistryState();
        LoadSecurityCenterState();
        RefreshAclButton();

        _loading = false;

        _ = LoadActivationAsync();
    }

    // ---------------- 关闭防火墙 ----------------

    /// <summary>两个 profile 都关才把开关打成「开」；读不到就置灰。</summary>
    private void LoadFirewallState()
    {
        bool wasLoading = _loading;
        _loading = true;

        if (FirewallControl.TryGetDisabled() is bool disabled)
        {
            FirewallToggle.IsEnabled = true;
            FirewallToggle.IsOn = disabled;
        }
        else
        {
            FirewallToggle.IsEnabled = false;
            FirewallToggle.IsOn = false;
        }

        _loading = wasLoading;
    }

    private async void Firewall_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded)
        {
            return;
        }

        bool wanted = FirewallToggle.IsOn;

        // 已经是这个状态就别动：Toggled 会在 IsOn 赋值之后才投递，光靠 _loading 挡不住加载时的那一次
        if (FirewallControl.TryGetDisabled() == wanted)
        {
            return;
        }

        FirewallToggle.IsEnabled = false;
        Border? busy = Toast.Busy(wanted ? "正在关闭防火墙" : "正在开启防火墙", "等待管理员确认…");

        var (ok, error) = await Task.Run(() =>
        {
            bool succeeded = FirewallControl.Apply(wanted, out string message);
            return (succeeded, message);
        });

        // 以注册表里的真实状态为准
        if (FirewallControl.TryGetDisabled() is bool actual && actual != wanted)
        {
            _loading = true;
            FirewallToggle.IsOn = actual;
            _loading = false;
        }

        FirewallToggle.IsEnabled = true;
        Toast.Dismiss(busy);

        if (ok)
        {
            Toast.Success(wanted ? "防火墙已关闭" : "防火墙已开启", "专用网络 + 公用网络");
        }
        else
        {
            Toast.Error("切换失败", error);
        }
    }

    // ---------------- 关闭防病毒（Defender 组策略） ----------------

    /// <summary>把开关对齐策略键里的真实状态：DisableAntiSpyware = 1 才是「已启用」。</summary>
    private void LoadDefenderState()
    {
        bool wasLoading = _loading;
        _loading = true;
        DefenderToggle.IsOn = DefenderPolicy.TryGetDisabled();
        _loading = wasLoading;
    }

    private async void Defender_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded)
        {
            return;
        }

        bool wanted = DefenderToggle.IsOn;

        if (DefenderPolicy.TryGetDisabled() == wanted)
        {
            return;
        }

        DefenderToggle.IsEnabled = false;
        Border? busy = Toast.Busy(wanted ? "正在关闭防病毒" : "正在恢复防病毒", "等待管理员确认…");

        var (ok, error) = await Task.Run(() =>
        {
            bool succeeded = DefenderPolicy.Apply(wanted, out string message);
            return (succeeded, message);
        });

        bool actual = DefenderPolicy.TryGetDisabled();
        if (actual != wanted)
        {
            _loading = true;
            DefenderToggle.IsOn = actual;
            _loading = false;
        }

        DefenderToggle.IsEnabled = true;
        Toast.Dismiss(busy);

        if (ok)
        {
            if (wanted)
            {
                Toast.Success("已启用「关闭 Microsoft Defender 防病毒」", "启动优先级 / 常驻两项也已禁用 · 重启后生效", 6);
            }
            else
            {
                Toast.Success("已设为「未配置」", "重启后恢复 Defender 策略");
            }
        }
        else
        {
            Toast.Error("切换失败", error);
        }
    }

    // ---------------- 关闭防病毒(注册表)：一整组 Defender 值 ----------------

    private void LoadDefenderRegistryState()
    {
        bool wasLoading = _loading;
        _loading = true;
        DefenderRegistryToggle.IsOn = DefenderRegistryTweak.TryGetEnabled();
        _loading = wasLoading;
    }

    private async void DefenderRegistry_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded)
        {
            return;
        }

        bool wanted = DefenderRegistryToggle.IsOn;

        if (DefenderRegistryTweak.TryGetEnabled() == wanted)
        {
            return;
        }

        DefenderRegistryToggle.IsEnabled = false;
        Border? busy = Toast.Busy(wanted ? "正在写注册表" : "正在清理注册表", "等待管理员确认…");

        var (ok, error) = await Task.Run(() =>
        {
            bool succeeded = DefenderRegistryTweak.Apply(wanted, out string message);
            return (succeeded, message);
        });

        bool actual = DefenderRegistryTweak.TryGetEnabled();
        if (actual != wanted)
        {
            _loading = true;
            DefenderRegistryToggle.IsOn = actual;
            _loading = false;
        }

        DefenderRegistryToggle.IsEnabled = true;
        Toast.Dismiss(busy);

        // 这组值和「关闭防病毒」共用 DisableAntiSpyware / ServiceKeepAlive，顺手把那边刷一下
        LoadDefenderState();

        if (ok)
        {
            Toast.Success(
                wanted ? "已写入 6 个 Defender 策略值" : "已删除这 6 个值",
                wanted ? "重启后生效" : "恢复未配置状态",
                wanted ? 6 : 4);
        }
        else
        {
            Toast.Error("切换失败", error);
        }
    }

    // ---------------- 安全中心设置：一键关 / 开那一批防护 ----------------

    /// <summary>注册表那 7 项全关才算「开」（已关闭）。</summary>
    private void LoadSecurityCenterState()
    {
        bool wasLoading = _loading;
        _loading = true;
        SecurityCenterToggle.IsOn = SecurityCenterGuard.IsDisabled();
        _loading = wasLoading;
    }

    private async void SecurityCenter_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded)
        {
            return;
        }

        bool wanted = SecurityCenterToggle.IsOn;

        // 已经是这个状态就别动
        if (SecurityCenterGuard.IsDisabled() == wanted)
        {
            return;
        }

        SecurityCenterToggle.IsEnabled = false;
        Border? busy = Toast.Busy(
            wanted ? "正在关闭安全中心防护" : "正在恢复安全中心防护",
            "等待管理员确认…");

        var (ok, report) = await Task.Run(() =>
        {
            bool succeeded = SecurityCenterGuard.Run(wanted, out string log);
            return (succeeded, log);
        });

        // 以注册表真实状态为准
        _loading = true;
        SecurityCenterToggle.IsOn = SecurityCenterGuard.IsDisabled();
        _loading = false;
        SecurityCenterToggle.IsEnabled = true;
        Toast.Dismiss(busy);

        // 智能应用控制那条键和「关闭防病毒(注册表)」共用，顺手刷新一下
        LoadDefenderState();
        LoadDefenderRegistryState();

        if (ok)
        {
            DefenderState state = ParseState(report);
            string stateText = DescribeState(state);

            if (wanted && state.TamperProtection == true && state.RealTimeProtection != false)
            {
                // 篡改防护会把 Defender 那几项还原：脚本"成功"也不代表真关掉了
                Toast.Info(
                    "已写入，但实时保护还开着",
                    $"篡改防护=开 会把实时保护 / 云保护 / 样本提交还原。{stateText}",
                    12);
            }
            else if (wanted && state.RealTimeProtection == true)
            {
                Toast.Info("部分已关，实时保护还开着", stateText, 12);
            }
            else
            {
                Toast.Success(
                    wanted ? "安全中心防护已关闭" : "安全中心防护已恢复",
                    wanted
                        ? $"内核隔离三项要重启后生效。{stateText}"
                        : $"内核隔离三项要重启后生效。{stateText}",
                    8);
            }
        }
        else
        {
            Toast.Error("有项目没能改成功", FirstFailures(report), 12);
        }
    }

    // ---------------- 深度通道：已移除（篡改防护不在控制范围内） ----------------

    /// <summary>脚本末尾回读的 Defender 真实状态。</summary>
    private sealed class DefenderState
    {
        public bool? RealTimeProtection { get; init; }

        public bool? TamperProtection { get; init; }

        public int? Cloud { get; init; }

        public int? Samples { get; init; }

        public bool? AsyncInspection { get; init; }

        /// <summary>开发人员驱动器保护：0 = 关（异步扫描），1 = 开（同步扫描）。</summary>
        public int? DevDriveProtection { get; init; }
    }

    private static DefenderState ParseState(string report)
    {
        bool? rtp = null;
        bool? tamper = null;
        bool? async = null;
        int? cloud = null;
        int? samples = null;
        int? devDrive = null;

        foreach (string raw in report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (!line.StartsWith("STATE", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string part in line.Substring(5).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = part.Split('=');
                if (pair.Length != 2)
                {
                    continue;
                }

                string key = pair[0].Trim();
                string value = pair[1].Trim();

                switch (key)
                {
                    case "实时保护":
                        rtp = string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "篡改防护":
                        tamper = string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "云保护":
                        cloud = int.TryParse(value, out int c) ? c : null;
                        break;
                    case "样本提交":
                        samples = int.TryParse(value, out int s) ? s : null;
                        break;
                    case "异步检查":
                        async = string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "开发人员驱动器保护":
                        devDrive = int.TryParse(value, out int d) ? d : null;
                        break;
                }
            }
        }

        return new DefenderState
        {
            RealTimeProtection = rtp,
            TamperProtection = tamper,
            Cloud = cloud,
            Samples = samples,
            AsyncInspection = async,
            DevDriveProtection = devDrive,
        };
    }

    private static string DescribeState(DefenderState state)
    {
        var parts = new System.Collections.Generic.List<string>();

        if (state.RealTimeProtection is bool rtp)
        {
            parts.Add("实时保护=" + (rtp ? "开" : "关"));
        }

        if (state.TamperProtection is bool tamper)
        {
            parts.Add("篡改防护=" + (tamper ? "开" : "关"));
        }

        if (state.Cloud is int cloud)
        {
            parts.Add("云保护=" + cloud);
        }

        if (state.Samples is int samples)
        {
            parts.Add("样本提交=" + samples);
        }

        if (state.AsyncInspection is bool async)
        {
            parts.Add("异步检查=" + (async ? "允许" : "禁止"));
        }

        if (state.DevDriveProtection is int devDrive)
        {
            parts.Add("开发人员驱动器保护=" + (devDrive == 1 ? "开" : "关"));
        }

        return parts.Count > 0 ? "实际：" + string.Join(" ", parts) : string.Empty;
    }

    /// <summary>从 PowerShell 日志里挑出失败/警告行，最多 4 条。</summary>
    private static string FirstFailures(string report)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            return "没有拿到脚本输出";
        }

        var lines = new System.Collections.Generic.List<string>();
        foreach (string raw in report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.StartsWith("FAIL", StringComparison.Ordinal) || line.StartsWith("WARN", StringComparison.Ordinal))
            {
                lines.Add(line);
                if (lines.Count == 4)
                {
                    break;
                }
            }
        }

        return lines.Count > 0 ? string.Join("\n", lines) : report;
    }

    // ---------------- 系统激活 ----------------

    private async Task LoadActivationAsync()
    {
        bool? activated = await ActivationStatus.IsActivatedAsync();

        if (activated is null)
        {
            ActivationText.Text = "无法检测";
            return;
        }

        ActivationText.Text = activated.Value ? "已激活" : "未激活";
    }

    /// <summary>一键 KMS：交给管理员 PowerShell 依次跑 4 条 slmgr 命令（每条间隔 0.5 秒）。</summary>
    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        ActivateButton.IsEnabled = false;
        Border? busy = Toast.Busy("正在激活", "管理员 PowerShell 依次执行 4 条 slmgr 命令…");

        var (ok, report) = await Task.Run(() =>
        {
            bool succeeded = KmsActivation.Run(out string log);
            return (succeeded, log);
        });

        ActivateButton.IsEnabled = true;
        Toast.Dismiss(busy);

        if (ok)
        {
            Toast.Success("KMS 激活命令已全部执行完成", "可到「设置 → 系统 → 激活」确认状态", 6);
        }
        else
        {
            Toast.Error("激活失败", LastUsefulLine(report), 10);
        }

        // 回读一遍激活状态
        await LoadActivationAsync();
    }

    /// <summary>从 slmgr 输出里挑最后一条有用的行，用来显示失败原因。</summary>
    private static string LastUsefulLine(string report)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            return string.Empty;
        }

        string[] lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length > 0 && !line.StartsWith(">", StringComparison.Ordinal) && !line.StartsWith("==", StringComparison.Ordinal))
            {
                return line;
            }
        }

        return string.Empty;
    }

    // ---------------- 注册表权限 ----------------

    /// <summary>按当前用户是否已有完全控制，决定按钮是「添加权限」还是「已有权限」，并带出防火墙通知开关。</summary>
    private void RefreshAclButton()
    {
        bool granted = RegistryAcl.CheckFullControl(RegistryAcl.TargetSubKey) == AclState.Granted;

        GrantAclButton.Content = granted ? "已有权限" : "添加权限";
        GrantAclButton.IsEnabled = !granted;

        // 没权限时整块收起：注册表根本写不进去，摆个开关只会误导
        FirewallNotifySection.Visibility = granted ? Visibility.Visible : Visibility.Collapsed;

        if (granted)
        {
            LoadFirewallNotify();
        }
    }

    /// <summary>把「防火墙通知」开关对齐注册表里的真实 Start 值。</summary>
    private void LoadFirewallNotify()
    {
        bool wasLoading = _loading;
        _loading = true;
        FirewallNotifyToggle.IsOn = SecurityCenterNotify.TryGetEnabled() ?? false;
        _loading = wasLoading;
    }

    private void FirewallNotify_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        bool wanted = FirewallNotifyToggle.IsOn;
        bool ok = SecurityCenterNotify.TrySetEnabled(wanted);

        // 回读为准：系统实际值为准时纠正开关
        if (SecurityCenterNotify.TryGetEnabled() is bool actual && actual != wanted)
        {
            _loading = true;
            FirewallNotifyToggle.IsOn = actual;
            _loading = false;
        }

        if (ok)
        {
            Toast.Success(
                wanted ? "防火墙通知已开启" : "防火墙通知已关闭",
                wanted ? "Start = 2" : "Start = 4");
        }
        else
        {
            Toast.Error("写注册表失败", "需要该键的写权限，先点「添加权限」");
        }
    }

    private void GrantAcl_Click(object sender, RoutedEventArgs e)
    {
        GrantAclButton.IsEnabled = false;
        Border? busy = Toast.Busy("正在添加权限", RegistryAcl.TargetSubKey);

        try
        {
            bool ok = RegistryAcl.IsElevated
                ? RegistryAcl.Apply(RegistryAcl.TargetSubKey, out _)
                : RegistryAcl.ApplyViaElevatedSelf(RegistryAcl.TargetSubKey, out _);

            Toast.Dismiss(busy);

            if (ok)
            {
                Toast.Success("已添加权限", $"{RegistryAcl.CurrentUserDisplayName} 完全控制 + 所有者（含子键）", 6);
            }
            else
            {
                Toast.Error("添加失败", "没能写入权限，可能是取消了 UAC");
            }
        }
        finally
        {
            // 以检测结果为准：成功就变「已有权限」，失败/取消则继续显示「添加权限」
            RefreshAclButton();
        }
    }
}
