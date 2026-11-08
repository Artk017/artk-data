using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using ArtkTools.Helpers;
using ArtkTools.Services;
using Windows.Graphics;

namespace ArtkTools.Views;

/// <summary>
/// 「添加软件」窗口：真的浏览器 + 点下载就抓直链。
/// 用户打开厂商官网，在网页里点下载按钮，WebView2 的 DownloadStarting 会给出真实地址；
/// 拦下这次下载，把地址、当前页面一起预填进确认框，确认后直接写进服务器清单。
/// </summary>
public sealed partial class AddSoftwareWindow : Window
{
    private readonly WindowSizeLimits _limits;
    private CoreWebView2? _core;
    private bool _confirming;
    private List<string> _iconCandidates = new();

    // 主窗口关闭时要连这些浏览器窗口一起收掉，否则进程会被它们吊着不退出
    private static readonly List<AddSoftwareWindow> Live = new();
    private static readonly object LiveGate = new();

    public AddSoftwareWindow()
    {
        InitializeComponent();

        Title = "添加软件 · 在网页里点下载";

        // 和主窗口一样跟随设置里的主题
        RootGrid.RequestedTheme = AppSettings.Current.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
        }

        _limits = WindowSizeLimits.Apply(
            WinRT.Interop.WindowNative.GetWindowHandle(this), 900, 620);

        ResizeAndCenter();

        RootGrid.Loaded += OnRootLoaded;

        lock (LiveGate)
        {
            Live.Add(this);
        }

        Closed += (_, _) =>
        {
            lock (LiveGate)
            {
                Live.Remove(this);
            }

            _limits.Dispose();
        };
    }

    /// <summary>关掉所有还开着的「添加软件」窗口（应用退出时调用）。</summary>
    public static void CloseAll()
    {
        AddSoftwareWindow[] windows;

        lock (LiveGate)
        {
            windows = Live.ToArray();
            Live.Clear();
        }

        foreach (AddSoftwareWindow window in windows)
        {
            window.Close();
        }
    }

    /// <summary>本窗口是否成功添加过软件，软件页据此决定要不要重新拉清单。</summary>
    public bool AddedAny { get; private set; }

    private void ResizeAndCenter()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;

        int width = Math.Min(1180, Math.Max(900, work.Width - 80));
        int height = Math.Min(820, Math.Max(620, work.Height - 80));

        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2)));
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            string profile = Path.Combine(CatalogServer.Folder, "webview2");
            Directory.CreateDirectory(profile);

            CoreWebView2Environment environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, null);
            await Browser.EnsureCoreWebView2Async(environment);

            _core = Browser.CoreWebView2;
            _core.Settings.AreDevToolsEnabled = false;
            _core.Settings.IsStatusBarEnabled = false;

            _core.DownloadStarting += OnDownloadStarting;
            _core.NewWindowRequested += OnNewWindowRequested;
            _core.SourceChanged += OnSourceChanged;
            _core.HistoryChanged += OnHistoryChanged;
            _core.NavigationStarting += OnNavigationStarting;
            _core.NavigationCompleted += OnNavigationCompleted;

            StartOverlay.Visibility = Visibility.Collapsed;
            AddressBox.Focus(FocusState.Programmatic);
            UpdateHistoryButtons();
        }
        catch (Exception ex)
        {
            StartRing.IsActive = false;
            StartRing.Visibility = Visibility.Collapsed;
            StartText.Text = "浏览器启动失败：" + ex.Message + "\n\n需要安装 Microsoft Edge WebView2 运行时。";
            RuntimeButton.Visibility = Visibility.Visible;
            StatusText.Text = "浏览器不可用，装好运行时后重新打开本窗口。";
        }
    }

    // ---------------- 地址栏 / 导航 ----------------

    private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            Navigate();
        }
    }

    private void Go_Click(object sender, RoutedEventArgs e) => Navigate();

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_core?.CanGoBack == true)
        {
            _core.GoBack();
        }
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_core?.CanGoForward == true)
        {
            _core.GoForward();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _core?.Reload();

    private void Navigate()
    {
        if (_core is null)
        {
            return;
        }

        string input = AddressBox.Text.Trim();
        if (input.Length == 0)
        {
            return;
        }

        // 不是网址的（没域名、带空格、纯中文关键词）就当搜索词，交给必应
        string target = input.Contains("://", StringComparison.Ordinal)
            ? input
            : LooksLikeAddress(input)
                ? "https://" + input
                : "https://www.bing.com/search?q=" + Uri.EscapeDataString(input);

        try
        {
            _core.Navigate(target);
        }
        catch (Exception ex)
        {
            StatusText.Text = "网址打不开：" + ex.Message;
        }
    }

    /// <summary>粗略判断输入像不像网址：localhost、IP 或者带点的域名。</summary>
    private static bool LooksLikeAddress(string value)
    {
        string host = value.Split('/', '?', '#')[0];

        int colon = host.IndexOf(':');
        if (colon >= 0 && host[(colon + 1)..].All(char.IsDigit))
        {
            host = host[..colon];
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return host.Length > 0 &&
               !host.Any(char.IsWhiteSpace) &&
               host.Contains('.') &&
               !host.StartsWith('.') &&
               !host.EndsWith('.');
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        LoadingRing.IsActive = true;
        StatusText.Text = "正在加载页面…";
        _iconCandidates = new();

        if (!string.IsNullOrWhiteSpace(args.Uri))
        {
            AddressBox.Text = args.Uri;
        }
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        LoadingRing.IsActive = false;
        UpdateHistoryButtons();

        if (!args.IsSuccess)
        {
            StatusText.Text = "页面打不开，检查网址或网络后再试。";
            return;
        }

        AddressBox.Text = sender.Source ?? AddressBox.Text;
        _ = RefreshIconCandidatesAsync();
    }

    private void OnSourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
    {
        string source = sender.Source ?? string.Empty;

        if (source.Length > 0 && source != "about:blank" && AddressBox.Text != source)
        {
            AddressBox.Text = source;
        }

        UpdateHistoryButtons();
    }

    private void OnHistoryChanged(CoreWebView2 sender, object args) => UpdateHistoryButtons();

    private void UpdateHistoryButtons()
    {
        BackButton.IsEnabled = _core?.CanGoBack == true;
        ForwardButton.IsEnabled = _core?.CanGoForward == true;
    }

    // ---------------- 抓下载地址 ----------------

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // 不真的下载，只把地址截下来
        e.Cancel = true;

        string uri = e.DownloadOperation.Uri;
        if (!uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? file = Path.GetFileName(e.ResultFilePath);

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_confirming)
            {
                return;
            }

            string page = _core?.Source ?? string.Empty;
            string name = DeriveName(_core?.DocumentTitle, file);
            _ = AddFromDownloadAsync(name, page, uri);
        });
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // 有些站点在新窗口里发下载，拦下来在当前页面打开
        e.Handled = true;
        _core?.Navigate(e.Uri);
    }

    private async Task AddFromDownloadAsync(string name, string page, string link)
    {
        _confirming = true;

        try
        {
            StatusText.Text = "抓到了下载地址，确认一下再添加。";

            var draft = new AppEntry
            {
                Name = name,
                HomeUrl = page.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? page : link,
                DirectUrl = link,
            };

            AppEntry? entry = await AppEditorDialog.ShowAsync(
                RootGrid.XamlRoot, draft, await CollectIconsForDialogAsync());

            if (entry is null)
            {
                StatusText.Text = "已取消，可以回网页重新点下载。";
                return;
            }

            try
            {
                await CatalogServer.SaveAsync(AppCatalog.ToDto(entry));
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("保存失败", ex.Message);
                StatusText.Text = "没有保存成功，可以再试一次。";
                return;
            }

            AddedAny = true;
            Toast.Success("已添加软件", entry.Name + " · 软件页里可以直接下载", 5);
            Close();
        }
        finally
        {
            _confirming = false;
        }
    }

    /// <summary>页面加载完先扫一遍图标，用户点下载时就能直接拿来选，也顺便给个数量提示。</summary>
    private async Task RefreshIconCandidatesAsync()
    {
        _iconCandidates = await CollectIconCandidatesAsync();

        if (_confirming)
        {
            return;
        }

        StatusText.Text = _iconCandidates.Count > 0
            ? $"在网页里点厂商的「下载」按钮（这一页找到 {_iconCandidates.Count} 个可用图标）。"
            : "在网页里点厂商的「下载」按钮，就会自动抓取直链并弹出添加确认。";
    }

    private async Task<List<string>> CollectIconsForDialogAsync() =>
        _iconCandidates.Count > 0 ? _iconCandidates : await CollectIconCandidatesAsync();

    /// <summary>把当前网页里的图标类图片挑出来，最多 12 个给用户选。</summary>
    private async Task<List<string>> CollectIconCandidatesAsync()
    {
        var urls = new List<string>();

        if (_core is null)
        {
            return urls;
        }

        try
        {
            string json = await _core.ExecuteScriptAsync(IconScript);

            if (json is "null" or "\"\"" || json.Length < 3)
            {
                return urls;
            }

            using JsonDocument document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return urls;
            }

            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("url", out JsonElement value))
                {
                    continue;
                }

                string? url = value.GetString();

                if (string.IsNullOrWhiteSpace(url) ||
                    !IsUsableIconUrl(url) ||
                    urls.Contains(url))
                {
                    continue;
                }

                urls.Add(url);

                if (urls.Count >= 12)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            // 页面不给脚本执行就算了，回落到首字母图标
        }

        return urls;
    }

    private static bool IsUsableIconUrl(string url) =>
        url.Length <= 2000 &&
        (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
        !url.Contains(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>在网页里按优先级收集：apple-touch-icon / favicon > og:image > 页面里的大图。</summary>
    private const string IconScript = @"(function () {
  const out = [];
  const seen = new Set();

  const add = function (u, w, h, p) {
    try {
      if (!u) { return; }
      const abs = new URL(u, document.baseURI).href;
      if (!/^https?:/i.test(abs)) { return; }
      if (/.svg(\?|#|$)/i.test(abs)) { return; }
      if (seen.has(abs)) { return; }
      seen.add(abs);
      out.push({ url: abs, w: w || 0, h: h || 0, p: p || 0 });
    } catch (e) { }
  };

  document.querySelectorAll('[rel~=icon],[rel=apple-touch-icon],[rel=apple-touch-icon-precomposed],[rel=mask-icon]')
    .forEach(function (l) { add(l.href, 0, 0, 4); });

  document.querySelectorAll(""meta[property='og:image'],meta[property='og:image:secure_url'],meta[name='og:image'],meta[name='twitter:image'],meta[name='msapplication-TileImage']"")
    .forEach(function (m) { add(m.content, 0, 0, 2); });

  document.querySelectorAll('img').forEach(function (img) {
    const w = img.naturalWidth || img.width || 0;
    const h = img.naturalHeight || img.height || 0;
    if (w >= 48 && h >= 48) { add(img.currentSrc || img.src, w, h, 1); }
  });

  const score = function (x) {
    const a = (x.w && x.h) ? Math.min(x.w, x.h) / Math.max(x.w, x.h) : 0.6;
    const area = (x.w && x.h) ? x.w * x.h : 1;
    return area * a;
  };

  out.sort(function (a, b) {
    if (a.p !== b.p) { return b.p - a.p; }
    return score(b) - score(a);
  });

  return out.slice(0, 12);
})()";

    /// <summary>从页面标题 / 文件名猜一个还算像样的软件名，用户可在确认框里改。</summary>
    private static string DeriveName(string? title, string? fileName)
    {
        string text = (title ?? string.Empty).Trim();

        foreach (string separator in new[] { " - ", " – ", " — ", " | ", "｜", "_" })
        {
            int at = text.IndexOf(separator, StringComparison.Ordinal);
            if (at > 1)
            {
                text = text[..at];
                break;
            }
        }

        text = text.Trim();

        foreach (string suffix in new[] { "官方下载", "下载中心", "官方版", "下载", "官网" })
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal) && text.Length > suffix.Length)
            {
                text = text[..^suffix.Length].Trim();
            }
        }

        if (text.Length == 0)
        {
            string file = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);

            foreach (string token in new[] { "Setup", "Installer", "Install", "_x64", "x64" })
            {
                file = file.Replace(token, " ", StringComparison.OrdinalIgnoreCase);
            }

            text = Regex.Replace(file, "\\s+", " ").Trim(' ', '-', '_');
        }

        if (text.Length == 0)
        {
            text = "新软件";
        }

        return text.Length > 60 ? text[..60].Trim() : text;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "关闭",
            XamlRoot = RootGrid.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    private void Runtime_Click(object sender, RoutedEventArgs e) =>
        AppDownloads.OpenUrl("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
}
