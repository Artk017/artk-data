using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ArtkTools.Services;

namespace ArtkTools.Views;

/// <summary>
/// 「微软词汇」弹窗：把本机微软拼音词库上传到服务器，或把服务器上的词库导回本机。
/// 外壳由 <see cref="ToolWindow"/> 负责，结果用主窗口右上角的 Toast 提示。
/// </summary>
public sealed partial class PinyinVocabView : UserControl
{
    private bool _busy;

    public PinyinVocabView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshState();
    }

    private async void RefreshState()
    {
        var (count, size) = PinyinVocab.LocalStats();
        LocalStatusText.Text = count == 0
            ? "没有找到微软拼音词库"
            : $"{count} 个文件 · {FormatSize(size)}";

        if (!CatalogServer.Configured)
        {
            ServerStatusText.Text = "没有配置服务器";
            return;
        }

        try
        {
            PinyinVocabInfo info = await PinyinVocab.FetchInfoAsync();

            ServerStatusText.Text = info.Exists
                ? $"{info.UploadedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {info.Count} 个文件 · {FormatSize(info.Size)}"
                : "还没有上传过词汇";
        }
        catch (Exception ex)
        {
            ServerStatusText.Text = "读取失败：" + ex.Message;
        }
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        Border? busy = Toast.Busy("正在上传微软词汇", "打包词库并发送到服务器…");

        try
        {
            var (ok, message) = await PinyinVocab.UploadAsync();
            Toast.Dismiss(busy);

            if (ok)
            {
                Toast.Success("微软词汇已上传", message);
            }
            else
            {
                Toast.Error("上传失败", message);
            }
        }
        catch (Exception ex)
        {
            Toast.Dismiss(busy);
            Toast.Error("上传失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        RefreshState();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        Border? busy = Toast.Busy("正在下载微软词汇", "下载后会重启微软拼音并导入…");

        try
        {
            var (ok, message) = await PinyinVocab.ImportAsync();
            Toast.Dismiss(busy);

            if (ok)
            {
                Toast.Success("微软词汇已导入", message, 8);
            }
            else
            {
                Toast.Error("导入失败", message);
            }
        }
        catch (Exception ex)
        {
            Toast.Dismiss(busy);
            Toast.Error("导入失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        RefreshState();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        Border? busy = Toast.Busy("正在下载微软词汇", "取回词库文件，不修改本机输入法…");

        try
        {
            var (ok, message) = await PinyinVocab.SaveToFileAsync();
            Toast.Dismiss(busy);

            if (ok)
            {
                Toast.Success("词库已保存", message, 8);
            }
            else
            {
                Toast.Error("下载失败", message);
            }
        }
        catch (Exception ex)
        {
            Toast.Dismiss(busy);
            Toast.Error("下载失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UploadButton.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
    }

    private static string FormatSize(long bytes)
    {
        return bytes >= 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0:0.0} MB"
            : $"{bytes / 1024.0:0.#} KB";
    }
}
