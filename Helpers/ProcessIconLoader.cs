using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ArtkTools.Helpers;

/// <summary>
/// 从可执行文件 / DLL 提取关联图标并转换成 WinUI 可用的 ImageSource。
/// 结果按路径 + 图标序号缓存，避免重复解码；解码（GDI / GDI+）放到后台线程，别卡 UI。
/// </summary>
public static class ProcessIconLoader
{
    private static readonly object Gate = new();

    private static readonly Dictionary<string, BitmapImage?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, BitmapImage?> IndexedCache =
        new(StringComparer.OrdinalIgnoreCase);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string fileName, int iconIndex,
        IntPtr[]? largeIcons, IntPtr[]? smallIcons, uint iconCount);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

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

    public static BitmapImage? TryGetIndexedCached(string? filePath, int iconIndex)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        lock (Gate)
        {
            return IndexedCache.TryGetValue(IconKey(filePath, iconIndex), out var image) ? image : null;
        }
    }

    /// <summary>
    /// 提取指定文件第 iconIndex 个图标（负数为资源 ID），用于右键菜单这类「Icon=dll,-42」的场景。
    /// </summary>
    public static async Task<BitmapImage?> GetIconAsync(string? filePath, int iconIndex = 0)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        string key = IconKey(filePath, iconIndex);

        lock (Gate)
        {
            if (IndexedCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        byte[]? png = await Task.Run(() => ExtractIndexedPng(filePath, iconIndex));

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
            IndexedCache[key] = image;
        }

        return image;
    }

    private static string IconKey(string path, int index)
    {
        long ticks = 0;
        try
        {
            ticks = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch
        {
            // 文件不可访问时退化为路径缓存
        }

        return $"{path}|{index}|{ticks}";
    }

    private static byte[]? ExtractIndexedPng(string filePath, int iconIndex)
    {
        try
        {
            var small = new IntPtr[1];
            if (ExtractIconEx(filePath, iconIndex, null, small, 1) > 0 && small[0] != IntPtr.Zero)
            {
                try
                {
                    using var icon = (Icon)Icon.FromHandle(small[0]).Clone();
                    using var bitmap = icon.ToBitmap();
                    using var stream = new MemoryStream();
                    bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    return stream.ToArray();
                }
                finally
                {
                    DestroyIcon(small[0]);
                }
            }
        }
        catch
        {
            // 按序号提取失败时回退到关联图标
        }

        return ExtractPng(filePath);
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
