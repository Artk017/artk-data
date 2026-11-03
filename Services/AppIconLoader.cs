using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace WinUIDemo.Services;

/// <summary>
/// 网页上抓来的图标：下载后缓存到本地，再交给卡片 / 选择框显示。
/// 不做解码尺寸以外的处理，失败就返回 null（卡片继续显示首字母）。
/// </summary>
public static class AppIconLoader
{
    private const long MaxBytes = 10L * 1024 * 1024;

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static readonly ConcurrentDictionary<string, BitmapImage> Cache = new();

    private static string Folder => Path.Combine(CatalogServer.Folder, "icons");

    /// <summary>拿一张图片；同一次运行里同一地址只下载一次。</summary>
    public static async Task<BitmapImage?> GetAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Cache.TryGetValue(url, out BitmapImage? cached))
        {
            return cached;
        }

        try
        {
            string path = CachePath(url);
            byte[]? bytes = null;

            if (File.Exists(path))
            {
                bytes = await File.ReadAllBytesAsync(path);
            }
            else
            {
                using HttpResponseMessage response = await Client.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead);

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                if (response.Content.Headers.ContentLength is long length && length > MaxBytes)
                {
                    return null;
                }

                bytes = await response.Content.ReadAsByteArrayAsync();

                if (bytes.Length > MaxBytes)
                {
                    return null;
                }

                Directory.CreateDirectory(Folder);
                await File.WriteAllBytesAsync(path, bytes);
            }

            if (bytes.Length == 0)
            {
                return null;
            }

            var bitmap = new BitmapImage { DecodePixelWidth = 128 };

            using var stream = new MemoryStream(bytes);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());

            Cache[url] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string CachePath(string url)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(Folder, hash + ".img");
    }
}
