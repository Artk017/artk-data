using System.Net.Http;
using System.Net.Http.Headers;

namespace NovaServer;

/// <summary>
/// 启动时把 data 目录准备齐：缺什么就从数据仓库（GitHub Release 资产）下载什么。
/// 服务器上不再需要手动往 data 里拷 catalog.json / *.reg。
/// </summary>
internal static class DataSync
{
    /// <summary>默认数据源：GitHub Release 资产（tag = artk-tools）。</summary>
    public const string DefaultBaseUrl = "https://github.com/Artk017/artk-data/releases/download/artk-tools/";

    /// <summary>本地文件名 ← 远端资产名（远端用的是英文名，本地保留中文名给旧接口 /data/{文件名} 用）。</summary>
    private static readonly (string Local, string Remote)[] Required =
    {
        ("catalog.json", "catalog.json"),
        ("PotPlayer.64.bit.reg", "PotPlayer.64.bit.reg"),
        ("崩坏：星穹铁道.reg", "Star.Rail.Game.reg"),
    };

    /// <summary>本地文件名 → 数据仓库里的资产名（发布时也用它，免得一个文件在 Release 里出现两份）。</summary>
    public static string RemoteName(string localName)
    {
        foreach ((string local, string remote) in Required)
        {
            if (local.Equals(localName, StringComparison.OrdinalIgnoreCase))
            {
                return remote;
            }
        }

        return localName;
    }

    /// <summary>
    /// 确保数据文件就位：目录不存在就创建；文件缺失（或 refresh = true）就下载。
    /// 单个文件失败不影响其它文件，也不会让服务起不来（catalog.json 缺失时由 CatalogStore 回退到内置清单）。
    /// </summary>
    public static async Task EnsureAsync(string dataFilePath, ServerConfig config, bool refresh, CancellationToken token = default)
    {
        string dataDir = Path.GetDirectoryName(Path.GetFullPath(dataFilePath)) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(dataDir);

        string baseUrl = string.IsNullOrWhiteSpace(config.DataBaseUrl) ? DefaultBaseUrl : config.DataBaseUrl.Trim();
        if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
        {
            baseUrl += "/";
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ArtkTools-Server/1.0 (+https://github.com/Artk017/artk-data)");

        if (!string.IsNullOrWhiteSpace(config.DataToken))
        {
            // 私有仓库 / 触发限流时可以用 server.json 里的 dataToken 带凭证
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.DataToken.Trim());
        }

        Console.WriteLine($"[数据] 目录：{dataDir}");
        Console.WriteLine($"[数据] 数据源：{baseUrl}");

        foreach ((string local, string remote) in Required)
        {
            string path = Path.Combine(dataDir, local);
            bool exists = File.Exists(path) && new FileInfo(path).Length > 0;

            if (exists && !refresh)
            {
                Console.WriteLine($"[数据] 已存在，跳过：{local}");
                continue;
            }

            string url = baseUrl + Uri.EscapeDataString(remote);

            try
            {
                byte[] bytes = await http.GetByteArrayAsync(url, token).ConfigureAwait(false);

                string temp = path + ".download";
                await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
                File.Move(temp, path, true);

                Console.WriteLine($"[数据] 下载完成：{local}（{bytes.Length:N0} 字节）");
                ServerLog.Success($"数据已同步：{local}（{bytes.Length:N0} 字节）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[数据] 下载失败：{local} ← {url}");
                Console.WriteLine($"[数据]   原因：{ex.Message}");
                if (!exists)
                {
                    Console.WriteLine($"[数据]   该文件仍然缺失。");
                }

                ServerLog.Warning(exists
                    ? $"数据刷新失败，继续使用本地文件：{local}（{ex.Message}）"
                    : $"数据下载失败：{local}（{ex.Message}）");
            }
        }
    }
}
