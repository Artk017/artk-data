using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace NovaServer;

public sealed partial class MainWindow : Window
{
    private readonly ServerHost _host;
    private readonly ObservableCollection<LogRow> _logRows = new();

    private ServerClient? _client;
    private TrayIcon? _tray;
    private DispatcherTimer? _statsTimer;
    private bool _busy;
    private bool _online;
    private bool _startedHere;
    private bool _exitRequested;
    private bool _hiddenToTray;
    private bool _trayHintShown;
    private bool _loadingCloseAction;

    /// <summary>当前主窗口：重复启动时第二个进程要把它叫到前台。</summary>
    public static MainWindow? Active { get; private set; }

    public MainWindow()
    {
        InitializeComponent();

        Active = this;
        Title = "Artk Server";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        VersionBadgeText.Text = "v" + AppVersion();

        Toast.Attach(ToastHost);

        ServerLog.EntryAdded += OnLogEntry;
        foreach (LogEntry entry in ServerLog.Snapshot())
        {
            _logRows.Add(MakeRow(entry));
        }

        LogList.ItemsSource = _logRows;
        UpdateLogEmptyText();

        Nav.SelectedItem = HomeItem;
        ShowPage("home");

        _tray = new TrayIcon("Artk Server · 清单服务", OnTrayOpen, OnTrayExit);
        AppWindow.Closing += OnAppWindowClosing;
        AppWindow.Changed += OnAppWindowChanged;

        _host = ServerHost.Load();
        UiPreferences.Load(_host.HomeDirectory);

        _loadingCloseAction = true;
        CloseActionCombo.SelectedIndex =
            UiPreferences.CloseBehavior == CloseAction.Exit ? 1 : 0;
        _loadingCloseAction = false;

        ApplyWindowIcon();
        ApplyBackdrop();
        ResizeAndCenter(1020, 940);

        RootGrid.Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private static string AppVersion()
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(info.ProductVersion))
            {
                return info.ProductVersion.Split('+')[0];
            }

            if (!string.IsNullOrWhiteSpace(info.FileVersion))
            {
                return info.FileVersion;
            }
        }
        catch
        {
            // 读不到就用程序集版本
        }

        Version? version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null ? "—" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnLoaded;
        await StartAsync();
    }

    /// <summary>
    /// 端口上已经有服务（比如计划任务里的 --headless）就直接连它；
    /// 没有就在本进程里把服务跑起来，界面照旧通过本机接口读写。
    /// </summary>
    private async Task StartAsync()
    {
        SetState(false, "正在启动…");
        ServerLog.Info("正在检查本机是否有正在运行的清单服务…");
        _client = new ServerClient(_host.Port, _host.Config.ApiKey);

        try
        {
            await _client.GetSettingsAsync();
            await LoadAsync();
            ServerLog.Success($"已连接正在运行的清单服务：http://localhost:{_host.Port}");
            return;
        }
        catch (ServerUnauthorizedException)
        {
            ShowUnavailable("端口上已经有服务在跑，但 API Key 和 server.json 里的不一致");
            return;
        }
        catch (ServerUnreachableException)
        {
            // 没服务，继续往下自己起
            ServerLog.Info("本机没有运行中的服务，正在启动内置清单服务…");
        }
        catch (Exception ex)
        {
            ShowUnavailable(ex.Message);
            return;
        }

        try
        {
            SubtitleText.Text = "正在准备数据…";
            ServerLog.Info("正在准备数据目录…");
            await _host.PrepareAsync(_host.Config.RefreshDataOnStart);
            _host.BuildApp();
            await _host.StartAsync();
            _host.Publisher.Start();
            _startedHere = true;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ShowUnavailable("服务启动失败：" + ex.Message);
        }
    }

    private async Task LoadAsync()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            ServerSettings settings = await _client.GetSettingsAsync();
            Render(settings);
            SetState(true, "● 运行中");
            EnsureStatsTimer();
        }
        catch (Exception ex)
        {
            SetState(false, "● 未连接");
            ServerLog.Error("读取服务器设置失败：" + ex.Message);
            Toast.Error("读取设置失败", ex.Message);
        }
    }

    private void Render(ServerSettings s)
    {
        RenderStats(s);

        // ── 服务设置 ──
        PortBox.Value = s.Server.Port;
        ApiKeyBox.Password = string.Empty;
        ApiKeyBox.PlaceholderText = $"已配置 {s.Server.ApiKeyMasked}";
        ApiKeyHint.Text = string.IsNullOrEmpty(s.Server.ApiKeyMasked)
            ? "当前：未配置（写接口不校验）"
            : $"当前：{s.Server.ApiKeyMasked}";
        RequireReadToggle.IsOn = s.Server.RequireKeyForRead;
        RefreshDataToggle.IsOn = s.Server.RefreshDataOnStart;
        DataBaseUrlBox.Text = s.Server.DataBaseUrl;

        string readHint = s.Server.RequireKeyForRead ? "读接口需要密钥" : "读接口不校验密钥";
        string refreshHint = s.Server.RefreshDataOnStart ? " · 启动会强制重新下载" : string.Empty;
        ServerStatusText.Text = $"数据文件 {s.Server.ResolvedDataFile} · {readHint}{refreshHint}";

        // ── 自动发布 ──
        AutoPublishToggle.IsOn = s.Publish.AutoPublish;
        RepoBox.Text = string.IsNullOrEmpty(s.Publish.Repo) ? string.Empty : s.Publish.Repo;
        RepoBox.PlaceholderText = $"留空用 {s.Publish.Repo}";
        TagBox.Text = string.IsNullOrEmpty(s.Publish.Tag) ? string.Empty : s.Publish.Tag;
        TagBox.PlaceholderText = $"留空用 {s.Publish.Tag}";
        TokenBox.Password = string.Empty;
        TokenBox.PlaceholderText = s.Publish.HasToken
            ? $"已配置 {s.Publish.TokenMasked}"
            : "还没有 token";
        TokenHint.Text = s.Publish.HasToken ? $"当前：{s.Publish.TokenMasked}" : "当前：未配置";
        RegFilesToggle.IsOn = s.Publish.RegFiles;
        PublishStatusText.Text = BuildPublishStatus(s.Publish);

        // ── 数据文件 ──
        DataDirText.Text = $"目录：{s.Server.DataDir}";
        ConfigPathText.Text = $"配置：{s.ConfigPath}";

        FilesList.ItemsSource = s.Files;
        FilesList.Visibility = s.Files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilesEmptyText.Visibility = s.Files.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>只刷新主页顶部那几张统计卡片，不碰设置 / 发布页里正在编辑的输入框。</summary>
    private void RenderStats(ServerSettings s)
    {
        SubtitleText.Text = $"端口 {s.Server.Port} · {s.Catalog.Count} 款软件 · 清单 v{s.Catalog.Version}";

        StatCount.Text = $"{s.Catalog.Count} 款";
        StatCountNote.Text = $"清单 v{s.Catalog.Version}";

        StatUpdated.Text = When(s.Catalog.UpdatedAt);
        StatUpdatedNote.Text = s.Catalog.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss");

        StatPublish.Text = s.Publish.AutoPublish ? (s.Publish.HasToken ? "已开启" : "缺 token") : "已关闭";
        StatPublishNote.Text = $"{s.Publish.Repo} @ {s.Publish.Tag}";

        StatLast.Text = s.Publish.LastPublishedAt is { } published ? When(published) : "还没发过";
        StatLastNote.Text = string.IsNullOrWhiteSpace(s.Publish.LastUploaded) ? "—" : s.Publish.LastUploaded;
    }

    /// <summary>
    /// 主页统计卡片每 5 秒自动刷一次：日志是实时追加的，卡片不跟着刷的话，
    /// 自动发布刚发完这里还会写着「还没发过」，看着像没发出去。
    /// </summary>
    private void EnsureStatsTimer()
    {
        if (_statsTimer is not null)
        {
            return;
        }

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statsTimer.Tick += async (_, _) => await RefreshStatsAsync();
        _statsTimer.Start();
    }

    private async Task RefreshStatsAsync()
    {
        if (_client is null || _busy)
        {
            return;
        }

        try
        {
            ServerSettings settings = await _client.GetSettingsAsync();
            RenderStats(settings);
        }
        catch
        {
            // 刷新失败就保持上一次的样子，不弹提示
        }
    }

    private static string BuildPublishStatus(PublishSection p)
    {
        var bits = new List<string>
        {
            p.Enabled
                ? "自动发布运行中"
                : p.AutoPublish ? "自动发布已开，但没有可用 token" : "自动发布已关闭",
            $"目标 {p.Repo} @ {p.Tag}",
            p.LastPublishedAt is { } published
                ? $"上次 {When(published)}{(string.IsNullOrWhiteSpace(p.LastUploaded) ? string.Empty : "：" + p.LastUploaded)}"
                : "还没有发布过",
        };

        if (!string.IsNullOrWhiteSpace(p.LastError))
        {
            bits.Add("上次失败：" + p.LastError);
        }

        return string.Join(" · ", bits);
    }

    private static string When(DateTimeOffset value)
    {
        TimeSpan diff = DateTimeOffset.Now - value;

        if (diff < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }

        if (diff < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)diff.TotalMinutes)} 分钟前";
        }

        if (diff < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)diff.TotalHours)} 小时前";
        }

        return value.ToString("yyyy-MM-dd HH:mm");
    }

    private void SetState(bool running, string text)
    {
        _online = running;

        StateText.Text = text;
        StateText.Foreground = new SolidColorBrush(running
            ? Windows.UI.Color.FromArgb(255, 0x0F, 0x9D, 0x58)
            : Windows.UI.Color.FromArgb(255, 0xA0, 0xA0, 0xA0));

        SaveServerButton.IsEnabled = running && !_busy;
        SavePublishButton.IsEnabled = running && !_busy;
        PublishNowButton.IsEnabled = running && !_busy;
    }

    private void ShowUnavailable(string message)
    {
        SetState(false, "● 未连接");
        SubtitleText.Text = "服务未运行";
        ServerLog.Error(message);
        Toast.Error("服务未运行", message, 10);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        bool enabled = !busy && _online;
        SaveServerButton.IsEnabled = enabled;
        SavePublishButton.IsEnabled = enabled;
        PublishNowButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = !busy;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async void SaveServer_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null)
        {
            return;
        }

        double portValue = PortBox.Value;
        var patch = new Dictionary<string, object?>
        {
            ["port"] = double.IsNaN(portValue) ? 0 : (int)Math.Round(portValue),
            ["requireKeyForRead"] = RequireReadToggle.IsOn,
            ["refreshDataOnStart"] = RefreshDataToggle.IsOn,
            ["dataBaseUrl"] = DataBaseUrlBox.Text.Trim(),
        };

        string key = ApiKeyBox.Password.Trim();
        if (key.Length > 0)
        {
            patch["apiKey"] = key;
        }

        await SaveAsync(patch, "服务设置已保存");
    }

    private async void SavePublish_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null)
        {
            return;
        }

        var patch = new Dictionary<string, object?>
        {
            ["autoPublish"] = AutoPublishToggle.IsOn,
            ["publishRepo"] = RepoBox.Text.Trim(),
            ["publishTag"] = TagBox.Text.Trim(),
            ["publishRegFiles"] = RegFilesToggle.IsOn,
        };

        string token = TokenBox.Password.Trim();
        if (token.Length > 0)
        {
            patch["publishToken"] = token;
        }

        await SaveAsync(patch, "发布设置已保存");
    }

    private async Task SaveAsync(Dictionary<string, object?> patch, string title)
    {
        if (_client is null || _busy)
        {
            return;
        }

        SetBusy(true);

        try
        {
            SaveResult result = await _client.SaveAsync(patch);

            if (!string.IsNullOrEmpty(result.ApiKey))
            {
                // 服务端改了密钥，后续请求跟着用新的，别把自己挡在 401 外面
                _client.Key = result.ApiKey;
            }

            var parts = new List<string>();
            if (result.Applied.Count > 0)
            {
                parts.Add("已更新：" + string.Join("、", result.Applied));
            }

            if (result.RestartRequired.Count > 0)
            {
                parts.Add("重启服务后生效：" + string.Join("、", result.RestartRequired));
            }

            string detail = parts.Count == 0 ? "没有改动" : string.Join("；", parts);
            ServerLog.Success($"{title}：{detail}");
            Toast.Success(title, detail);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ServerLog.Error($"{title}失败：{ex.Message}");
            Toast.Error("保存失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void PublishNow_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || _busy)
        {
            return;
        }

        SetBusy(true);

        try
        {
            string message = await _client.PublishNowAsync();
            ServerLog.Success("手动发布完成：" + message);
            Toast.Success("发布完成", message, 6);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ServerLog.Error("手动发布失败：" + ex.Message);
            Toast.Error("发布失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ── 左侧导航 ────────────────────────────────────────────────────────

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            ShowPage(tag);
        }
    }

    private void ShowPage(string tag)
    {
        HomePage.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PublishPage.Visibility = tag == "publish" ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = tag == "files" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 运行日志 ────────────────────────────────────────────────────────

    private void OnLogEntry(LogEntry entry)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _logRows.Add(MakeRow(entry));

            while (_logRows.Count > 400)
            {
                _logRows.RemoveAt(0);
            }

            UpdateLogEmptyText();
            LogList.ScrollIntoView(_logRows[^1]);
        });
    }

    private void UpdateLogEmptyText()
    {
        bool empty = _logRows.Count == 0;
        LogList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        LogEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private static LogRow MakeRow(LogEntry entry) => new()
    {
        TimeText = entry.Time.ToString("HH:mm:ss"),
        LevelText = entry.Level switch
        {
            LogLevel.Success => "成功",
            LogLevel.Warning => "提示",
            LogLevel.Error => "错误",
            _ => "信息",
        },
        Message = entry.Message,
        LevelBrush = new SolidColorBrush(entry.Level switch
        {
            LogLevel.Success => Windows.UI.Color.FromArgb(255, 0x0F, 0x9D, 0x58),
            LogLevel.Warning => Windows.UI.Color.FromArgb(255, 0xB4, 0x53, 0x09),
            LogLevel.Error => Windows.UI.Color.FromArgb(255, 0xDC, 0x26, 0x26),
            _ => Windows.UI.Color.FromArgb(255, 0x8A, 0x8A, 0x8A),
        }),
    };

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logRows.Clear();
        UpdateLogEmptyText();
    }

    // ── 后台最小化 / 托盘 ────────────────────────────────────────────────

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
        {
            return;
        }

        args.Cancel = true;

        if (UiPreferences.CloseBehavior == CloseAction.Exit)
        {
            // 设置里选了「退出」：停服务、关进程；异步排一下，避免在 Closing 里重入 Close
            DispatcherQueue.TryEnqueue(ExitApp);
            return;
        }

        // 默认：藏到托盘，服务继续跑
        HideToTray();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange || _exitRequested || _hiddenToTray)
        {
            return;
        }

        if (sender.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized)
        {
            if (UiPreferences.CloseBehavior == CloseAction.Exit)
            {
                // 选了退出时，最小化就正常待在任务栏
                return;
            }

            DispatcherQueue.TryEnqueue(HideToTray);
        }
    }

    private void CloseAction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingCloseAction)
        {
            return;
        }

        UiPreferences.CloseBehavior =
            CloseActionCombo.SelectedIndex == 1 ? CloseAction.Exit : CloseAction.MinimizeToTray;
        UiPreferences.Save();

        ServerLog.Info(UiPreferences.CloseBehavior == CloseAction.Exit
            ? "关闭行为已保存：退出 Artk Server"
            : "关闭行为已保存：最小化至托盘");
    }

    // ── 卸载 ────────────────────────────────────────────────────────────

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "卸载 Artk Server",
            Content = "会先停止后台服务，删除计划任务、防火墙规则、server.json、ui.json 和 data 目录，"
                      + "再卸载程序本体。清单数据一并删除，无法恢复。",
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        UninstallButton.IsEnabled = false;

        if (!ServerUninstaller.Launch(_host.HomeDirectory))
        {
            UninstallButton.IsEnabled = true;
            Toast.Error("卸载未开始", "管理员授权被取消了");
            return;
        }

        ServerLog.Info("正在退出并清理安装…");
        _exitRequested = true;
        _tray?.Hide();
        Close();
    }

    private void HideToTray()
    {
        if (_exitRequested || _hiddenToTray)
        {
            return;
        }

        _hiddenToTray = true;
        _tray?.Show();
        AppWindow.Hide();

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.ShowBalloon("Artk Server 仍在后台运行",
                "双击托盘图标可以重新打开界面，右键菜单可以退出。");
        }

        ServerLog.Info("窗口已隐藏到托盘，清单服务继续在后台运行");
    }

    private void OnTrayOpen()
    {
        BringToFront();
    }

    /// <summary>把窗口从托盘 / 最小化恢复并拉到前面。</summary>
    internal void BringToFront()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _tray?.Hide();
            _hiddenToTray = false;
            AppWindow.Show();

            if (AppWindow.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            Activate();
            ServerLog.Info("已把窗口重新拉到前台");
        });
    }

    private void OnTrayExit()
    {
        DispatcherQueue.TryEnqueue(ExitApp);
    }

    private void ExitApp()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        _tray?.Hide();
        Close();
    }

    /// <summary>主页日志列表的一行。</summary>
    private sealed class LogRow
    {
        public string TimeText { get; init; } = string.Empty;

        public string LevelText { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public Brush LevelBrush { get; init; } = new SolidColorBrush(Colors.Gray);
    }

    /// <summary>任务栏 / Alt+Tab / 标题栏用的应用图标。</summary>
    private void ApplyWindowIcon()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

        if (System.IO.File.Exists(path))
        {
            AppWindow.SetIcon(path);
        }
    }

    private void ApplyBackdrop()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        }
        else
        {
            SolidBackground.Visibility = Visibility.Visible;
        }
    }

    private void ResizeAndCenter(int width, int height)
    {
        // AppWindow 用的是物理像素，高分屏（125% / 150% / 250%）要乘上缩放，否则窗口会窄得放不下内容
        IntPtr hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        double scale = GetDpiForWindow(hwnd) / 96d;
        if (scale <= 0)
        {
            scale = 1;
        }

        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 work = area.WorkArea;

        int pixelWidth = Math.Min((int)(width * scale), work.Width - 40);
        int pixelHeight = Math.Min((int)(height * scale), work.Height - 40);

        AppWindow.Resize(new SizeInt32(pixelWidth, pixelHeight));
        AppWindow.Move(new PointInt32(
            work.X + ((work.Width - pixelWidth) / 2),
            work.Y + ((work.Height - pixelHeight) / 2)));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        ServerLog.EntryAdded -= OnLogEntry;
        _statsTimer?.Stop();
        _statsTimer = null;

        try
        {
            // 只停自己启动的服务；连的是外部服务就让它继续跑
            if (_startedHere)
            {
                ServerLog.Info("正在停止内置清单服务…");
                await _host.StopAsync();
            }
        }
        finally
        {
            Active = null;
            _tray?.Dispose();
            _tray = null;
            Application.Current.Exit();
        }
    }
}
