using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ArtkTools.Helpers;
using Windows.Graphics;

namespace ArtkTools.Views;

/// <summary>Windows 页：系统设置与系统信息直接展开，清理功能在单独的“垃圾清理”页。</summary>
public sealed partial class WindowsPage : Page
{
    private readonly ToolWindowSlot _vocab = new(
        "微软词汇", "\uE82D", new SizeInt32(470, 380), () => new PinyinVocabView(),
        new SizeInt32(400, 320));

    public WindowsPage()
    {
        InitializeComponent();
    }

    private void OpenPinyinVocab_Click(object sender, RoutedEventArgs e) => _vocab.Show();

    private async void FileLocations_Click(object sender, RoutedEventArgs e)
    {
        // 更改 下载 / 文档 / 图片 / 视频 的存放位置（等同属性里的「位置 → 移动」）
        await FileLocationsDialog.ShowAsync(XamlRoot);
    }
}
