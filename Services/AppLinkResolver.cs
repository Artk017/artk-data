using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIDemo.Services;

/// <summary>怎么拿直链。</summary>
public enum ResolveKind
{
    /// <summary>没有自动办法（官网需要真实点击才会签发带签名的地址），可以在「添加软件」里打开网页点一次下载抓下来。</summary>
    None = 0,

    /// <summary>KOOK：官网有一套公开的版本接口，直接返回带签名的安装包地址。</summary>
    KookApi,

    /// <summary>NVIDIA：下载页 HTML 里就写着当前版本的安装包地址，抓出来即可（下载要带 Referer）。</summary>
    NvidiaPage,

    /// <summary>奇游：官网首页里直接放着当前安装包地址（文件名带时间戳，每版都换）。</summary>
    QiyouPage,
}

/// <summary>把「官网下载页」变成可下载的安装包地址。</summary>
public static class AppLinkResolver
{
    private static readonly HttpClient Client = CreateClient();

    private static readonly Regex NvidiaPattern = new(
        @"https://cn\.download\.nvidia\.com/nvapp/client/[0-9.]+/NVIDIA_app_v[0-9.]+\.exe",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QiyouPattern = new(
        @"https://static\.qiyou\.cn/[^""'\s<>]*QiYouInstaller[^""'\s<>]*\.exe",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>解析出安装包地址；解析不出来返回 (null, null)。</summary>
    public static async Task<(string? Url, string? Referer)> ResolveAsync(AppEntry entry, CancellationToken token)
    {
        try
        {
            return entry.Resolve switch
            {
                ResolveKind.KookApi => await ResolveKookAsync(token).ConfigureAwait(false),
                ResolveKind.NvidiaPage => await ScrapeAsync(entry.HomeUrl, NvidiaPattern, token).ConfigureAwait(false),
                ResolveKind.QiyouPage => await ScrapeAsync(entry.HomeUrl, QiyouPattern, token).ConfigureAwait(false),
                _ => (null, null),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static async Task<(string?, string?)> ResolveKookAsync(CancellationToken token)
    {
        const string api = "https://www.kookapp.cn/api/v2/updates/latest-version?platform=windows&source=1";

        using var request = new HttpRequestMessage(HttpMethod.Get, api);
        request.Headers.Referrer = new Uri("https://www.kookapp.cn/download");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

        using HttpResponseMessage response = await Client.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("url", out JsonElement url) &&
            url.GetString() is { Length: > 0 } value)
        {
            return (value, "https://www.kookapp.cn/download");
        }

        return (null, null);
    }

    private static async Task<(string?, string?)> ScrapeAsync(string pageUrl, Regex pattern, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");

        using HttpResponseMessage response = await Client.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string html = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        Match match = pattern.Match(html);

        return match.Success ? (match.Value, pageUrl) : (null, null);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        return client;
    }
}
