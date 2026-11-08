using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ArtkTools.Helpers;
using ArtkTools.Services.ComputerTools;

namespace ArtkTools.Views;

/// <summary>左侧“场景分类”的一项（Scene 为空表示全部场景）。</summary>
public sealed class CmSceneNavItem
{
    public string Scene { get; init; } = "";

    public string Key { get; init; } = "";

    public int Count { get; init; }

    public string CountText => Count.ToString();
}

/// <summary>主列表中的“场景分区标题”行（不可操作）。</summary>
public sealed class CmSectionHeader
{
    public string Key { get; init; } = "";

    public int Count { get; init; }

    public string CountText => Count.ToString();
}

/// <summary>列表行模板选择器：分区标题与菜单条目分别使用不同模板。</summary>
public sealed class CmRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ItemTemplate { get; set; }

    public DataTemplate? HeaderTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
        => item is CmSectionHeader ? HeaderTemplate! : ItemTemplate!;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}

/// <summary>
/// 右键菜单管理页：与 tubatools-master 一致，按固定场景顺序分组显示，
/// 默认只显示已识别来源的第三方菜单，可切换到“展示全部”。
/// </summary>
public sealed partial class ContextMenuPage : Page
{
    private readonly List<ContextMenuEntry> _entries = new();
    private bool _loaded;
    private bool _allMode;
    private string _scene = "";
    private string _lastStatus = "";
    private bool _syncingSceneNav;
    private int _generation;

    public ContextMenuPage()
    {
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await LoadAsync();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(ComputerToolsPage));
        }
    }

    private async Task LoadAsync()
    {
        int generation = ++_generation;
        MenuRefreshButton.IsEnabled = false;
        MenuStatusText.Text = "正在枚举当前用户、所有用户以及 32/64 位右键入口……";

        try
        {
            IReadOnlyList<ContextMenuEntry> entries = await ContextMenuService.ScanAsync();

            if (generation != _generation)
            {
                return;
            }

            _entries.Clear();
            _entries.AddRange(entries);
            _allMode = false;
            AllModeCheck.IsChecked = false;
            _scene = "";

            ApplyFilter();
            _ = HydrateIconsAsync(entries, generation);
        }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                MenuStatusText.Text = "读取失败：" + ex.Message;
            }
        }
        finally
        {
            if (generation == _generation)
            {
                MenuRefreshButton.IsEnabled = true;
            }
        }
    }

    /// <summary>后台提取每个条目的软件图标，提取到一个就地更新一个。</summary>
    private async Task HydrateIconsAsync(IReadOnlyList<ContextMenuEntry> entries, int generation)
    {
        var pending = entries
            .Where(e => e.IconPath.Length > 0 && !e.IconLoaded)
            .ToList();

        if (pending.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(4);
        int done = 0;

        await Task.WhenAll(pending.Select(async entry =>
        {
            await gate.WaitAsync();

            try
            {
                entry.IconDisplay = await ProcessIconLoader.GetIconAsync(entry.IconPath, entry.IconIndex);
            }
            catch
            {
                entry.IconDisplay = null;
            }
            finally
            {
                entry.IconLoaded = true;
                int completed = Interlocked.Increment(ref done);

                if (generation == _generation && completed % 24 == 0)
                {
                    MenuStatusText.Text = $"正在提取软件图标… {completed} / {pending.Count}";
                }

                gate.Release();
            }
        }));

        if (generation == _generation)
        {
            MenuStatusText.Text = _lastStatus;
        }
    }

    private void AllMode_Click(object sender, RoutedEventArgs e)
    {
        _allMode = AllModeCheck.IsChecked == true;

        if (_loaded)
        {
            ApplyFilter();
        }
    }

    private void MenuSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loaded)
        {
            ApplyFilter();
        }
    }

    private async void MenuRefresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void SceneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSceneNav)
        {
            return;
        }

        _scene = (SceneList.SelectedItem as CmSceneNavItem)?.Scene ?? "";
        // 切换场景只刷新右侧列表，左侧场景栏保持不动（避免每次点击都重建、闪烁）
        ApplyFilter(rebuildSceneNav: false);
    }

    private void ApplyFilter(bool rebuildSceneNav = true)
    {
        string query = MenuSearch.Text.Trim();
        bool searching = query.Length > 0;

        List<ContextMenuEntry> pool = searching
            ? _entries.Where(e => ContextMenuService.MatchesKeyword(e, query)).ToList()
            : _entries.Where(e => _allMode || ContextMenuService.MatchesMainMenuList(e)).ToList();

        if (_scene.Length > 0 && !pool.Any(e => e.Scene == _scene))
        {
            _scene = "";
        }

        List<ContextMenuEntry> visible = _scene.Length == 0
            ? pool
            : pool.Where(e => e.Scene == _scene).ToList();

        // 按固定场景顺序分组，拍平成“分区标题 + 条目”列表交给模板选择器
        var flat = new List<object>();

        foreach (var group in visible
                     .GroupBy(e => e.Scene)
                     .Select(g => (Scene: g.Key, Items: g.ToList()))
                     .OrderBy(g => ContextMenuService.SceneRank(g.Scene))
                     .ThenBy(g => g.Scene, StringComparer.OrdinalIgnoreCase))
        {
            flat.Add(new CmSectionHeader { Key = group.Scene, Count = group.Items.Count });
            flat.AddRange(group.Items);
        }

        MenuList.ItemsSource = flat;
        MenuEmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MenuEmptyText.Text = searching
            ? $"没有找到匹配“{query}”的右键菜单项目。"
            : _scene.Length > 0
                ? "该场景下没有已识别的第三方右键菜单，可打开「展示全部」查看系统项。"
                : "没有找到已识别的第三方右键菜单，可打开「展示全部」查看系统项。";

        int enabled = visible.Count(e => e.Enabled);

        if (searching)
        {
            _lastStatus = $"搜索“{query}”：匹配 {visible.Count} 项（含系统内置与未识别项）";
        }
        else if (_scene.Length > 0)
        {
            _lastStatus = $"场景「{_scene}」：第三方 {visible.Count} 项 · 已显示 {enabled} · 已隐藏 {visible.Count - enabled}";
        }
        else
        {
            _lastStatus = _allMode
                ? $"已显示全部 {visible.Count} 项（含系统内置、未识别与命令仓库）"
                : $"第三方菜单 {visible.Count} 项 · 已显示 {enabled} · 已隐藏 {visible.Count - enabled} · 系统内置默认不显示";
        }

        MenuStatusText.Text = _lastStatus;

        if (rebuildSceneNav)
        {
            RebuildSceneNav(pool);
        }
    }

    private void RebuildSceneNav(IReadOnlyList<ContextMenuEntry> pool)
    {
        var items = new List<CmSceneNavItem>
        {
            new() { Scene = "", Key = "全部场景", Count = pool.Count },
        };

        items.AddRange(pool
            .GroupBy(e => e.Scene)
            .Select(g => new CmSceneNavItem { Scene = g.Key, Key = g.Key, Count = g.Count() })
            .OrderBy(v => ContextMenuService.SceneRank(v.Scene))
            .ThenBy(v => v.Key, StringComparer.OrdinalIgnoreCase));

        _syncingSceneNav = true;

        try
        {
            SceneList.ItemsSource = items;
            SceneList.SelectedItem = items.FirstOrDefault(v => v.Scene == _scene) ?? items[0];
        }
        finally
        {
            _syncingSceneNav = false;
        }
    }

    private async void MenuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not ContextMenuEntry entry)
        {
            return;
        }

        // 初次绑定 / 程序回填时也会触发，值没变就不写注册表
        if (toggle.IsOn == entry.Enabled)
        {
            return;
        }

        bool target = toggle.IsOn;

        try
        {
            await Task.Run(() => ContextMenuService.SetEnabled(entry, target));
        }
        catch (Exception ex)
        {
            toggle.IsOn = entry.Enabled;
            MenuStatusText.Text = "修改失败：" + ex.Message;
            return;
        }

        entry.Enabled = target;
        MenuStatusText.Text = $"「{entry.Name}」已{(target ? "启用" : "禁用")}，重启资源管理器后生效。";
    }

    private async void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ContextMenuEntry entry)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "删除右键菜单项",
            Content = $"确定删除「{entry.Name}」吗？\n\n位置：{entry.RegistryPath}\n来源：{entry.DetailText}\n\n" +
                      "删除后需要重新安装对应软件才能恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await Task.Run(() => ContextMenuService.Delete(entry));
        }
        catch (Exception ex)
        {
            MenuStatusText.Text = "删除失败：" + ex.Message;
            return;
        }

        await LoadAsync();
    }
}
