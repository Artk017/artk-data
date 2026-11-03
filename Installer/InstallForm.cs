using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace NovaSetup;

internal sealed class InstallForm : Form
{
    private readonly Label _titleLabel;
    private readonly Label _subLabel;
    private readonly Label _dirLabel;
    private readonly TextBox _dir;
    private readonly Button _browse;
    private readonly Button _resetDefault;
    private readonly CheckBox _desktop;
    private readonly CheckBox _launch;
    private readonly CheckBox? _service;
    private readonly Button _install;
    private readonly Button _cancel;
    private readonly Button _done;
    private readonly Label _status;
    private readonly ProgressBar _bar;
    private readonly bool _isUpdate;
    private readonly string _productName = ProductInfo.Current.DisplayName;
    private InstallOptions? _options;
    private bool _busy;

    public int ExitCode { get; private set; } = 1;

    public InstallForm(InstallOptions defaults)
    {
        ExistingInstall? existing = ExistingInstallDetector.Find();
        _isUpdate = existing is not null;
        string newVersion = Application.ProductVersion;
        string title = _isUpdate ? $"更新 {_productName}" : $"安装 {_productName}";

        // 服务端多一个「注册后台服务」选项，窗口相应高一点
        bool hasService = ProductInfo.Current.SupportsService;
        int height = hasService ? 330 : 305;
        int statusTop = hasService ? 230 : 204;
        int barTop = hasService ? 270 : 246;
        int buttonTop = hasService ? 282 : 260;

        Text = title;

        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = true;
        ShowInTaskbar = true;
        ClientSize = new Size(520, height);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.White;

        _titleLabel = new Label
        {
            Text = title,
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 16),
        };

        string subText = _isUpdate
            ? $"检测到已安装 {_productName}{(string.IsNullOrWhiteSpace(existing!.Version) ? string.Empty : "（版本 " + existing.Version + "）")}，将更新到 {newVersion}。"
            : hasService
                ? "选择安装位置，并可选择是否创建桌面快捷方式、注册后台服务、安装后打开设置界面。"
                : "选择安装位置，并可选择是否创建桌面快捷方式、安装后立即打开。";

        _subLabel = new Label
        {
            Text = subText,
            // 高度跟着行数走：安装路径长的时候这句话会折成三行，固定高度会把最后一行裁掉一半
            AutoSize = true,
            MaximumSize = new Size(480, 0),
            ForeColor = Color.FromArgb(90, 90, 90),
            Location = new Point(22, 54),
        };

        _dirLabel = new Label
        {
            Text = $"安装位置（自动使用「{ProductInfo.Current.InstallFolderName}」子目录）",
            AutoSize = true,
            Location = new Point(22, 92),
        };

        _dir = new TextBox
        {
            Text = _isUpdate ? existing!.InstallDir : defaults.InstallDir,
            Location = new Point(22, 114),
            Width = 360,
            ReadOnly = _isUpdate,
        };

        _browse = new Button { Text = "浏览…", Location = new Point(388, 113), Size = new Size(52, 25) };
        _resetDefault = new Button { Text = "默认", Location = new Point(444, 113), Size = new Size(52, 25) };

        if (_isUpdate)
        {
            _browse.Enabled = false;
            _resetDefault.Enabled = false;
        }

        _desktop = new CheckBox
        {
            Text = "创建桌面快捷方式",
            Checked = defaults.CreateDesktopShortcut,
            AutoSize = true,
            Location = new Point(22, 150),
        };

        _launch = new CheckBox
        {
            Text = hasService ? "安装完成后打开设置界面" : $"安装完成后立即打开 {_productName}",
            Checked = defaults.LaunchAfterInstall,
            AutoSize = true,
            Location = new Point(22, 176),
        };

        if (hasService)
        {
            _service = new CheckBox
            {
                Text = "注册开机自启后台服务（需管理员授权）",
                Checked = defaults.RegisterService,
                AutoSize = true,
                Location = new Point(22, 202),
            };
        }

        _status = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Location = new Point(22, statusTop),
            Size = new Size(474, 40),
            ForeColor = Color.FromArgb(90, 90, 90),
        };

        _bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Location = new Point(22, barTop),
            Size = new Size(474, 6),
            Visible = false,
        };

        _install = new Button
        {
            Text = _isUpdate ? "更新" : "安装",
            Location = new Point(330, buttonTop),
            Size = new Size(90, 30),
        };

        _cancel = new Button
        {
            Text = "取消",
            Location = new Point(426, buttonTop),
            Size = new Size(70, 30),
        };

        _done = new Button
        {
            Text = "确定",
            Location = new Point(426, buttonTop),
            Size = new Size(70, 30),
            Visible = false,
        };

        AcceptButton = _install;
        CancelButton = _cancel;

        Controls.AddRange(new Control[]
        {
            _titleLabel, _subLabel, _dirLabel, _dir, _browse, _resetDefault, _desktop, _launch,
            _status, _bar, _install, _cancel, _done,
        });

        if (_service is not null)
        {
            Controls.Add(_service);
        }

        ScaleForDpi();

        _browse.Click += (_, _) =>
        {
            if (_busy)
            {
                return;
            }

            using var dialog = new FolderBrowserDialog
            {
                SelectedPath = SafeStartingDir(_dir.Text),
                Description = $"选择 {_productName} 的安装位置",
                UseDescriptionForTitle = true,
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                // 选的是父目录（比如 G:\dow）就自动补成 G:\dow\Artk Server，装完不跟别的东西混在一起
                _dir.Text = InstallOptions.NormalizeProductDir(dialog.SelectedPath);
            }
        };

        _resetDefault.Click += (_, _) =>
        {
            if (!_busy)
            {
                _dir.Text = InstallOptions.DefaultInstallDir();
            }
        };

        _install.Click += Install_Click;
        _cancel.Click += (_, _) => CancelOrClose();
        _done.Click += Done_Click;
    }

    /// <summary>
    /// 按系统 DPI 放大界面。坐标都是按 96 DPI 写死的，而文字会跟着系统 DPI 变大，
    /// 控件不跟着放大，高分屏（125% / 150%）上标签就只露得出一半；
    /// WinForms 自带的自动缩放对这种纯代码创建的窗口不生效，只能自己算。
    /// </summary>
    private void ScaleForDpi()
    {
        float scale;

        using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
        {
            scale = graphics.DpiX / 96f;
        }

        if (scale <= 1.01f)
        {
            return;
        }

        ClientSize = new Size(
            (int)Math.Round(ClientSize.Width * scale),
            (int)Math.Round(ClientSize.Height * scale));

        foreach (Control control in Controls)
        {
            control.Location = new Point(
                (int)Math.Round(control.Left * scale),
                (int)Math.Round(control.Top * scale));

            // 自动尺寸的标签 / 勾选框已经按字体量过高度了，别再改
            if (!control.AutoSize)
            {
                control.Size = new Size(
                    (int)Math.Round(control.Width * scale),
                    (int)Math.Round(control.Height * scale));
            }

            // 折行宽度也要跟着放大，不然窗口变宽了文字还在窄处折行
            if (control.MaximumSize.Width > 0)
            {
                control.MaximumSize = new Size(
                    (int)Math.Round(control.MaximumSize.Width * scale),
                    control.MaximumSize.Height > 0 ? (int)Math.Round(control.MaximumSize.Height * scale) : 0);
            }
        }
    }

    private static string SafeStartingDir(string raw)
    {
        try
        {
            if (Directory.Exists(raw))
            {
                return raw;
            }
        }
        catch
        {
            // 忽略
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    private void CancelOrClose()
    {
        if (_busy)
        {
            _status.Text = "正在取消…";
            _cancel.Enabled = false;

            try
            {
                _cts?.Cancel();
            }
            catch
            {
                // 取消失败忽略
            }

            return;
        }

        Close();
    }

    private CancellationTokenSource? _cts;

    private async void Install_Click(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        string dir = _dir.Text.Trim();
        if (dir.Length == 0)
        {
            MessageBox.Show(this, "请填写安装位置。", _productName + " 安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Path.GetFullPath(dir);
        }
        catch
        {
            MessageBox.Show(this, "安装位置无效，请重新选择。", _productName + " 安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 手输 G:\dow 这类父目录的，也补成 G:\dow\Artk Server；界面上直接把最终位置显示出来
        dir = InstallOptions.NormalizeProductDir(dir);
        _dir.Text = dir;

        SetBusy(true);

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        _options = new InstallOptions
        {
            InstallDir = dir,
            CreateDesktopShortcut = _desktop.Checked,
            LaunchAfterInstall = _launch.Checked,
            RegisterService = _service?.Checked ?? false,
        };

        var progress = new Progress<string>(s =>
        {
            if (!IsDisposed)
            {
                _status.Text = s;
            }
        });

        int code = await Task.Run(() => SetupEngine.Run(_options, s => ((IProgress<string>)progress).Report(s), token));

        SetBusy(false);

        if (token.IsCancellationRequested || code == 2)
        {
            ExitCode = 2;
            if (!IsDisposed)
            {
                Close();
            }

            return;
        }

        if (code == 0)
        {
            ExitCode = 0;
            ShowDone();
            return;
        }

        ExitCode = code;
        MessageBox.Show(this, "安装没有成功，请根据上方提示处理后重试。", _productName + " 安装",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>安装成功后的完成页：先点「确定」才创建桌面快捷方式 / 注册服务 / 打开程序。</summary>
    private void ShowDone()
    {
        _titleLabel.Text = _isUpdate ? "更新完成" : "安装完成";
        _subLabel.Text = _isUpdate
            ? $"{_productName} 已更新到 {Application.ProductVersion}。"
            : $"{_productName} 已安装到 {_dir.Text}。";

        _subLabel.Text += Environment.NewLine + "点击「确定」执行后续操作并关闭安装程序。";

        _dirLabel.Visible = false;
        _dir.Visible = false;
        _browse.Visible = false;
        _resetDefault.Visible = false;
        _desktop.Visible = false;
        _launch.Visible = false;
        if (_service is not null)
        {
            _service.Visible = false;
        }

        _bar.Visible = false;
        _install.Visible = false;
        _cancel.Visible = false;
        _status.Text = string.Empty;

        _done.Visible = true;
        AcceptButton = _done;
        CancelButton = null;
        _done.Focus();
    }

    private void Done_Click(object? sender, EventArgs e)
    {
        _done.Enabled = false;

        if (_options is { } options)
        {
            try
            {
                SetupEngine.ApplyPostInstall(options, s =>
                {
                    if (!IsDisposed)
                    {
                        _status.Text = s;
                    }
                });
            }
            catch
            {
                // 快捷方式 / 服务 / 启动失败不影响安装结果
            }
        }

        Close();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _dir.Enabled = !busy;
        _desktop.Enabled = !busy;
        _launch.Enabled = !busy;
        if (_service is not null)
        {
            _service.Enabled = !busy;
        }

        _install.Enabled = !busy;
        _cancel.Enabled = true;
        _bar.Visible = busy;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy)
        {
            try
            {
                _cts?.Cancel();
            }
            catch
            {
                // 忽略
            }

            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }
}
