using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIDemo.Services;

/// <summary>自动识别出来的软件信息，字段和添加 / 编辑对话框一一对应。</summary>
public sealed class AutoFilled
{
    public string Name { get; init; } = string.Empty;
    public string Vendor { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Category { get; init; } = "工具";
    public string Color { get; init; } = "#6C4CF1";
    public string HomeUrl { get; init; } = string.Empty;
    public string? DirectUrl { get; init; }
    public ResolveKind Resolve { get; init; }
}

/// <summary>
/// 给「添加软件」用的自动识别：给一个官网首页或安装包直链，尽量把
/// 名称 / 厂商 / 简介 / 分类 / 配色 / 官网 / 直链 / 解析方式都猜出来。
/// 都是启发式，识别完仍让用户确认后再保存。
/// </summary>
public static class AppAutoFill
{
    private static readonly HttpClient Http = CreateClient();

    private static readonly string[] Palette =
    {
        "#6C4CF1", "#5B6CF9", "#12B7F5", "#07C160", "#31C27C",
        "#F57C00", "#EF5350", "#26A69A", "#8E24AA", "#1B75BB",
        "#4B9FD5", "#76B900", "#00A8E0", "#D81B60", "#546E7A",
    };

    /// <summary>文件名里常见的「安装器」噪音词，命名时去掉。</summary>
    private static readonly string[] NoiseWords =
    {
        "setup", "installer", "install", "installshield", "web", "online",
        "win64", "win32", "x64", "x86", "64bit", "32bit", "64", "32", "full", "latest",
    };

    /// <summary>已知中文名映射（文件名 / 英文名 → 中文显示名）。</summary>
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wechat"] = "微信",
        ["weixin"] = "微信",
        ["qqmusic"] = "QQ音乐",
        ["qq"] = "QQ",
        ["kook"] = "KOOK",
        ["neteasecloudmusic"] = "网易云音乐",
        ["cloudmusic"] = "网易云音乐",
        ["steam"] = "Steam",
        ["bandizip"] = "Bandizip",
        ["potplayer"] = "PotPlayer",
        ["potplayersetup"] = "PotPlayer",
        ["vscode"] = "Visual Studio Code",
        ["code"] = "Visual Studio Code",
    };

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        return client;
    }

    /// <summary>识别一个链接。识别不到时也会尽量返回能填的字段，不抛异常。</summary>
    public static async Task<AutoFilled> DetectAsync(string rawUrl, CancellationToken token = default)
    {
        string url = (rawUrl ?? string.Empty).Trim();
        if (url.Length == 0)
        {
            return new AutoFilled();
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        // 直接贴的是安装包：官网取站点根，名称从文件名猜
        if (IsInstallerUrl(url))
        {
            Uri uri = new(url);
            string file = System.IO.Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            string installerName = NameFromFileName(file);
            return new AutoFilled
            {
                Name = installerName,
                Vendor = uri.Host,
                HomeUrl = $"{uri.Scheme}://{uri.Host}",
                DirectUrl = url,
                Category = GuessCategory(installerName + " " + file),
                Color = ColorFor(installerName),
            };
        }

        // 已知官网：直接用内置清单里校对过的信息，比抓页面更准也更快
        string pageHost = SafeUri(url)?.Host ?? string.Empty;
        AppEntry? preset = FindBuiltinByHost(pageHost);
        if (preset is not null)
        {
            return new AutoFilled
            {
                Name = preset.Name,
                Vendor = preset.Vendor,
                Summary = preset.Summary,
                Category = preset.Category,
                Color = preset.Color,
                HomeUrl = url,
                DirectUrl = preset.DirectUrl,
                Resolve = preset.Resolve,
            };
        }

        string html;
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            html = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string host = SafeUri(url)?.Host ?? url;
            return new AutoFilled
            {
                Name = host,
                Vendor = host,
                HomeUrl = url,
                Category = "工具",
                Color = ColorFor(host),
            };
        }

        string title = CleanName(FirstMatch(html, @"<title[^>]*>(.*?)</title>"));
        string ogTitle = MetaContent(html, "og:title");
        string ogSite = MetaContent(html, "og:site_name");
        string desc = MetaContent(html, "og:description");
        if (string.IsNullOrWhiteSpace(desc))
        {
            desc = MetaContent(html, "description");
        }

        string theme = MetaContent(html, "theme-color");

        string name = CleanName(!string.IsNullOrWhiteSpace(ogTitle) ? ogTitle : title);
        if (name.Length == 0)
        {
            name = SafeUri(url)?.Host ?? url;
        }

        string vendor = !string.IsNullOrWhiteSpace(ogSite) ? ogSite.Trim() : SafeUri(url)?.Host ?? string.Empty;
        string summary = Truncate(StripTags(desc), 48);

        string? direct = FindInstallerLink(html, url);
        ResolveKind resolve = GuessResolve(url, direct);
        string color = IsHexColor(theme) ? NormalizeHex(theme!) : ColorFor(name);

        return new AutoFilled
        {
            Name = name,
            Vendor = vendor,
            Summary = summary,
            Category = GuessCategory(name + " " + summary + " " + vendor),
            Color = color,
            HomeUrl = url,
            DirectUrl = direct,
            Resolve = resolve,
        };
    }

    private static bool IsInstallerUrl(string url)
    {
        string path = url;
        int q = path.IndexOf('?');
        if (q >= 0)
        {
            path = path[..q];
        }

        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri? SafeUri(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri : null;
    }

    private static string FirstMatch(string html, string pattern)
    {
        Match m = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    /// <summary>取某个 meta 的 content；property / name 两种写法都认，属性顺序不限。</summary>
    private static string MetaContent(string html, string key)
    {
        Match tag = Regex.Match(
            html,
            "<meta[^>]*(?:property|name)\\s*=\\s*[\"']" + Regex.Escape(key) + "[\"'][^>]*>",
            RegexOptions.IgnoreCase);

        if (!tag.Success)
        {
            return string.Empty;
        }

        Match content = Regex.Match(tag.Value, "content\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase);
        return content.Success ? WebUtility.HtmlDecode(content.Groups[1].Value).Trim() : string.Empty;
    }

    /// <summary>在页面里找第一个像安装包的链接（.exe / .msi），优先 setup / install / download。</summary>
    private static string? FindInstallerLink(string html, string pageUrl)
    {
        MatchCollection matches = Regex.Matches(
            html,
            "href\\s*=\\s*[\"']([^\"']+\\.(?:exe|msi)(?:\\?[^\"']*)?)[\"']",
            RegexOptions.IgnoreCase);

        if (matches.Count == 0)
        {
            return null;
        }

        Uri? baseUri = SafeUri(pageUrl);
        var candidates = new List<string>();

        foreach (Match m in matches)
        {
            string href = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            string? absolute = null;

            if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                absolute = href;
            }
            else if (baseUri is not null && Uri.TryCreate(baseUri, href, out Uri? combined))
            {
                absolute = combined.ToString();
            }

            if (absolute is not null && !candidates.Contains(absolute))
            {
                candidates.Add(absolute);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        foreach (string candidate in candidates)
        {
            if (candidate.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains("install", StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains("download", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    private static ResolveKind GuessResolve(string url, string? direct)
    {
        if (direct is not null)
        {
            return ResolveKind.None;
        }

        string host = SafeUri(url)?.Host ?? string.Empty;

        if (host.Contains("nvidia", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveKind.NvidiaPage;
        }

        if (host.Contains("qiyou", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveKind.QiyouPage;
        }

        if (host.Contains("kook", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveKind.KookApi;
        }

        return ResolveKind.None;
    }

    private static string NameFromFileName(string fileName)
    {
        string raw = Regex.Replace(fileName, @"[vV]?\d+(\.\d+)+", " ");
        string key = Regex.Replace(raw, "[^A-Za-z0-9]+", string.Empty); // 只留字母数字

        // 反复剥掉结尾的 Setup / Installer / 64 / Web 之类噪音词
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (string noise in NoiseWords)
            {
                if (key.Length > noise.Length && key.EndsWith(noise, StringComparison.OrdinalIgnoreCase))
                {
                    key = key[..^noise.Length];
                    changed = true;
                    break;
                }
            }
        }

        if (key.Length > 0 && KnownNames.TryGetValue(key, out string? known))
        {
            return known;
        }

        // 驼峰拆成空格，读起来友好一点
        string spaced = Regex.Replace(key, "(?<=[a-z0-9])(?=[A-Z])", " ").Trim();
        return spaced.Length > 0 ? spaced : fileName;
    }

    /// <summary>按官网 host 找内置清单条目（去掉 www. 再比），用于直接套用校对过的信息。</summary>
    private static AppEntry? FindBuiltinByHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        string h = TrimWww(host);

        foreach (AppEntry entry in AppCatalog.Builtins)
        {
            Uri? home = SafeUri(entry.HomeUrl);
            if (home is null)
            {
                continue;
            }

            string eh = TrimWww(home.Host);

            if (string.Equals(h, eh, StringComparison.OrdinalIgnoreCase) ||
                h.EndsWith("." + eh, StringComparison.OrdinalIgnoreCase) ||
                eh.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static string TrimWww(string host)
    {
        host = host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static string CleanName(string value)
    {
        string text = WebUtility.HtmlDecode(value ?? string.Empty);
        text = StripTags(text).Trim();

        // 标题常见的“官网 | 官方下载”等尾巴，取第一段
        foreach (string sep in new[] { " | ", "｜", " - ", " – ", "—", "_", "：", ": ", " — " })
        {
            int idx = text.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
            {
                text = text[..idx];
                break;
            }
        }

        text = Regex.Replace(text, @"(官方)?(网站|官网|首页|下载|官方网站|官网首页)+$", string.Empty).Trim();

        if (KnownNames.TryGetValue(text.Replace(" ", string.Empty), out string? known))
        {
            return known;
        }

        return text;
    }

    private static string StripTags(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return Regex.Replace(value, "<[^>]+>", string.Empty);
    }

    private static string Truncate(string value, int max)
    {
        value = Regex.Replace(value, "\\s+", " ").Trim();
        return value.Length <= max ? value : value[..max].TrimEnd() + "…";
    }

    private static bool IsHexColor(string? value)
    {
        return value is not null && Regex.IsMatch(value.Trim(), "^#?[0-9a-fA-F]{6}$");
    }

    private static string NormalizeHex(string value)
    {
        value = value.Trim().TrimStart('#');
        return "#" + value.ToUpperInvariant();
    }

    private static string ColorFor(string seed)
    {
        int hash = 0;
        foreach (char c in seed ?? string.Empty)
        {
            hash = unchecked(hash * 31 + c);
        }

        int index = Math.Abs(hash) % Palette.Length;
        return Palette[index];
    }

    private static string GuessCategory(string text)
    {
        string t = text.ToLowerInvariant();

        if (Contains(t, "游戏", "game", "电竞", "竞技", "加速", "steam", "开黑"))
        {
            return "游戏";
        }

        if (Contains(t, "聊天", "通讯", "消息", "社交", "语音", "kook", "微信", "wechat", "qq", "im"))
        {
            return "通讯";
        }

        return "工具";
    }

    private static bool Contains(string text, params string[] words)
    {
        foreach (string w in words)
        {
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
