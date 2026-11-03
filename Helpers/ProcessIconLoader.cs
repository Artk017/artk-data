using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinUIDemo.Helpers;

/// <summary>
/// 从可执行文件提取关联图标并转换成 WinUI 可用的 ImageSource。
/// 结果按路径缓存，避免重复解码；解码（GDI+）放到后台线程，别卡 UI。
/// </summary>
public static class ProcessIconLoader
{
    private static readonly object Gate = new();

    private static readonly Dictionary<string, BitmapImage?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static BitmapImage? TryGetCached(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return null;
        }

        lock (Gate)
        {
            return Cache.TryGetValue(executablePath, out var image) ? image : null;
        }
    }

    public static async Task<BitmapImage?> GetAsync(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(executablePath, out var cached))
            {
                return cached;
            }
        }

        // 提取 + 编码是纯 CPU/GDI 操作，扔到后台线程，UI 线程只负责最后的 SetSourceAsync
        byte[]? png = await Task.Run(() => ExtractPng(executablePath));

        BitmapImage? image = null;
        if (png is not null)
        {
            try
            {
                image = new BitmapImage();
                using var stream = new MemoryStream(png);
                await image.SetSourceAsync(stream.AsRandomAccessStream());
            }
            catch
            {
                image = null;
            }
        }

        lock (Gate)
        {
            Cache[executablePath] = image;
        }

        return image;
    }

    private static byte[]? ExtractPng(string executablePath)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(executablePath);
            if (icon is null)
            {
                return null;
            }

            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        catch
        {
            // 没有图标或文件不可读时返回 null，界面显示占位
            return null;
        }
    }
}
