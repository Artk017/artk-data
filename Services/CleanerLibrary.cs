using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ArtkTools.Services;

/// <summary>
/// 把 LangSecRef 映射成界面上的中文分类。
/// 已知代号优先；未知分类统一归到“其他应用”，避免界面出现英文分组名。
/// </summary>
public static class CleanerCategoryResolver
{
    /// <summary>数据库里没写分类、或分类代号不认识时的归处。</summary>
    public static readonly CleanerCategory OtherApps = new("其他应用", 1000);
    private static readonly IReadOnlyDictionary<int, CleanerCategory> Map =
        new Dictionary<int, CleanerCategory>
        {
            [3006] = new("Microsoft Edge", 5),
            [3021] = new("应用程序", 10),
            [3022] = new("互联网应用", 20),
            [3023] = new("多媒体", 30),
            [3024] = new("实用工具", 40),
            [3025] = new("Windows 系统", 50),
            [3026] = new("Firefox 浏览器", 60),
            [3027] = new("Opera 浏览器", 70),
            [3028] = new("Safari 浏览器", 80),
            [3029] = new("Google Chrome", 90),
            [3030] = new("Thunderbird 邮件", 100),
            [3031] = new("Microsoft Store", 110),
            [3033] = new("Vivaldi 浏览器", 130),
            [3034] = new("Brave 浏览器", 140),
            [3035] = new("Opera GX", 150),
            [3036] = new("Spotify", 160),
            [3037] = new("Avast Secure Browser", 170),
            [3038] = new("AVG Secure Browser", 180),
            [3039] = new("Arc 浏览器", 190),
            [3040] = new("iTunes", 200),
            [3042] = new("WhatsApp", 210),
            [3043] = new("Norton Private Browser", 220),
            [3044] = new("Avira Secure Browser", 230),
            // 自制补充规则（Assets\CleanerExtra.ini）里的国产软件分类
            [4001] = new("腾讯应用", 12),
            [4002] = new("网易应用", 24),
            [4003] = new("国产影音", 32),
            [4004] = new("国产游戏", 36),
            [4005] = new("安全与工具", 46),
        };

    public static CleanerCategory Resolve(CleanerEntry entry)
    {
        if (entry.LangSecRef is int code && Map.TryGetValue(code, out var category))
        {
            return category;
        }

        return OtherApps;
    }
}

/// <summary>加载内置的 Winapp2.ini 与国产软件补充规则，只保留当前机器上确实安装了的应用。</summary>
public static class CleanerLibrary
{
    /// <summary>规则数据库路径，随程序输出到 Assets 目录。</summary>
    public static string DatabasePath => Path.Combine(AppContext.BaseDirectory, "Assets", "Winapp2.ini");

    /// <summary>国产软件补充规则；Winapp2 以国外软件为主，这里补齐常用的国内应用。</summary>
    public static string ExtraDatabasePath => Path.Combine(AppContext.BaseDirectory, "Assets", "CleanerExtra.ini");

    public static List<string> DatabasePaths => new[] { DatabasePath, ExtraDatabasePath }
        .Where(File.Exists)
        .ToList();

    public static async Task<List<CleanerEntry>> LoadInstalledAsync()
    {
        var paths = DatabasePaths;
        if (paths.Count == 0)
        {
            throw new FileNotFoundException("找不到清理规则数据库。", DatabasePath);
        }

        var parser = new CleanerParser();
        var parsed = new List<CleanerEntry>();

        // 补充规则放在最后加载；同名项目以先加载的 Winapp2 为准
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            foreach (var entry in await parser.ParseFileAsync(path))
            {
                if (seenNames.Add(entry.Name))
                {
                    parsed.Add(entry);
                }
            }
        }

        // 识别安装状态要读注册表和文件系统，放到后台线程
        return await Task.Run(() =>
        {
            var detector = new CleanerDetector();
            var result = new List<CleanerEntry>();

            foreach (var entry in parsed)
            {
                entry.Category = CleanerCategoryResolver.Resolve(entry);

                if (detector.IsInstalled(entry))
                {
                    result.Add(entry);
                }
            }

            return result;
        });
    }
}
