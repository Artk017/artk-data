using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ArtkTools.Services;

/// <summary>
/// 软件页用到的文件操作：下载安装包、打开文件夹、运行安装包、开浏览器。
/// 下载一律落到用户的「下载」文件夹，先写 <c>.part</c> 再改名，取消或失败不会留下半个 exe。
/// </summary>
public static class AppDownloads
{
    private static readonly HttpClient Client = CreateClient();

    /// <summary>用户的下载文件夹（跟着「下载」库走，被改到 D 盘也能找对）。</summary>
    public static string Folder
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", writable: false);

                if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string raw && raw.Length > 0)
                {
                    string expanded = Environment.ExpandEnvironmentVariables(raw);
                    if (Directory.Exists(expanded))
                    {
                        return expanded;
                    }
                }
            }
            catch (Exception)
            {
                // 读不到就退回默认位置
            }

            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    /// <summary>下载到「下载」文件夹，返回最终文件路径。referer 用于防盗链的 CDN（NVIDIA 之类）。</summary>
    public static async Task<string> DownloadAsync(
        string url,
        string? referer,
        string? preferredName,
        IProgress<(long Received, long? Total)> progress,
        CancellationToken token)
    {
        Directory.CreateDirectory(Folder);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(referer))
        {
            request.Headers.Referrer = new Uri(referer);
        }

        using HttpResponseMessage response = await Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        string name = ResolveFileName(response, preferredName);
        string finalPath = Path.Combine(Folder, name);
        string tempPath = finalPath + ".part";
        bool completed = false;

        try
        {
            await using (Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                long lastReport = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;

                    long now = Environment.TickCount64;
                    if (now - lastReport >= 120)
                    {
                        progress.Report((received, total));
                        lastReport = now;
                    }
                }

                progress.Report((received, total));
            }

            File.Move(tempPath, finalPath, overwrite: true);
            completed = true;
            return finalPath;
        }
        finally
        {
            // 取消 / 断网 / 磁盘满都不要留下半个安装包
            if (!completed)
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception)
                {
                    // 删不掉就算了，下次下载会覆盖
                }
            }
        }
    }

    /// <summary>优先用服务器给的文件名，其次地址里的文件名，最后用我们自己的名字。</summary>
    private static string ResolveFileName(HttpResponseMessage response, string? preferredName)
    {
        string? candidate = null;

        ContentDispositionHeaderValue? disposition = response.Content.Headers.ContentDisposition;
        if (disposition is not null)
        {
            candidate = disposition.FileNameStar ?? disposition.FileName;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            Uri? uri = response.RequestMessage?.RequestUri;
            if (uri is not null)
            {
                candidate = Path.GetFileName(uri.LocalPath);
            }
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = preferredName ?? "setup.exe";
        }

        candidate = candidate.Trim().Trim('"');

        foreach (char bad in Path.GetInvalidFileNameChars())
        {
            candidate = candidate.Replace(bad, '_');
        }

        if (!candidate.Contains('.', StringComparison.Ordinal))
        {
            candidate += ".exe";
        }

        return candidate;
    }

    /// <summary>用默认程序打开网址（没有直链的软件走这里）。</summary>
    public static bool OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>运行下载好的安装包。</summary>
    public static bool RunFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>打开「下载」文件夹并选中指定文件。</summary>
    public static bool ShowInFolder(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else
            {
                Directory.CreateDirectory(Folder);
                Process.Start(new ProcessStartInfo(Folder) { UseShellExecute = true });
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 探测远程安装包大小（字节）。先探 HEAD，服务器不支持 HEAD 再看 GET 响应头里的 Content-Length。
    /// 取不到就返回 null —— 界面据此决定「不显示大小」，绝不瞎编。
    /// </summary>
    public static async Task<long?> TryGetRemoteFileSizeAsync(string url, string? referer, CancellationToken token = default)
    {
        foreach (HttpMethod method in new[] { HttpMethod.Head, HttpMethod.Get })
        {
            try
            {
                long? size = await TrySizeAsync(method, url, referer, token).ConfigureAwait(false);
                if (size is long bytes && bytes > 0)
                {
                    return bytes;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 这个方法探测失败就换下一个
            }
        }

        return null;
    }

    private static async Task<long?> TrySizeAsync(HttpMethod method, string url, string? referer, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(referer))
        {
            request.Headers.Referrer = new Uri(referer);
        }

        using HttpResponseMessage response = await Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return response.Content.Headers.ContentLength;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Nova/1.0");

        return client;
    }
}
