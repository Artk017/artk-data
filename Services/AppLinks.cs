using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WinUIDemo.Services;

/// <summary>一条从「Nova 直链工具」里抓回来的下载地址。</summary>
public sealed class StoredLink
{
    public string Url { get; set; } = string.Empty;

    public string? Referer { get; set; }

    public string CapturedAt { get; set; } = string.Empty;
}

/// <summary>导出给直链工具用的清单项。</summary>
public sealed class CatalogExport
{
    public string Name { get; set; } = string.Empty;

    public string Page { get; set; } = string.Empty;

    public bool HasLink { get; set; }
}

/// <summary>
/// 和「Nova 直链工具」(NovaLinkGrabber.exe) 共享的目录：
/// %LOCALAPPDATA%\NovaWorkbench\links.json —— 工具写入，软件页读取；
/// %LOCALAPPDATA%\NovaWorkbench\catalog.json —— 软件页导出，工具读取。
/// 有些厂商（QQ、QQ音乐这种）下载地址是页面里临时签发的，抓一次存下来就能直接下。
/// </summary>
public static class AppLinks
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static Dictionary<string, StoredLink>? _cache;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova");

    public static string FilePath => Path.Combine(Folder, "links.json");

    public static string CatalogPath => Path.Combine(Folder, "catalog.json");

    public static Dictionary<string, StoredLink> All
    {
        get
        {
            if (_cache is not null)
            {
                return _cache;
            }

            try
            {
                if (File.Exists(FilePath))
                {
                    Dictionary<string, StoredLink>? data = JsonSerializer.Deserialize<Dictionary<string, StoredLink>>(
                        File.ReadAllText(FilePath));
                    if (data is not null)
                    {
                        _cache = data;
                        return _cache;
                    }
                }
            }
            catch (Exception)
            {
                // 文件坏了就当没有，不影响使用
            }

            _cache = new Dictionary<string, StoredLink>(StringComparer.Ordinal);
            return _cache;
        }
    }

    /// <summary>工具改完文件后调一下，重新读盘。</summary>
    public static void Reload() => _cache = null;

    public static bool TryGet(string name, out StoredLink link) => All.TryGetValue(name, out link!);

    /// <summary>把当前清单写给直链工具（工具用不到就忽略）。</summary>
    public static void ExportCatalog()
    {
        try
        {
            var list = new List<CatalogExport>(AppCatalog.All.Count);
            foreach (AppEntry entry in AppCatalog.All)
            {
                list.Add(new CatalogExport
                {
                    Name = entry.Name,
                    Page = entry.HomeUrl,
                    HasLink = entry.HasDirectLink || All.ContainsKey(entry.Name),
                });
            }

            Directory.CreateDirectory(Folder);
            File.WriteAllText(CatalogPath, JsonSerializer.Serialize(list, Options));
        }
        catch (Exception)
        {
            // 导出失败不影响主流程
        }
    }
}
