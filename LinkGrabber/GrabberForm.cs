using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NovaLinkGrabber;

/// <summary>
/// 小工具：左边选软件，中间是真的浏览器，你在里面点厂商的「下载」按钮，
/// 工具把下载开始的真实地址截下来，存给 Artk Tools 用。
/// </summary>
internal sealed class GrabberForm : Form
{
    private readonly ComboBox _apps = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill,
    };

    private readonly TextBox _address = new()
    {
        Dock = DockStyle.Fill,
        Font = new Font("Microsoft YaHei UI", 9F),
    };

    private readonly Button _open = new()
    {
        Text = "打开页面",
        Dock = DockStyle.Fill,
    };

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    private readonly TextBox _link = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        Font = new Font("Consolas", 9F),
    };

    private readonly Button _copy = new() { Text = "复制", Dock = DockStyle.Fill };

    private readonly Button _save = new() { Text = "保存给 Artk Tools", Dock = DockStyle.Fill, Enabled = false };

    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Text = "选一个软件 → 点「打开页面」→ 在网页里点厂商的下载按钮，链接会自动抓到",
    };

    private CoreWebView2? _core;
    private string _currentPage = string.Empty;
    private string? _captured;

    public GrabberForm()
    {
        Text = "Artk Tools 直链工具";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1120, 780);
        MinimumSize = new Size(820, 600);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();

        _open.Click += (_, _) => Navigate();
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                Navigate();
                e.SuppressKeyPress = true;
            }
        };
        _apps.SelectedIndexChanged += (_, _) => SelectApp();
        _copy.Click += (_, _) => CopyLink();
        _save.Click += (_, _) => SaveLink();

        LoadApps();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96F));

        // ── 顶部：软件下拉 + 地址栏 ──
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(10, 8, 10, 4),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200F));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));

        top.Controls.Add(MakeLabel("软件"), 0, 0);
        top.Controls.Add(_apps, 1, 0);
        top.Controls.Add(MakeLabel("页面"), 2, 0);
        top.Controls.Add(_address, 3, 0);
        top.Controls.Add(_open, 4, 0);

        // ── 底部：抓到的链接 + 状态 ──
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            Padding = new Padding(10, 4, 10, 8),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70F));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        bottom.Controls.Add(MakeLabel("链接"), 0, 0);
        bottom.Controls.Add(_link, 1, 0);
        bottom.Controls.Add(_copy, 2, 0);
        bottom.Controls.Add(_save, 3, 0);
        bottom.Controls.Add(_status, 1, 1);
        bottom.SetColumnSpan(_status, 3);

        root.Controls.Add(top, 0, 0);
        root.Controls.Add(_web, 0, 1);
        root.Controls.Add(bottom, 0, 2);

        Controls.Add(root);
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 0, 8, 0),
    };

    private void LoadApps()
    {
        List<CatalogEntry> catalog = Store.LoadCatalog();
        Dictionary<string, StoredLink> links = Store.LoadLinks();

        _apps.Items.Clear();
        foreach (CatalogEntry entry in catalog)
        {
            bool done = links.ContainsKey(entry.Name);
            _apps.Items.Add(new AppChoice(entry, done));
        }

        if (_apps.Items.Count > 0)
        {
            _apps.SelectedIndex = 0;
        }
    }

    private void SelectApp()
    {
        if (_apps.SelectedItem is not AppChoice choice)
        {
            return;
        }

        _address.Text = choice.Entry.Page;
        _captured = null;
        _link.Clear();
        _save.Enabled = false;

        if (Store.LoadLinks().TryGetValue(choice.Entry.Name, out StoredLink? stored))
        {
            _link.Text = stored.Url;
            SetStatus($"「{choice.Entry.Name}」已经存过直链（{stored.CapturedAt}），可以直接下载；重新抓一遍会覆盖");
        }
        else
        {
            SetStatus($"「{choice.Entry.Name}」还没存直链：点「打开页面」，在网页里点厂商的下载按钮");
        }
    }

    private void Navigate()
    {
        if (_core is null)
        {
            SetStatus("浏览器还在初始化，稍等一下");
            return;
        }

        string text = _address.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (!text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text;
        }

        try
        {
            _core.Navigate(text);
        }
        catch (Exception ex)
        {
            SetStatus("打不开这个地址：" + ex.Message);
        }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            string profile = Path.Combine(Store.Folder, "grabber-profile");
            Directory.CreateDirectory(profile);

            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, profile);
            await _web.EnsureCoreWebView2Async(environment);

            _core = _web.CoreWebView2;
            _core.Settings.AreDevToolsEnabled = false;
            _core.Settings.AreDefaultContextMenusEnabled = true;
            _core.DownloadStarting += OnDownloadStarting;
            _core.NewWindowRequested += OnNewWindowRequested;
            _core.SourceChanged += (_, _) =>
            {
                _currentPage = _core.Source ?? string.Empty;
                if (_currentPage.Length > 0 && _address.Text != _currentPage)
                {
                    _address.Text = _currentPage;
                }
            };

            SetStatus("就绪：选一个软件 → 点「打开页面」→ 在网页里点下载按钮");
        }
        catch (Exception ex)
        {
            SetStatus("WebView2 初始化失败（可能没装 Edge WebView2 运行时）：" + ex.Message);
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // 自己不要真下载，只把地址截下来交给 Artk Tools
        e.Cancel = true;

        string uri = e.DownloadOperation.Uri;
        if (!uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _captured = uri;
        _link.Text = uri;
        _save.Enabled = true;
        _copy.Enabled = true;

        bool signed = uri.Contains("?sign=") || uri.Contains("auth_key") || uri.Contains("token=");
        SetStatus(signed
            ? "抓到了（链接带签名，最好马上用）→ 点「保存给 Artk Tools」"
            : "抓到了 → 点「保存给 Artk Tools」，之后在 Artk Tools 里点下载就能直接下");
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // 有些下载是弹新窗口打开的，拦下来在当前页面加载
        e.Handled = true;
        _core?.Navigate(e.Uri);
    }

    private void CopyLink()
    {
        if (_link.Text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_link.Text);
            SetStatus("已复制到剪贴板");
        }
        catch (Exception)
        {
            SetStatus("复制失败，手动选中复制吧");
        }
    }

    private void SaveLink()
    {
        if (_captured is null)
        {
            SetStatus("还没有抓到链接：先在网页里点厂商的下载按钮");
            return;
        }

        if (_apps.SelectedItem is not AppChoice choice)
        {
            SetStatus("先在上面选一个软件");
            return;
        }

        try
        {
            Store.StoreLink(choice.Entry.Name, _captured, _currentPage);
            SetStatus($"已保存：「{choice.Entry.Name}」→ 回到 Artk Tools 的软件页就能直接下载");

            int index = _apps.SelectedIndex;
            LoadApps();
            _apps.SelectedIndex = index;
        }
        catch (Exception ex)
        {
            SetStatus("保存失败：" + ex.Message);
        }
    }

    private void SetStatus(string text) => _status.Text = text;

    /// <summary>下拉框里的一项；已经存过直链的后面带个点。</summary>
    private sealed class AppChoice
    {
        public AppChoice(CatalogEntry entry, bool done)
        {
            Entry = entry;
            Done = done;
        }

        public CatalogEntry Entry { get; }

        public bool Done { get; }

        public override string ToString() => Done ? Entry.Name + "  ●" : Entry.Name;
    }
}
