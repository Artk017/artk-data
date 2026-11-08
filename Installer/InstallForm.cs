using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace NovaSetup;

/// <summary>
/// 经典向导样式的安装程序：白色内容区 + 右上角插画 + 灰色底栏按钮。
/// 第一页选安装位置和选项，第二页显示实时进度与日志。
/// </summary>
internal sealed class InstallForm : Form
{
    private const int WindowWidth = 720;
    private const int Pad = 36;
    private const int HeaderHeight = 96;
    private const int FooterTop = 416;
    private const int WindowHeight = 480;
    private const int ContentWidth = WindowWidth - Pad * 2;
    private const int ButtonHeight = 32;
    private const int PrimaryWidth = 108;
    private const int SecondaryWidth = 96;

    private static readonly Color FooterBack = Color.FromArgb(0xF3, 0xF3, 0xF3);

    private readonly string _productName = ProductInfo.Current.DisplayName;
    private readonly string _windowTitle;
    private readonly bool _isUpdate;
    private readonly bool _hasService;

    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly InstallGlyph _art = new();

    private readonly Panel _page1 = new();
    private readonly Label _dirLabel = new();
    private readonly TextBox _dir = new();
    private readonly ModernButton _browse = new();
    private readonly Label _hint = new();
    private readonly Label _optionsLabel = new();
    private readonly CheckBox _desktop = new();
    private readonly CheckBox _launch = new();
    private CheckBox? _service;

    private readonly Panel _page2 = new();
    private readonly Label _status = new();
    private readonly Label _percent = new();
    private readonly AccentProgressBar _bar = new();
    private readonly TextBox _log = new();

    private readonly Panel _footer = new();
    private readonly Panel _footerLine = new();
    private readonly ModernButton _primary = new();
    private readonly ModernButton _secondary = new();

    private InstallOptions? _options;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _finished;

    public int ExitCode { get; private set; } = 1;

    public InstallForm(InstallOptions defaults)
    {
        ExistingInstall? existing = ExistingInstallDetector.Find();
        _isUpdate = existing is not null;
        _hasService = ProductInfo.Current.SupportsService;
        _windowTitle = _isUpdate ? $"更新 {_productName}" : $"安装 {_productName}";

        Text = _windowTitle;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Palette.Card;
        ForeColor = Palette.TextPrimary;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = true;
        ShowInTaskbar = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(WindowWidth, WindowHeight);

        BuildHeader(existing);
        BuildPage1(defaults, existing);
        BuildPage2();
        BuildFooter();

        Controls.AddRange(new Control[]
        {
            _title, _subtitle, _art, _page1, _page2, _footerLine, _footer,
        });

        _footer.Controls.AddRange(new Control[] { _primary, _secondary });
        ScaleForDpi();

        _browse.Click += (_, _) => BrowseDir();
        _primary.Click += Primary_Click;
        _secondary.Click += (_, _) => CancelOrClose();

        AcceptButton = _primary;
        CancelButton = _secondary;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        _dir.SelectionLength = 0;
        _primary.Focus();
    }

    // ---------------- 界面搭建（坐标按 96 DPI 写） ----------------

    private void BuildHeader(ExistingInstall? existing)
    {
        _title.Text = _windowTitle;
        _title.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
        _title.ForeColor = Palette.TextPrimary;
        _title.BackColor = Palette.Card;
        _title.AutoSize = true;
        _title.Location = new Point(Pad, 26);

        _subtitle.Text = BuildSubtitle(existing);
        _subtitle.ForeColor = Palette.TextSecondary;
        _subtitle.BackColor = Palette.Card;
        _subtitle.AutoSize = true;
        _subtitle.MaximumSize = new Size(ContentWidth - 90, 0);
        _subtitle.Location = new Point(Pad, 60);

        _art.Location = new Point(WindowWidth - Pad - 64, 24);
        _art.Size = new Size(64, 64);
        _art.BackColor = Palette.Card;

        try
        {
            Icon? appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            if (appIcon is not null)
            {
                Icon = appIcon;
            }
        }
        catch
        {
            // 图标读不到就不显示，不影响安装
        }
    }

    private string BuildSubtitle(ExistingInstall? existing)
    {
        if (_isUpdate && existing is not null)
        {
            string current = string.IsNullOrWhiteSpace(existing.Version)
                ? string.Empty
                : $"（当前 {existing.Version}）";

            return $"已安装 {_productName}{current}，将更新到 {Application.ProductVersion}。";
        }

        return _hasService
            ? "选择安装位置后点击「下一步」，可安装桌面快捷方式和后台服务。"
            : $"选择 {_productName} 的安装位置，然后点击「下一步」。";
    }

    private void BuildPage1(InstallOptions defaults, ExistingInstall? existing)
    {
        _page1.Location = new Point(0, HeaderHeight);
        _page1.Size = new Size(WindowWidth, FooterTop - HeaderHeight);
        _page1.BackColor = Palette.Card;

        _dirLabel.Text = "安装位置";
        _dirLabel.ForeColor = Palette.TextSecondary;
        _dirLabel.BackColor = Palette.Card;
        _dirLabel.AutoSize = true;
        _dirLabel.Location = new Point(Pad, 14);

        _dir.Text = _isUpdate ? existing!.InstallDir : defaults.InstallDir;
        _dir.Font = new Font("Microsoft YaHei UI", 9.5F);
        _dir.BackColor = Palette.Card;
        _dir.ForeColor = Palette.TextPrimary;
        _dir.BorderStyle = BorderStyle.FixedSingle;
        _dir.ReadOnly = _isUpdate;
        _dir.Location = new Point(Pad, 38);
        _dir.Size = new Size(ContentWidth - 114, 28);

        _browse.Text = "浏览…";
        _browse.Filled = false;
        _browse.Font = new Font("Microsoft YaHei UI", 9F);
        _browse.BackColor = Palette.Card;
        _browse.Location = new Point(WindowWidth - Pad - 104, 36);
        _browse.Size = new Size(104, 32);
        _browse.Enabled = !_isUpdate;

        _hint.Text = _isUpdate
            ? "更新会保留原有安装位置。"
            : $"会自动创建「{ProductInfo.Current.InstallFolderName}」子目录。";
        _hint.Font = new Font("Microsoft YaHei UI", 8.25F);
        _hint.ForeColor = Palette.TextSecondary;
        _hint.BackColor = Palette.Card;
        _hint.AutoSize = true;
        _hint.Location = new Point(Pad, 74);

        _optionsLabel.Text = "选项";
        _optionsLabel.ForeColor = Palette.TextSecondary;
        _optionsLabel.BackColor = Palette.Card;
        _optionsLabel.AutoSize = true;
        _optionsLabel.Location = new Point(Pad, 118);

        _desktop.Text = "创建桌面快捷方式";
        _desktop.Checked = defaults.CreateDesktopShortcut;
        _desktop.ForeColor = Palette.TextPrimary;
        _desktop.BackColor = Palette.Card;
        _desktop.AutoSize = true;
        _desktop.Location = new Point(Pad, 142);

        _launch.Text = _hasService
            ? "安装完成后打开设置界面"
            : $"安装完成后立即打开 {_productName}";
        _launch.Checked = defaults.LaunchAfterInstall;
        _launch.ForeColor = Palette.TextPrimary;
        _launch.BackColor = Palette.Card;
        _launch.AutoSize = true;
        _launch.Location = new Point(Pad, 170);

        _page1.Controls.AddRange(new Control[] { _dirLabel, _dir, _browse, _hint, _optionsLabel, _desktop, _launch });

        if (_hasService)
        {
            var service = new CheckBox
            {
                Text = "注册开机自启后台服务（需管理员授权）",
                Checked = defaults.RegisterService,
                ForeColor = Palette.TextPrimary,
                BackColor = Palette.Card,
                AutoSize = true,
                Location = new Point(Pad, 198),
            };

            _service = service;
            _page1.Controls.Add(service);
        }
    }

    private void BuildPage2()
    {
        _page2.Location = new Point(0, HeaderHeight);
        _page2.Size = new Size(WindowWidth, FooterTop - HeaderHeight);
        _page2.BackColor = Palette.Card;
        _page2.Visible = false;

        _status.Text = "正在准备…";
        _status.ForeColor = Palette.TextSecondary;
        _status.BackColor = Palette.Card;
        _status.AutoSize = false;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Location = new Point(Pad, 14);
        _status.Size = new Size(ContentWidth - 90, 20);

        _percent.Text = "0%";
        _percent.ForeColor = Palette.TextSecondary;
        _percent.BackColor = Palette.Card;
        _percent.AutoSize = false;
        _percent.TextAlign = ContentAlignment.MiddleRight;
        _percent.Location = new Point(WindowWidth - Pad - 90, 14);
        _percent.Size = new Size(90, 20);

        _bar.Location = new Point(Pad, 42);
        _bar.Size = new Size(ContentWidth, 8);

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.WordWrap = false;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.BackColor = Palette.Card;
        _log.ForeColor = Palette.TextPrimary;
        _log.Font = new Font("Consolas", 9F);
        _log.TabStop = false;
        _log.Location = new Point(Pad, 62);
        _log.Size = new Size(ContentWidth, 242);

        _page2.Controls.AddRange(new Control[] { _status, _percent, _bar, _log });
    }

    private void BuildFooter()
    {
        _footerLine.Location = new Point(0, FooterTop);
        _footerLine.Size = new Size(WindowWidth, 1);
        _footerLine.BackColor = Palette.Border;

        _footer.Location = new Point(0, FooterTop + 1);
        _footer.Size = new Size(WindowWidth, WindowHeight - FooterTop - 1);
        _footer.BackColor = FooterBack;

        _primary.Text = "下一步";
        _primary.Filled = true;
        _primary.Font = new Font("Microsoft YaHei UI", 9F);
        _primary.BackColor = FooterBack;
        _primary.Location = new Point(WindowWidth - Pad - PrimaryWidth, 16);
        _primary.Size = new Size(PrimaryWidth, ButtonHeight);

        _secondary.Text = "取消";
        _secondary.Filled = false;
        _secondary.Font = new Font("Microsoft YaHei UI", 9F);
        _secondary.BackColor = FooterBack;
        _secondary.Location = new Point(
            WindowWidth - Pad - PrimaryWidth - 10 - SecondaryWidth, 16);
        _secondary.Size = new Size(SecondaryWidth, ButtonHeight);
    }

    /// <summary>
    /// 按系统 DPI 放大坐标。坐标都是按 96 DPI 写死的；字体的磅值会随 DPI 自动变大，
    /// 所以只放大位置和尺寸就够了（嵌套页面里的控件一起递归处理）。
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

        ScaleLayout(this, scale);
    }

    private static void ScaleLayout(Control parent, float scale)
    {
        foreach (Control control in parent.Controls)
        {
            control.Location = new Point(
                (int)Math.Round(control.Left * scale),
                (int)Math.Round(control.Top * scale));

            // 标签 / 勾选框是自动尺寸的，已经按字体量过；文本框（包括 AutoSize 默认为 true 的多行框）要手动缩放
            if (control is not Label && control is not CheckBox)
            {
                control.Size = new Size(
                    (int)Math.Round(control.Width * scale),
                    (int)Math.Round(control.Height * scale));
            }

            if (control.MaximumSize.Width > 0)
            {
                control.MaximumSize = new Size(
                    (int)Math.Round(control.MaximumSize.Width * scale),
                    control.MaximumSize.Height > 0 ? (int)Math.Round(control.MaximumSize.Height * scale) : 0);
            }

            ScaleLayout(control, scale);
        }
    }

    // ---------------- 交互 ----------------

    private void BrowseDir()
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
            // 选的是父目录（比如 G:\dow）就自动补成 G:\dow\Artk Tools，装完不跟别的东西混在一起
            _dir.Text = InstallOptions.NormalizeProductDir(dialog.SelectedPath);
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

    private async void Primary_Click(object? sender, EventArgs e)
    {
        if (_page1.Visible)
        {
            await StartInstallAsync();
            return;
        }

        if (_finished)
        {
            Done();
            return;
        }

        // 安装失败时按钮是「关闭」
        Close();
    }

    private void CancelOrClose()
    {
        if (_busy)
        {
            _status.Text = "正在取消…";
            _secondary.Enabled = false;

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

    // ---------------- 安装 ----------------

    private async Task StartInstallAsync()
    {
        if (_busy)
        {
            return;
        }

        string dir = _dir.Text.Trim();

        if (dir.Length == 0)
        {
            MessageBox.Show(this, "请填写安装位置。", _productName + " 安装",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Path.GetFullPath(dir);
        }
        catch
        {
            MessageBox.Show(this, "安装位置无效，请重新选择。", _productName + " 安装",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 手输 G:\dow 这类父目录的，也补成 G:\dow\Artk Tools
        dir = InstallOptions.NormalizeProductDir(dir);
        _dir.Text = dir;

        var options = new InstallOptions
        {
            InstallDir = dir,
            CreateDesktopShortcut = _desktop.Checked,
            LaunchAfterInstall = _launch.Checked,
            RegisterService = _service?.Checked ?? false,
        };

        _options = options;

        _page1.Visible = false;
        _page2.Visible = true;
        _title.Text = _isUpdate ? $"正在更新 {_productName}" : $"正在安装 {_productName}";
        _subtitle.Text = "安装过程会自动完成，请稍候。";
        _status.Text = _isUpdate ? "正在更新…" : "正在安装…";
        _bar.Value = 0;
        _percent.Text = "0%";
        _log.Text = string.Empty;

        _primary.Text = "安装中…";
        _primary.Enabled = false;
        _secondary.Text = "取消";
        _secondary.Enabled = true;
        _secondary.Visible = true;
        AcceptButton = null;

        AppendLog(_isUpdate ? "开始更新…" : "开始安装…");
        SetBusy(true);

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        // 三个阶段：步骤文案、百分比、Velopack 的原始输出
        var status = new Progress<string>(text =>
        {
            if (IsDisposed)
            {
                return;
            }

            _status.Text = text;
            AppendLog(text);
        });

        var detail = new Progress<string>(AppendLog);

        var progress = new Progress<int>(value =>
        {
            if (IsDisposed)
            {
                return;
            }

            _bar.Value = value;
            _percent.Text = value + "%";
        });

        int code = await Task.Run(() => SetupEngine.Run(
            options,
            text => ((IProgress<string>)status).Report(text),
            progress,
            text => ((IProgress<string>)detail).Report(text),
            token));

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
        _bar.Value = 0;
        _title.Text = "安装没有成功";
        _subtitle.Text = "请查看日志，处理后可关闭窗口重新安装。";
        _status.Text = "安装失败。";
        _primary.Text = "关闭";
        _primary.Visible = true;
        _primary.Enabled = true;
        AcceptButton = _primary;
        _secondary.Visible = false;
    }

    /// <summary>安装成功：进度走满，按钮换成「完成」，点它才执行收尾（快捷方式 / 服务 / 启动）。</summary>
    private void ShowDone()
    {
        _finished = true;

        _bar.Value = 100;
        _percent.Text = "100%";
        _title.Text = _isUpdate ? "更新完成" : "安装完成";
        _subtitle.Text = _isUpdate
            ? $"{_productName} 已更新到 {Application.ProductVersion}。"
            : $"{_productName} 已安装到 {_dir.Text}。";
        _status.Text = "点击「完成」关闭窗口。";
        AppendLog(_isUpdate ? "更新完成。" : "安装完成。");

        _primary.Text = "完成";
        _primary.Visible = true;
        _primary.Enabled = true;
        _secondary.Visible = false;
        AcceptButton = _primary;
        _primary.Focus();
    }

    private void Done()
    {
        _primary.Enabled = false;

        if (_options is { } options)
        {
            try
            {
                SetupEngine.ApplyPostInstall(options, AppendLog);
            }
            catch
            {
                // 快捷方式 / 服务 / 启动失败不影响安装结果
            }
        }

        Close();
    }

    private void AppendLog(string line)
    {
        if (IsDisposed || line.Length == 0)
        {
            return;
        }

        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _dir.Enabled = !busy;
        _browse.Enabled = !busy && !_isUpdate;
        _desktop.Enabled = !busy;
        _launch.Enabled = !busy;

        if (_service is not null)
        {
            _service.Enabled = !busy;
        }
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
