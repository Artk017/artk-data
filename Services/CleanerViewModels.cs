using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace WinUIDemo.Services;

/// <summary>左侧列表里的一行清理项目。</summary>
public sealed class CleanerEntryItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _sizeText = "";

    public CleanerEntryItem(CleanerEntry entry, bool isSelected)
    {
        Entry = entry;
        _isSelected = isSelected;
    }

    public CleanerEntry Entry { get; }

    public string Name => CleanerNameTranslator.Translate(Entry.Name);

    public string? Warning => CleanerNameTranslator.TranslateWarning(Entry.Warning);

    public bool HasWarning => !string.IsNullOrWhiteSpace(Entry.Warning);

    public Visibility WarningVisibility => HasWarning ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>是否勾选；变更后由页面更新勾选集合并持久化。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>扫描后显示的可清理体积，空字符串表示尚未扫描。</summary>
    public string SizeText
    {
        get => _sizeText;
        set
        {
            if (_sizeText == value)
            {
                return;
            }

            _sizeText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeVisibility));
        }
    }

    public Visibility SizeVisibility =>
        string.IsNullOrEmpty(_sizeText) ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>左侧列表里的一个分类（对应一个 Expander）。</summary>
public sealed class CleanerCategoryItem : INotifyPropertyChanged
{
    private bool _isExpanded;

    public CleanerCategoryItem(CleanerCategory category)
    {
        Category = category;
    }

    public CleanerCategory Category { get; }

    public string Name => Category.Name;

    public ObservableCollection<CleanerEntryItem> Entries { get; } = new();

    public string CountText => $"{Entries.Count} 项";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>右侧结果列表的一行。</summary>
public sealed class ScanResultItem
{
    public ScanResultItem(ScanResult result)
    {
        Result = result;
        AppName = CleanerNameTranslator.Translate(result.Entry.Name);
        CategoryName = result.Entry.Category.Name;
        Size = result.FormattedSize;

        var parts = new System.Collections.Generic.List<string>();
        if (result.FilesToDelete.Count > 0)
        {
            parts.Add($"{result.FilesToDelete.Count} 个文件");
        }

        if (result.RegistryToDelete.Count > 0)
        {
            parts.Add($"{result.RegistryToDelete.Count} 项注册表");
        }

        CountSummary = string.Join(" · ", parts);
    }

    public ScanResult Result { get; }

    public string AppName { get; }

    public string CategoryName { get; }

    public string CountSummary { get; }

    public string Size { get; }
}

/// <summary>详情对话框中的一行：标题行或路径行。</summary>
public sealed record CleanerDetailItem(string Text, bool IsHeader)
{
    public bool IsNotHeader => !IsHeader;
}
