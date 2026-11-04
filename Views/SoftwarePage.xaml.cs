using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;
using WinUIDemo.Helpers;
using WinUIDemo.Services;

namespace WinUIDemo.Views;

public sealed partial class SoftwarePage : Page
{
    private readonly List<SoftwareItem> _all = new();

    // 「添加软件」窗口同一时间只开一个，避免叠出好几个浏览器窗口
    private AddSoftwareWindow? _addWindow;

    // 切回本页时不必每次都重新拉清单（会发 HTTP、重建卡片、重扫注册表）。短时间内复用上次结果。
    private static readonly TimeSpan ReloadMinInterval = TimeSpan.FromSeconds(30);
    private DateTime _lastLoadUtc = DateTime.MinValue;
    private int _seenRevision;

    public ObservableCollection<SoftwareItem> Items { get; } = new();

    public SoftwarePage()
    {
        InitializeComponent();

        // 清单只从服务器取：先清空，加载完再铺（拿不到就保持空）
        ApplyFilter();

        _ = LoadCatalogAsync();
    }

    /// <summary>从服务器重新加载清单，然后重建卡片；拿不到就清空、不显示。</summary>
    private async Task LoadCatalogAsync()
    {
        _lastLoadUtc = DateTime.UtcNow;
        _seenRevision = CatalogServer.Revision;

        await AppCatalog.ReloadAsync();

        RebuildItems();

        int count = _all.Count;

        SourceText.Text = count > 0
            ? $"服务器清单 · {count} 款"
            : AppCatalog.Source;

        EmptyState.Visibility = count > 0 ? Visibility.Collapsed : Visibility.Visible;

        if (count == 0)
        {
            EmptyHint.Text = $"{AppCatalog.Source}。检查网络或服务器状态后点「重试」。";
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        SourceText.Text = "清单加载中…";
        EmptyState.Visibility = Visibility.Collapsed;
        _ = LoadCatalogAsync();
    }

    /// <summary>清单变了以后重建卡片；正在下载的不打断，只刷新链接状态。</summary>
    private void RebuildItems()
    {
        if (_all.Any(i => i.IsDownloading))
        {
            foreach (SoftwareItem item in _all)
            {
                item.RefreshLink();
            }

            return;
        }

        _all.Clear();

        foreach (AppEntry entry in AppCatalog.All)
        {
            _all.Add(new SoftwareItem(entry));
        }

        ApplyFilter();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 旧版直链工具抓回来的地址还在本地文件里，回来时重新读一遍
        AppLinks.Reload();

        foreach (SoftwareItem item in _all)
        {
            item.RefreshLink();
        }

        // 清单可能在别处（服务器网页 / 另一台电脑）改过，回来时同步一次；
        // 服务器地址刚改过（Revision 变化）也要立即重连；否则短时间反复切页就别重复拉。
        if (CatalogServer.Revision != _seenRevision || DateTime.UtcNow - _lastLoadUtc >= ReloadMinInterval)
        {
            _ = LoadCatalogAsync();
        }
    }

    private void ApplyFilter()
    {
        Items.Clear();

        foreach (SoftwareItem item in _all)
        {
            Items.Add(item);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_addWindow is not null)
        {
            _addWindow.Activate();
            return;
        }

        var window = new AddSoftwareWindow();
        _addWindow = window;

        window.Closed += async (_, _) =>
        {
            _addWindow = null;

            if (window.AddedAny)
            {
                await LoadCatalogAsync();
            }
        };

        window.Activate();
    }

    /// <summary>右键卡片：编辑 / 删除。</summary>
    private void Card_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SoftwareItem item } target)
        {
            return;
        }

        ShowCardMenu(target, item, e.GetPosition(target));
    }

    private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            UiMotion.ScaleTo(element, 1.01);
        }
    }

    private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            UiMotion.ScaleTo(element, 1.0);
        }
    }

    private void Card_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            UiMotion.ScaleTo(element, 0.99, UiMotion.PressDuration);
        }
    }

    private void Card_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            UiMotion.ScaleTo(element, 1.01, UiMotion.PressDuration);
        }
    }

    /// <summary>卡片右上角的「⋯」按钮。</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SoftwareItem item } button)
        {
            return;
        }

        ShowCardMenu(button, item, new Windows.Foundation.Point(-150, 32));
    }

    private void ShowCardMenu(FrameworkElement anchor, SoftwareItem item, Windows.Foundation.Point position)
    {
        var edit = new MenuFlyoutItem { Text = "编辑这款软件…", Icon = new FontIcon { Glyph = "\uE70F" } };
        edit.Click += async (_, _) => await EditItemAsync(item);

        var remove = new MenuFlyoutItem { Text = "删除", Icon = new FontIcon { Glyph = "\uE74D" } };
        remove.Click += async (_, _) => await DeleteItemAsync(item);

        var flyout = new MenuFlyout();
        flyout.Items.Add(edit);
        flyout.Items.Add(remove);
        flyout.ShowAt(anchor, new FlyoutShowOptions { Position = position });
    }

    private async Task EditItemAsync(SoftwareItem item)
    {
        AppEntry? edited = await AppEditorDialog.ShowAsync(XamlRoot, item.Entry);

        if (edited is null)
        {
            return;
        }

        await SaveEntryAsync(edited);
    }

    private async Task DeleteItemAsync(SoftwareItem item)
    {
        var confirm = new ContentDialog
        {
            Title = "删除「" + item.Name + "」？",
            Content = "会从服务器清单里删掉，其它电脑同步后也看不到了。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(item.Entry.Id))
            {
                await ShowInfoAsync("删不掉", "这条在服务器上没有对应记录，无法删除。");
                return;
            }

            await CatalogServer.DeleteAsync(item.Entry.Id);
            await LoadCatalogAsync();
        }
        catch (Exception ex)
        {
            await ShowInfoAsync("删除失败", ex.Message);
        }
    }

    /// <summary>保存改动：一律写服务器。</summary>
    private async Task SaveEntryAsync(AppEntry entry)
    {
        try
        {
            await CatalogServer.SaveAsync(AppCatalog.ToDto(entry));
            await LoadCatalogAsync();
        }
        catch (Exception ex)
        {
            await ShowInfoAsync("保存失败", ex.Message);
        }
    }

    /// <summary>提示一律走右上角 Toast，不再弹「知道了」对话框。</summary>
    private Task ShowInfoAsync(string title, string message)
    {
        Toast.Error(title, message, 10);
        return Task.CompletedTask;
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SoftwareItem item })
        {
            return;
        }

        if (item.IsDownloading)
        {
            item.Cancel();
            return;
        }

        if (item.IsBusyInstalling)
        {
            return;
        }

        // 已经有安装包
        if (item.DownloadedPath is { Length: > 0 } path && File.Exists(path))
        {
            if (item.IsInstalled)
            {
                // 装过了，只是安装包没删干净：点一下清理
                await item.DeleteLeftoverInstallerAsync();
                return;
            }

            // 有安装包还没装：直接尝试静默安装（失败会转图形安装）
            await item.InstallDownloadedAsync();
            return;
        }

        if (!item.HasDirectLink)
        {
            bool opened = AppDownloads.OpenUrl(item.HomeUrl);
            item.SetStatus(opened
                ? "已打开官网下载页 · 想以后一键下载，可用「添加软件」在网页里点下载抓一次直链"
                : "打不开浏览器，请手动访问官网");
            return;
        }

        // 下载完成后自动接着装，做到「下载即装」
        await item.DownloadAsync();

        if (item.DownloadedPath is { Length: > 0 } && !item.IsDownloading)
        {
            await item.InstallDownloadedAsync();
        }
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
        {
            AppDownloads.OpenUrl(url);
        }
    }

    /// <summary>下拉里的「非静默安装」：有安装包就直接打开，没有就先下载再打开安装界面。</summary>
    private async void ManualInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        // 菜单项不在可视树里，Tag 的 x:Bind 未必拿得到，退回到 DataContext
        SoftwareItem? item = element.Tag as SoftwareItem ?? element.DataContext as SoftwareItem;

        if (item is null)
        {
            Toast.Error("找不到对应的软件", "列表项已经刷新，请重新点一次下拉菜单。");
            return;
        }

        if (item.IsDownloading || item.IsBusyInstalling)
        {
            return;
        }

        if (item.DownloadedPath is { Length: > 0 } path && File.Exists(path))
        {
            await item.InstallInteractivelyAsync();
            return;
        }

        if (!item.HasDirectLink)
        {
            bool opened = AppDownloads.OpenUrl(item.HomeUrl);
            item.SetStatus(opened
                ? "已打开官网下载页 · 想以后一键下载，可用「添加软件」在网页里点下载抓一次直链"
                : "打不开浏览器，请手动访问官网");
            return;
        }

        await item.DownloadAsync();

        if (item.DownloadedPath is { Length: > 0 } && !item.IsDownloading)
        {
            await item.InstallInteractivelyAsync();
        }
    }

    private void ItemFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SoftwareItem item })
        {
            AppDownloads.ShowInFolder(item.DownloadedPath);
        }
    }
}

public sealed class SoftwareItem : INotifyPropertyChanged
{
    private readonly AppEntry _entry;

    private CancellationTokenSource? _cts;
    private string? _downloadedPath;
    private string _statusText;
    private string _primaryText;
    private double _progress;
    private double _progressOpacity;
    private double _speed;
    private long _lastTick;
    private long _lastBytes;
    private Visibility _folderVisibility = Visibility.Collapsed;

    /// <summary>不含体积 / 已安装后缀的基础状态文案，拼装时统一加后缀。</summary>
    private string _baseStatus = string.Empty;

    /// <summary>自动探测到的安装包体积文案（如「约 270 MB」）；探测不到则不显示。</summary>
    private string? _sizeText;

    private bool _installed;
    private bool _sizeProbed;
    private bool _installedProbed;
    private bool _busyInstalling;

    /// <summary>真正要下的地址：固定直链，或旧版抓取工具留下的地址，或点下载时现场解析。</summary>
    private string? _url;

    /// <summary>下载时带的 Referer（有的 CDN 没有它会 403）。</summary>
    private string? _referer;

    private bool _hasDirect;

    private ImageBrush? _iconBrush;
    private Visibility _iconVisibility = Visibility.Collapsed;
    private Visibility _initialVisibility = Visibility.Visible;
    private Visibility _tileVisibility = Visibility.Visible;

    public SoftwareItem(AppEntry entry)
    {
        _entry = entry;
        TileBrush = CreateTileBrush(entry.Color);
        _statusText = string.Empty;
        _primaryText = "下载";
        RefreshLink();
        RestoreDownloadedState();
        _ = ProbeInstalledAsync();

        if (entry.IconUrl is { Length: > 0 })
        {
            _ = LoadIconAsync();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name => _entry.Name;

    /// <summary>原始清单条目，编辑 / 删除时要用到 Id。</summary>
    public AppEntry Entry => _entry;

    /// <summary>持久化下载记录用的稳定键：优先 Id，没有则用名字。</summary>
    public string StateKey => !string.IsNullOrWhiteSpace(_entry.Id) ? _entry.Id! : _entry.Name;

    public bool IsBusyInstalling => _busyInstalling;

    public bool IsInstalled => _installed;

    public string Initial => _entry.Initial;

    /// <summary>网页图标的圆角画笔；没抓到 / 下载失败时为空，卡片继续显示首字母。</summary>
    public ImageBrush? IconBrush
    {
        get => _iconBrush;
        private set
        {
            _iconBrush = value;
            OnPropertyChanged(nameof(IconBrush));
        }
    }

    public Visibility IconVisibility
    {
        get => _iconVisibility;
        private set
        {
            _iconVisibility = value;
            OnPropertyChanged(nameof(IconVisibility));
        }
    }

    public Visibility InitialVisibility
    {
        get => _initialVisibility;
        private set
        {
            _initialVisibility = value;
            OnPropertyChanged(nameof(InitialVisibility));
        }
    }

    /// <summary>固定底色的底板：用图片图标时隐藏，让图标背景透出卡片底色。</summary>
    public Visibility TileVisibility
    {
        get => _tileVisibility;
        private set
        {
            _tileVisibility = value;
            OnPropertyChanged(nameof(TileVisibility));
        }
    }

    public string HomeUrl => _entry.HomeUrl;

    public bool HasDirectLink => _hasDirect;

    public string? DownloadedPath => _downloadedPath;

    public bool IsDownloading => _cts is not null;

    public Brush TileBrush { get; }

    /// <summary>能直接下载时才显示「官网」按钮；不能的话主按钮本身就是「前往官网」。</summary>
    public Visibility HomeVisibility => _hasDirect ? Visibility.Visible : Visibility.Collapsed;

    private async Task LoadIconAsync()
    {
        BitmapImage? bitmap = await AppIconLoader.GetAsync(_entry.IconUrl);

        if (bitmap is null)
        {
            return;
        }

        IconBrush = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
        IconVisibility = Visibility.Visible;
        InitialVisibility = Visibility.Collapsed;
        TileVisibility = Visibility.Collapsed;
    }

    /// <summary>重新读一次直链来源（固定直链 / 抓回来的链接 / 自动解析）。</summary>
    public void RefreshLink()
    {
        bool busy = IsDownloading || _downloadedPath is not null;

        if (_entry.DirectUrl is { Length: > 0 } fixedUrl)
        {
            _url = fixedUrl;
            _referer = null;
            _hasDirect = true;
            _baseStatus = "官方直链";
        }
        else if (AppLinks.TryGet(_entry.Name, out StoredLink stored) && stored.Url.Length > 0)
        {
            _url = stored.Url;
            _referer = stored.Referer;
            _hasDirect = true;
            _baseStatus = $"已抓取直链 · {stored.CapturedAt}";
        }
        else if (_entry.Resolve != ResolveKind.None)
        {
            _url = null;
            _referer = null;
            _hasDirect = true;
            _baseStatus = "官方直链 · 点下载时自动获取最新版本";
        }
        else
        {
            _url = null;
            _referer = null;
            _hasDirect = false;
            _baseStatus = "官网下载页 · 点「前往官网」下载";
        }

        OnPropertyChanged(nameof(HasDirectLink));
        OnPropertyChanged(nameof(HomeVisibility));

        // 正在下载 / 已经下完的卡片别把当前进度冲掉
        if (!busy)
        {
            StatusText = ComposeStatus();
        }

        if (!busy && FolderVisibility != Visibility.Visible)
        {
            PrimaryText = _installed ? "重新下载" : (_hasDirect ? "下载" : "前往官网");
        }

        // 有确切地址就顺手探一下真实大小（每个实例只探一次，探不到就不显示）
        if (_hasDirect && _url is { Length: > 0 } sizeUrl)
        {
            _ = ProbeSizeAsync(sizeUrl, _referer);
        }
    }

    /// <summary>基础状态 + 体积 + 已安装，统一拼出来。</summary>
    private string ComposeStatus()
    {
        string text = _baseStatus;

        if (_sizeText is { Length: > 0 })
        {
            text = text.Length > 0 ? $"{text} · {_sizeText}" : _sizeText;
        }

        if (_installed)
        {
            text = text.Length > 0 ? $"{text} · 已安装" : "已安装";
        }

        return text;
    }

    /// <summary>后台探测安装包真实体积；探测不到就不显示（不再用手写的大小）。</summary>
    private async Task ProbeSizeAsync(string url, string? referer)
    {
        if (_sizeProbed)
        {
            return;
        }

        _sizeProbed = true;

        long? bytes;
        try
        {
            bytes = await AppDownloads.TryGetRemoteFileSizeAsync(url, referer);
        }
        catch
        {
            bytes = null;
        }

        if (bytes is long value && value > 0)
        {
            _sizeText = $"约 {value / 1024d / 1024d:0.#} MB";

            if (!IsDownloading && _downloadedPath is null)
            {
                StatusText = ComposeStatus();
            }
        }
    }

    /// <summary>后台扫卸载注册表，判断这个软件是不是已经装过了。</summary>
    private async Task ProbeInstalledAsync()
    {
        if (_installedProbed)
        {
            return;
        }

        _installedProbed = true;

        bool installed;
        try
        {
            installed = await Task.Run(() => AppInstall.IsInstalled(_entry.Name));
        }
        catch
        {
            installed = false;
        }

        if (installed)
        {
            _installed = true;

            if (!IsDownloading && _downloadedPath is null && !_busyInstalling)
            {
                StatusText = ComposeStatus();

                if (FolderVisibility != Visibility.Visible)
                {
                    PrimaryText = "重新下载";
                }
            }
        }
    }

    /// <summary>启动时检查上次下载的安装包是否还在，在就恢复「已下载」状态，不用重下。</summary>
    private void RestoreDownloadedState()
    {
        if (_downloadedPath is not null)
        {
            return;
        }

        string? saved = DownloadRegistry.Get(StateKey);
        if (saved is null)
        {
            return;
        }

        _downloadedPath = saved;
        FolderVisibility = Visibility.Visible;
        PrimaryText = "运行安装包";
        ProgressOpacity = 0;
        StatusText = $"已下载 {Path.GetFileName(saved)} · 点右边运行安装包";
        Toast.Success("下载完成", Path.GetFileName(saved));
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string PrimaryText
    {
        get => _primaryText;
        private set => Set(ref _primaryText, value);
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    /// <summary>进度条一直占着位置，只改透明度，避免下载开始/结束时卡片高度跳动。</summary>
    public double ProgressOpacity
    {
        get => _progressOpacity;
        private set => Set(ref _progressOpacity, value);
    }

    public Visibility FolderVisibility
    {
        get => _folderVisibility;
        private set => Set(ref _folderVisibility, value);
    }

    public void SetStatus(string text) => StatusText = text;

    public void Cancel() => _cts?.Cancel();

    public async Task DownloadAsync()
    {
        if (_cts is not null)
        {
            return;
        }

        if (_url is null && _entry.Resolve == ResolveKind.None)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        DownloadTracker.Register(_cts);
        CancellationToken token = _cts.Token;

        OnPropertyChanged(nameof(IsDownloading));
        PrimaryText = "取消";
        ProgressOpacity = 1;
        Progress = 0;
        _speed = 0;
        _lastTick = 0;
        _lastBytes = 0;
        StatusText = _url is null ? "正在获取官方直链…" : "正在连接…";

        var progress = new Progress<(long Received, long? Total)>(report =>
        {
            double mb = report.Received / 1024d / 1024d;
            long now = Environment.TickCount64;

            if (_lastTick != 0 && now > _lastTick)
            {
                double instant = (report.Received - _lastBytes) / 1024d / 1024d / ((now - _lastTick) / 1000d);
                _speed = _speed <= 0 ? instant : (_speed * 0.6) + (instant * 0.4);
            }

            _lastTick = now;
            _lastBytes = report.Received;

            if (report.Total is long total && total > 0)
            {
                Progress = Math.Clamp(report.Received * 100d / total, 0, 100);
                StatusText = $"下载中 {mb:0.0} / {total / 1024d / 1024d:0.0} MB · {_speed:0.0} MB/s";
            }
            else
            {
                StatusText = $"下载中 {mb:0.0} MB · {_speed:0.0} MB/s";
            }
        });

        try
        {
            if (_url is null)
            {
                (string? resolved, string? referer) = await AppLinkResolver.ResolveAsync(_entry, token);

                if (resolved is null)
                {
                    ProgressOpacity = 0;
                    StatusText = "没取到直链（官网可能改版了），已打开官网下载页";
                    AppDownloads.OpenUrl(_entry.HomeUrl);
                    Toast.Info("没取到直链", _entry.Name + " · 已打开官网下载页");
                    return;
                }

                _url = resolved;
                _referer ??= referer;
                StatusText = "正在连接…";
            }

            string path = await AppDownloads.DownloadAsync(_url, _referer, null, progress, token);

            _downloadedPath = path;
            DownloadRegistry.Set(StateKey, path);
            Progress = 100;
            ProgressOpacity = 0;
            PrimaryText = "运行安装包";
            FolderVisibility = Visibility.Visible;
            StatusText = $"已下载 {Path.GetFileName(path)} · 正在准备安装…";
        }
        catch (OperationCanceledException)
        {
            ProgressOpacity = 0;
            Progress = 0;
            PrimaryText = "下载";
            StatusText = "已取消下载";
            Toast.Info("已取消下载", _entry.Name);
        }
        catch (Exception ex)
        {
            ProgressOpacity = 0;
            Progress = 0;
            PrimaryText = "重试";
            StatusText = "下载失败：" + Describe(ex);
            Toast.Error("下载失败", _entry.Name + " · " + Describe(ex), 10);
        }
        finally
        {
            if (_cts is not null)
            {
                DownloadTracker.Unregister(_cts);
                _cts.Dispose();
            }

            _cts = null;
            OnPropertyChanged(nameof(IsDownloading));

            if (FolderVisibility != Visibility.Visible)
            {
                PrimaryText = HasDirectLink ? "下载" : "前往官网";
            }
        }
    }

    /// <summary>
    /// 无感安装下载好的安装包：尽力静默，以「卸载注册表里出现该软件」为准判定成功；
    /// 失败则打开图形安装程序让用户手动完成。装完顺手清掉该软件的开机自启动项。
    /// </summary>
    public async Task InstallDownloadedAsync()
    {
        if (_busyInstalling || _downloadedPath is not { Length: > 0 } path || !File.Exists(path))
        {
            return;
        }

        // 设置里配了安装根目录就装到「根目录\软件英文名」，否则用软件默认目录
        string root = AppSettings.Current.InstallDir;
        string? targetDir = string.IsNullOrWhiteSpace(root)
            ? null
            : AppInstall.BuildInstallDir(root, _entry.Name, _entry.Id);

        _busyInstalling = true;
        OnPropertyChanged(nameof(IsBusyInstalling));
        PrimaryText = "安装中…";
        StatusText = targetDir is not null ? $"正在安装到 {targetDir}…" : "正在无感安装…";
        ProgressOpacity = 0;

        bool ok;
        try
        {
            ok = await AppInstall.InstallAsync(path, _entry.Name, targetDir);
        }
        catch
        {
            ok = false;
        }
        finally
        {
            _busyInstalling = false;
            OnPropertyChanged(nameof(IsBusyInstalling));
        }

        if (ok && AppInstall.IsInstalled(_entry.Name))
        {
            _installed = true;

            // 清掉该软件安装时顺手加的开机自启动，避免每次开机都自己弹出来
            try
            {
                string? exe = AppInstall.FindExecutable(_entry.Name);
                AutostartHelper.RemoveFor(exe, targetDir);
            }
            catch
            {
                // 自启动清理失败不影响安装结果
            }

            bool deleted = await TryDeleteInstallerAsync();
            if (deleted)
            {
                FolderVisibility = Visibility.Collapsed;
                PrimaryText = "已安装";
            }
            else
            {
                // 装好了但文件还被占用没删掉，别谎报已删除
                FolderVisibility = Visibility.Visible;
                PrimaryText = "删除安装包";
            }

            StatusText = deleted ? "安装完成 · 安装包已自动删除" : "安装完成 · 安装包仍被占用，稍后可再点删除";
        }
        else
        {
            // 静默失败：打开图形安装程序，让用户手动选位置完成
            FolderVisibility = Visibility.Visible;
            PrimaryText = "运行安装包";
            StatusText = "无感安装不支持，已打开图形安装程序，请手动完成";
            AppDownloads.RunFile(path);
        }
    }

    /// <summary>
    /// 非静默安装：直接打开安装程序，由用户在安装界面里自己选选项 / 路径，装完回来核对状态。
    /// </summary>
    public async Task InstallInteractivelyAsync()
    {
        if (_busyInstalling || _downloadedPath is not { Length: > 0 } path || !File.Exists(path))
        {
            return;
        }

        _busyInstalling = true;
        OnPropertyChanged(nameof(IsBusyInstalling));
        PrimaryText = "安装中…";
        ProgressOpacity = 0;
        FolderVisibility = Visibility.Visible;
        StatusText = "已打开安装程序，请在窗口里完成安装";

        if (!AppDownloads.RunFile(path))
        {
            _busyInstalling = false;
            OnPropertyChanged(nameof(IsBusyInstalling));
            PrimaryText = "运行安装包";
            StatusText = "打不开安装程序，点「打开文件夹」手动双击运行";
            Toast.Error("打不开安装程序", _entry.Name);
            return;
        }

        // 最多等 3 分钟：装好了就把卡片切成「已安装」；安装程序提前关掉就立刻收尾
        string processName = Path.GetFileNameWithoutExtension(path);

        for (int i = 0; i < 90; i++)
        {
            await Task.Delay(2000);

            if (AppInstall.IsInstalled(_entry.Name))
            {
                _installed = true;
                _busyInstalling = false;
                OnPropertyChanged(nameof(IsBusyInstalling));
                PrimaryText = "已安装";
                StatusText = "安装完成 · 安装包保留在下载文件夹";
                Toast.Success("安装完成", _entry.Name);
                return;
            }

            // 安装程序已经退出（用户取消或直接关掉），不用再等满 3 分钟
            if (i >= 3 && !IsProcessRunning(processName))
            {
                break;
            }
        }

        _busyInstalling = false;
        OnPropertyChanged(nameof(IsBusyInstalling));
        PrimaryText = "运行安装包";
        StatusText = "没检测到安装结果：可能取消了安装，可再点一次";
        Toast.Info("没检测到安装结果", _entry.Name + " · 可能取消了安装");
    }

    private static bool IsProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>安装后清掉安装包：带重试，安装器进程收尾时文件常被占用。真正删掉才返回 true。</summary>
    private async Task<bool> TryDeleteInstallerAsync()
    {
        if (_downloadedPath is not { Length: > 0 } path)
        {
            return false;
        }

        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (!File.Exists(path))
            {
                ClearDownloaded();
                return true;
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
                // 被占用，等一会儿再试
            }

            if (!File.Exists(path))
            {
                ClearDownloaded();
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }

    private void ClearDownloaded()
    {
        _downloadedPath = null;
        DownloadRegistry.Remove(StateKey);
    }

    /// <summary>装好了但安装包没删掉时，点按钮再清一次。</summary>
    public async Task DeleteLeftoverInstallerAsync()
    {
        bool deleted = await TryDeleteInstallerAsync();
        if (deleted)
        {
            FolderVisibility = Visibility.Collapsed;
            PrimaryText = "已安装";
            StatusText = "安装包已删除";
        }
        else
        {
            StatusText = "删除失败，安装包仍被占用，稍后再试";
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException http => $"网络问题（{http.StatusCode?.ToString() ?? "无法连接"}）",
        IOException => "写文件失败，检查磁盘空间或杀毒软件",
        _ => ex.Message,
    };

    private static Brush CreateTileBrush(string hex)
    {
        Color baseColor = ParseColor(hex);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
        };

        brush.GradientStops.Add(new GradientStop { Color = baseColor, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Lighten(baseColor, 0.28), Offset = 1 });

        return brush;
    }

    private static Color ParseColor(string hex)
    {
        string value = hex.TrimStart('#');

        try
        {
            return Color.FromArgb(
                0xFF,
                Convert.ToByte(value.Substring(0, 2), 16),
                Convert.ToByte(value.Substring(2, 2), 16),
                Convert.ToByte(value.Substring(4, 2), 16));
        }
        catch (Exception)
        {
            return Color.FromArgb(0xFF, 0x6C, 0x4C, 0xF1);
        }
    }

    private static Color Lighten(Color color, double amount)
    {
        byte Mix(byte channel) => (byte)Math.Clamp(channel + ((255 - channel) * amount), 0, 255);

        return Color.FromArgb(color.A, Mix(color.R), Mix(color.G), Mix(color.B));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
