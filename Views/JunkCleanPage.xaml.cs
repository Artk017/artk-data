using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIDemo.Services;

namespace WinUIDemo.Views;

/// <summary>垃圾清理页：Windows 清理规则、扫描与删除。</summary>
public sealed partial class JunkCleanPage : Page
{
    private readonly CleanerEngine _engine = new();
    private readonly ObservableCollection<CleanerCategoryItem> _categories = new();
    private readonly ObservableCollection<ScanResultItem> _results = new();
    private readonly List<ScanResult> _scanResults = new();

    private List<CleanerEntry> _allEntries = new();
    private HashSet<string> _selectedNames = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private bool _loaded;
    private bool _busy;
    private bool _suppressSave;

    public JunkCleanPage()
    {
        InitializeComponent();

        CategoriesList.ItemsSource = _categories;
        ResultsList.ItemsSource = _results;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await LoadCleanerAsync();
    }

    // ---------------- 加载规则 ----------------

    private async Task LoadCleanerAsync()
    {
        SetBusy(true, "正在解析清理规则…");

        try
        {
            _allEntries = await CleanerLibrary.LoadInstalledAsync();

            InitializeSelection();
            BuildCategories(SearchBox.Text);
            UpdateSelectionSummary();
            UpdateCategoryEmptyState();

            int selected = _allEntries.Count(e => _selectedNames.Contains(e.Name));
            StatusText.Text = $"已识别 {_allEntries.Count} 个可清理项目，当前勾选 {selected} 项。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"清理规则加载失败：{ex.Message}";
            EmptyCategoriesText.Text = "无法加载清理规则，请确认 Assets\\Winapp2.ini 是否存在。";
            EmptyCategoriesText.Visibility = Visibility.Visible;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void InitializeSelection()
    {
        var settings = AppSettings.Current;

        if (settings.CleanerSelectionSaved)
        {
            // 旧版本把翻译后的中文名也写进了勾选集，这里统一映射回数据库原始名称
            _selectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var saved in settings.CleanerSelection)
            {
                var entry = _allEntries.FirstOrDefault(e =>
                    string.Equals(e.Name, saved, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(CleanerNameTranslator.Translate(e.Name), saved, StringComparison.OrdinalIgnoreCase));

                if (entry is not null)
                {
                    _selectedNames.Add(entry.Name);
                }
            }
        }
        else
        {
            // 没有保存过勾选时，沿用数据库里的 Default=True
            _selectedNames = new HashSet<string>(
                _allEntries.Where(e => e.Default).Select(e => e.Name),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    // ---------------- 列表构建 ----------------

    private void BuildCategories(string? filter)
    {
        _categories.Clear();

        var keyword = (filter ?? string.Empty).Trim();
        bool searching = keyword.Length > 0;

        var visible = _allEntries
            .Where(e => !searching ||
                        CleanerNameTranslator.Translate(e.Name).Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        e.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        e.Category.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var groups = visible
            .GroupBy(e => e.Category)
            .OrderBy(g => g.Key.Order)
            .ThenBy(g => g.Key.Name, StringComparer.CurrentCultureIgnoreCase);

        foreach (var group in groups)
        {
            var category = new CleanerCategoryItem(group.Key);

            foreach (var entry in group.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new CleanerEntryItem(entry, _selectedNames.Contains(entry.Name));

                // 已经扫过的项目，重建列表时把体积带回来
                var scanned = _scanResults.FirstOrDefault(r => ReferenceEquals(r.Entry, entry));
                if (scanned is not null)
                {
                    item.SizeText = scanned.FormattedSize;
                }

                item.PropertyChanged += EntryItem_PropertyChanged;
                category.Entries.Add(item);
            }

            // 搜索时全部展开，平时保持折叠
            category.IsExpanded = searching;
            _categories.Add(category);
        }

        UpdateCategoryEmptyState();
    }

    private void UpdateCategoryEmptyState()
    {
        if (_categories.Count > 0)
        {
            EmptyCategoriesText.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyCategoriesText.Visibility = Visibility.Visible;
        EmptyCategoriesText.Text = !_loaded
            ? "正在识别本机已安装的应用…"
            : !string.IsNullOrWhiteSpace(SearchBox.Text)
                ? "没有找到匹配的清理项目。"
                : "没有可清理的项目。";
    }

    private void EntryItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CleanerEntryItem.IsSelected) || sender is not CleanerEntryItem item)
        {
            return;
        }

        // 勾选集里存的是数据库原始名称，不能用翻译后的显示名
        if (item.IsSelected)
        {
            _selectedNames.Add(item.Entry.Name);
        }
        else
        {
            _selectedNames.Remove(item.Entry.Name);
        }

        SaveSelection();
        UpdateSelectionSummary();
        InvalidateScanResults();
    }

    /// <summary>勾选变化后，旧的扫描结果不再可信，清空并要求重新扫描。</summary>
    private void InvalidateScanResults()
    {
        if (_scanResults.Count == 0 && _results.Count == 0)
        {
            return;
        }

        _scanResults.Clear();
        _results.Clear();

        ResultTotalText.Text = "";
        TotalSizeText.Text = "0 B";
        TotalMetaText.Text = "";

        ClearAllEntrySizes();
        UpdateResultsEmptyState("勾选已更改，请重新扫描。");

        if (!_busy)
        {
            CleanButton.IsEnabled = false;
            StatusText.Text = "勾选已更改，请重新扫描。";
        }
    }

    private void ClearAllEntrySizes()
    {
        foreach (var item in _categories.SelectMany(c => c.Entries))
        {
            item.SizeText = "";
        }
    }

    private void UpdateSelectionSummary()
    {
        if (_allEntries.Count == 0)
        {
            SelectionSummaryText.Text = "";
            return;
        }

        int selected = _allEntries.Count(e => _selectedNames.Contains(e.Name));
        SelectionSummaryText.Text = $"已选 {selected} / {_allEntries.Count} 项";
    }

    private void SaveSelection()
    {
        if (_suppressSave)
        {
            return;
        }

        var settings = AppSettings.Current;
        settings.CleanerSelection = _selectedNames.ToList();
        settings.CleanerSelectionSaved = true;
        settings.Save();
    }

    // ---------------- 搜索 / 勾选 ----------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        BuildCategories(SearchBox.Text);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAllSelected(true);

    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAllSelected(false);

    private void SetAllSelected(bool selected)
    {
        InvalidateScanResults();
        _suppressSave = true;

        foreach (var item in _categories.SelectMany(c => c.Entries))
        {
            item.IsSelected = selected;
        }

        _selectedNames.Clear();
        if (selected)
        {
            foreach (var entry in _allEntries)
            {
                _selectedNames.Add(entry.Name);
            }
        }

        _suppressSave = false;
        SaveSelection();
        UpdateSelectionSummary();
    }

    private void SelectDefaults_Click(object sender, RoutedEventArgs e)
    {
        InvalidateScanResults();
        _selectedNames.Clear();

        foreach (var entry in _allEntries.Where(entry => entry.Default))
        {
            _selectedNames.Add(entry.Name);
        }

        SaveSelection();
        BuildCategories(SearchBox.Text);
        UpdateSelectionSummary();
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e) => SetAllExpanded(true);

    private void CollapseAll_Click(object sender, RoutedEventArgs e) => SetAllExpanded(false);

    private void SetAllExpanded(bool expanded)
    {
        foreach (var category in _categories)
        {
            category.IsExpanded = expanded;
        }
    }

    // ---------------- 扫描 ----------------

    private async void Scan_Click(object sender, RoutedEventArgs e) => await RunScanAsync();

    private async Task RunScanAsync()
    {
        if (_busy)
        {
            return;
        }

        var selected = _allEntries.Where(e => _selectedNames.Contains(e.Name)).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "请先勾选要扫描的项目。";
            return;
        }

        _scanResults.Clear();
        _results.Clear();
        UpdateResultsEmptyState("正在扫描…");

        using var cts = new CancellationTokenSource();
        _cts = cts;
        SetBusy(true, "正在扫描…");

        long totalBytes = 0;
        int totalFiles = 0;
        int totalRegistry = 0;
        int done = 0;
        int total = selected.Count;

        TotalSizeText.Text = "0 B";
        TotalMetaText.Text = "";
        ResultTotalText.Text = "";
        ScanProgressBar.Value = 0;
        ProgressText.Text = "0%";

        try
        {
            foreach (var entry in selected)
            {
                cts.Token.ThrowIfCancellationRequested();

                StatusText.Text = $"正在扫描：{CleanerNameTranslator.Translate(entry.Name)}（{done + 1}/{total}）";

                var result = await _engine.AnalyzeAsync(entry, null, cts.Token);

                if (result.FilesToDelete.Count > 0 || result.RegistryToDelete.Count > 0)
                {
                    _scanResults.Add(result);
                    _results.Add(new ScanResultItem(result));
                }

                UpdateEntrySize(entry, result.FormattedSize);

                totalBytes += result.TotalBytes;
                totalFiles += result.FilesToDelete.Count;
                totalRegistry += result.RegistryToDelete.Count;

                done++;
                ScanProgressBar.Value = done * 100.0 / total;
                ProgressText.Text = $"{done * 100.0 / total:0}%";
                TotalSizeText.Text = ScanResult.FormatBytes(totalBytes);
            }

            ResultTotalText.Text = ScanResult.FormatBytes(totalBytes);
            TotalMetaText.Text = $"{totalFiles} 个文件 · {totalRegistry} 项注册表";
            StatusText.Text = _scanResults.Count == 0
                ? "扫描完成，没有发现可清理的内容。"
                : $"扫描完成：{totalFiles} 个文件、{totalRegistry} 项注册表，共 {ScanResult.FormatBytes(totalBytes)}。";

            UpdateResultsEmptyState(_scanResults.Count == 0 ? "没有发现可清理的内容。" : null);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "扫描已取消。";
            UpdateResultsEmptyState("扫描已取消，可重新开始扫描。");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"扫描出错：{ex.Message}";
            UpdateResultsEmptyState("扫描出错，可重新尝试。");
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            SetBusy(false);
        }
    }

    // ---------------- 清理 ----------------

    private async void Clean_Click(object sender, RoutedEventArgs e) => await RunCleanAsync();

    private async Task RunCleanAsync()
    {
        if (_busy)
        {
            return;
        }

        // 没扫描过就直接清理：先自动扫描
        if (_scanResults.Count == 0)
        {
            await RunScanAsync();
        }

        if (_scanResults.Count == 0)
        {
            return;
        }

        if (!await CheckRunningBrowsersAsync())
        {
            return;
        }

        if (!await ConfirmWarningsAsync())
        {
            return;
        }

        long scannedBytes = _scanResults.Sum(r => r.TotalBytes);
        int fileCount = _scanResults.Sum(r => r.FilesToDelete.Count);
        int registryCount = _scanResults.Sum(r => r.RegistryToDelete.Count);

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认清理",
            Content = $"将删除 {fileCount} 个文件和 {registryCount} 项注册表内容，预计释放 {ScanResult.FormatBytes(scannedBytes)}。\n\n" +
                      "正在使用中的文件会自动跳过，不会影响系统运行。",
            PrimaryButtonText = "开始清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        SetBusy(true, "正在清理…");

        var snapshot = _scanResults.ToList();
        int done = 0;
        int total = snapshot.Count;
        long freed = 0;
        int removed = 0;

        ScanProgressBar.Value = 0;
        ProgressText.Text = "0%";

        try
        {
            foreach (var result in snapshot)
            {
                cts.Token.ThrowIfCancellationRequested();

                StatusText.Text = $"正在清理：{CleanerNameTranslator.Translate(result.Entry.Name)}（{done + 1}/{total}）";

                var (count, bytes) = await _engine.CleanAsync(result, null, cts.Token);
                removed += count;
                freed += bytes;

                RemoveScanResult(result);
                UpdateEntrySize(result.Entry, "");

                done++;
                ScanProgressBar.Value = done * 100.0 / total;
                ProgressText.Text = $"{done * 100.0 / total:0}%";
                UpdateResultsTotal();
            }

            StatusText.Text = freed > 0
                ? $"清理完成：删除 {removed} 个项目，释放 {ScanResult.FormatBytes(freed)}。"
                : "清理完成：没有可删除的内容（文件可能正在使用）。";

            UpdateResultsEmptyState("清理完成，可重新扫描。");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = freed > 0
                ? $"清理已取消，目前已释放 {ScanResult.FormatBytes(freed)}。"
                : "清理已取消。";
            UpdateResultsEmptyState("清理已取消，可重新扫描。");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"清理出错：{ex.Message}";
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            SetBusy(false);
        }
    }

    /// <summary>选中的规则涉及浏览器且浏览器正在运行时，提示先关闭。</summary>
    private static readonly (string Process, string Name, int[] Codes)[] KnownBrowsers =
    {
        ("chrome", "Google Chrome", new[] { 3029 }),
        ("firefox", "Mozilla Firefox", new[] { 3026 }),
        ("msedge", "Microsoft Edge", new[] { 3006 }),
        ("opera", "Opera", new[] { 3027, 3035 }),
        ("brave", "Brave", new[] { 3034 }),
        ("vivaldi", "Vivaldi", new[] { 3033 }),
    };

    private async Task<bool> CheckRunningBrowsersAsync(IEnumerable<CleanerEntry>? entries = null)
    {
        var codes = (entries ?? _allEntries.Where(e => _selectedNames.Contains(e.Name)))
            .Select(e => e.LangSecRef ?? -1)
            .ToHashSet();

        var running = KnownBrowsers
            .Where(b => b.Codes.Any(codes.Contains))
            .Where(b => Process.GetProcessesByName(b.Process).Length > 0)
            .Select(b => b.Name)
            .Distinct()
            .ToList();

        if (running.Count == 0)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "正在运行的浏览器",
            Content = $"为获得最佳清理效果，建议先关闭以下应用：{string.Join("、", running)}。{Environment.NewLine}{Environment.NewLine}" +
                      "正在运行的浏览器会锁定缓存和历史记录文件，清理时这些文件会被跳过。",
            PrimaryButtonText = "仍然继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> ConfirmWarningsAsync()
    {
        var warnings = _allEntries
            .Where(e => _selectedNames.Contains(e.Name) && !string.IsNullOrWhiteSpace(e.Warning))
            .Select(e => $"{CleanerNameTranslator.Translate(e.Name)}{Environment.NewLine}" +
                         $"{CleanerNameTranslator.TranslateWarning(e.Warning)}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (warnings.Count == 0)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "风险提示",
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                Content = new TextBlock
                {
                    Text = "以下项目删除后可能影响对应应用的功能，请确认后继续：" +
                           $"{Environment.NewLine}{Environment.NewLine}" +
                           string.Join($"{Environment.NewLine}{Environment.NewLine}", warnings),
                    TextWrapping = TextWrapping.Wrap,
                },
            },
            PrimaryButtonText = "仍然继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "正在取消…";
    }

    // ---------------- 结果列表 / 详情 ----------------

    private async void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ScanResultItem line || _busy)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = line.AppName,
            Content = BuildDetailContent(line.Result),
            PrimaryButtonText = "清理此项",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await CleanSingleAsync(line);
        }
    }

    private static ScrollViewer BuildDetailContent(ScanResult result)
    {
        var panel = new StackPanel { Spacing = 2 };

        void AddHeader(string text) => panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 2),
        });

        void AddLine(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Opacity = 0.72,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
            };

            ToolTipService.SetToolTip(block, text);
            panel.Children.Add(block);
        }

        const int maxLines = 300;

        if (result.FilesToDelete.Count > 0)
        {
            AddHeader($"文件（{result.FilesToDelete.Count}）");

            foreach (var file in result.FilesToDelete.Take(maxLines))
            {
                AddLine(file);
            }

            if (result.FilesToDelete.Count > maxLines)
            {
                AddLine($"… 其余 {result.FilesToDelete.Count - maxLines} 个文件未显示");
            }
        }

        if (result.RegistryToDelete.Count > 0)
        {
            AddHeader($"注册表项（{result.RegistryToDelete.Count}）");

            foreach (var item in result.RegistryToDelete.Take(maxLines))
            {
                AddLine(item.ToString());
            }

            if (result.RegistryToDelete.Count > maxLines)
            {
                AddLine($"… 其余 {result.RegistryToDelete.Count - maxLines} 项未显示");
            }
        }

        return new ScrollViewer
        {
            MaxHeight = 420,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel,
        };
    }

    private async Task CleanSingleAsync(ScanResultItem line)
    {
        if (!await CheckRunningBrowsersAsync(new[] { line.Result.Entry }))
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        SetBusy(true, $"正在清理：{line.AppName}…");

        try
        {
            var (count, bytes) = await _engine.CleanAsync(line.Result, null, cts.Token);

            RemoveScanResult(line.Result);
            UpdateEntrySize(line.Result.Entry, "");
            UpdateResultsTotal();

            StatusText.Text = bytes > 0
                ? $"{line.AppName}：删除 {count} 个项目，释放 {ScanResult.FormatBytes(bytes)}。"
                : $"{line.AppName}：没有可删除的内容（文件可能正在使用）。";

            UpdateResultsEmptyState("清理完成，可重新扫描。");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "清理已取消。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"清理出错：{ex.Message}";
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            SetBusy(false);
        }
    }

    private void RemoveScanResult(ScanResult result)
    {
        _scanResults.Remove(result);

        var item = _results.FirstOrDefault(r => ReferenceEquals(r.Result, result));
        if (item is not null)
        {
            _results.Remove(item);
        }
    }

    private void UpdateResultsTotal()
    {
        long bytes = _scanResults.Sum(r => r.TotalBytes);
        int files = _scanResults.Sum(r => r.FilesToDelete.Count);
        int registry = _scanResults.Sum(r => r.RegistryToDelete.Count);

        ResultTotalText.Text = bytes > 0 ? ScanResult.FormatBytes(bytes) : "";
        TotalSizeText.Text = bytes > 0 ? ScanResult.FormatBytes(bytes) : "0 B";
        TotalMetaText.Text = bytes > 0 ? $"{files} 个文件 · {registry} 项注册表" : "";
    }

    private void UpdateResultsEmptyState(string? message)
    {
        if (_results.Count > 0)
        {
            EmptyResultsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyResultsPanel.Visibility = Visibility.Visible;

        if (message is not null)
        {
            EmptyResultsText.Text = message;
        }
    }

    private void UpdateEntrySize(CleanerEntry entry, string sizeText)
    {
        foreach (var item in _categories.SelectMany(c => c.Entries))
        {
            if (ReferenceEquals(item.Entry, entry))
            {
                item.SizeText = sizeText;
            }
        }
    }

    // ---------------- 忙碌状态 ----------------

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;

        ScanButton.IsEnabled = !busy;
        CleanButton.IsEnabled = !busy && _scanResults.Count > 0;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        ScanProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        CategoriesList.IsHitTestVisible = !busy;
        SelectionToolbar.IsHitTestVisible = !busy;

        foreach (var button in SelectionToolbar.Children.OfType<Button>())
        {
            button.IsEnabled = !busy;
        }

        SearchBox.IsEnabled = !busy;

        if (status is not null)
        {
            StatusText.Text = status;
        }
    }
}
