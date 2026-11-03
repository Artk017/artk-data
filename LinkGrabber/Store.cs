using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NovaLinkGrabber;

/// <summary>一条手动抓到的直链。</summary>
public sealed class StoredLink
{
    public string Url { get; set; } = string.Empty;

    public string? Referer { get; set; }

    public string CapturedAt { get; set; } = string.Empty;
}

/// <summary>软件页清单里的一项（Nova 启动时会导出，工具用来填下拉框）。</summary>
public sealed class CatalogEntry
{
    public string Name { get; set; } = string.Empty;

    public string Page { get; set; } = string.Empty;

    public bool HasLink { get; set; }
}

/// <summary>
/// 和 Nova 共享的两个文件，都在 %LOCALAPPDATA%\NovaWorkbench 下：
///   catalog.json —— Nova 导出的软件清单（名称 + 官网下载页）
///   links.json   —— 本工具抓到的直链（Nova 的软件页读它）
/// </summary>
internal static class Store
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova");

    public static string LinksPath => Path.Combine(Folder, "links.json");

    public static string CatalogPath => Path.Combine(Folder, "catalog.json");

    public static Dictionary<string, StoredLink> LoadLinks()
    {
        try
        {
            if (File.Exists(LinksPath))
            {
                string json = File.ReadAllText(LinksPath);
                Dictionary<string, StoredLink>? data =
                    JsonSerializer.Deserialize<Dictionary<string, StoredLink>>(json);
                if (data is not null)
                {
                    return data;
                }
            }
        }
        catch (Exception)
        {
            // 文件坏了就当没有，下面会重建
        }

        return new Dictionary<string, StoredLink>(StringComparer.Ordinal);
    }

    public static void StoreLink(string name, string url, string? referer)
    {
        Dictionary<string, StoredLink> data = LoadLinks();
        data[name] = new StoredLink
        {
            Url = url,
            Referer = referer,
            CapturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        };

        Directory.CreateDirectory(Folder);
        File.WriteAllText(LinksPath, JsonSerializer.Serialize(data, Options));
    }

    public static List<CatalogEntry> LoadCatalog()
    {
        try
        {
            if (File.Exists(CatalogPath))
            {
                string json = File.ReadAllText(CatalogPath);
                List<CatalogEntry>? list = JsonSerializer.Deserialize<List<CatalogEntry>>(json);
                if (list is { Count: > 0 })
                {
                    return list;
                }
            }
        }
        catch (Exception)
        {
            // 读不到就用内置清单
        }

        return BuiltinCatalog();
    }

    /// <summary>Nova 还没导出过清单时的兜底，保证工具单独也能用。</summary>
    private static List<CatalogEntry> BuiltinCatalog() => new()
    {
        new() { Name = "KOOK", Page = "https://www.kookapp.cn/download" },
        new() { Name = "QQ", Page = "https://im.qq.com/pcqq" },
        new() { Name = "微信", Page = "https://pc.weixin.qq.com/" },
        new() { Name = "Logitech G HUB", Page = "https://www.logitechg.com/zh-cn/innovation/g-hub.html" },
        new() { Name = "NVIDIA App", Page = "https://www.nvidia.cn/software/nvidia-app/" },
        new() { Name = "Visual Studio Code", Page = "https://code.visualstudio.com/" },
        new() { Name = "PotPlayer 64 位", Page = "https://potplayer.tv/" },
        new() { Name = "Bandizip", Page = "https://cn.bandisoft.com/bandizip/" },
        new() { Name = "Steam", Page = "https://store.steampowered.com/about/" },
        new() { Name = "QQ音乐", Page = "https://y.qq.com/download/index.html" },
        new() { Name = "奇游电竞加速器", Page = "https://www.qiyou.cn/" },
    };
}
