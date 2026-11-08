using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArtkTools.Services;

/// <summary>清单条目的传输结构，字段和 NovaServer 的 AppItem 一致（camelCase）。</summary>
public sealed class AppDto
{
    public string? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Vendor { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Category { get; set; } = "工具";

    public string Color { get; set; } = "#6C4CF1";

    public string HomeUrl { get; set; } = string.Empty;

    public string? DirectUrl { get; set; }

    public string? IconUrl { get; set; }

    public string Resolve { get; set; } = "none";

    public string? SizeHint { get; set; }

    public string? Note { get; set; }

    public int Sort { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>服务端清单接口 /api/catalog 的返回体。</summary>
public sealed class CatalogPayload
{
    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Count { get; set; }

    public List<AppDto> Apps { get; set; } = new();
}

/// <summary>服务端返回的错误（校验失败之类），消息已经是人话。</summary>
public sealed class CatalogException : Exception
{
    public CatalogException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 清单服务客户端：从 Windows 10 服务器拉清单、增删改，并把最后一次成功的结果缓存到本地。
/// 没配服务器也能用：本地清单文件本身就是一份完整快照。
/// </summary>
public static class CatalogServer
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>应用数据目录（导出给直链工具的 catalog.json 写在这里）。</summary>
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova");

    /// <summary>清单服务器地址：写死在客户端里（设置页只读展示）。</summary>
    public static string Url => AppSettings.CatalogServerUrl;

    /// <summary>内置 API Key：跟随每个请求以 X-Api-Key 头发给服务器。</summary>
    public static string Key => AppSettings.CatalogApiKey;

    /// <summary>服务器地址变动（比如以后换服务器）时自增，页面据此决定要不要重新拉清单。</summary>
    public static int Revision { get; private set; }

    public static bool Configured => !string.IsNullOrWhiteSpace(Url);

    /// <summary>把用户填的地址收拾成 http://host:port 形式。</summary>
    public static string NormalizeUrl(string raw)
    {
        string url = (raw ?? string.Empty).Trim().TrimEnd('/');

        if (url.Length == 0)
        {
            return string.Empty;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }

        return url;
    }

    /// <summary>测试连接：请求 /api/health，返回是否可达与软件数量。</summary>
    public static async Task<(bool Ok, string Message)> PingAsync(CancellationToken token = default)
    {
        if (!Configured)
        {
            return (false, "还没填服务器地址");
        }

        string url = $"{NormalizeUrl(Url)}/api/health";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyKey(request);

            using HttpResponseMessage response = await Client.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "密钥不对（服务器拒绝了请求）"
                    : $"服务器返回 {(int)response.StatusCode}");
            }

            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            int count = 0;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("count", out JsonElement c) && c.TryGetInt32(out int n))
                {
                    count = n;
                }
            }
            catch (JsonException)
            {
                // 不是标准 JSON 也算连上了
            }

            return (true, $"连接成功 · 服务器上 {count} 款软件");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, "连不上：" + ex.Message);
        }
    }

    /// <summary>拉一份启用的清单。服务器不可达 / 清单为空时抛异常，由调用方决定要不要显示。</summary>
    public static async Task<List<AppDto>> FetchAsync(CancellationToken token = default)
    {
        if (!Configured)
        {
            throw new CatalogException("没有配置服务器地址");
        }

        string url = $"{NormalizeUrl(Url)}/api/catalog";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyKey(request);

        using HttpResponseMessage response = await Client.SendAsync(request, token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new CatalogException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "密钥不对（服务器拒绝了请求）"
                : $"服务器返回 {(int)response.StatusCode}");
        }

        CatalogPayload? payload = await response.Content
            .ReadFromJsonAsync<CatalogPayload>(Json, token).ConfigureAwait(false);

        List<AppDto> apps = payload?.Apps ?? new List<AppDto>();

        if (apps.Count == 0)
        {
            throw new CatalogException("服务器上的清单是空的");
        }

        return apps;
    }

    /// <summary>新增（Id 为空）或修改（Id 有值）。返回服务器落库后的条目。</summary>
    public static async Task<AppDto> SaveAsync(AppDto app, CancellationToken token = default)
    {
        string baseUrl = NormalizeUrl(Url);
        bool isNew = string.IsNullOrWhiteSpace(app.Id);

        using var request = new HttpRequestMessage(
            isNew ? HttpMethod.Post : HttpMethod.Put,
            isNew ? $"{baseUrl}/api/apps" : $"{baseUrl}/api/apps/{app.Id}");

        request.Content = new StringContent(JsonSerializer.Serialize(app, Json), Encoding.UTF8, "application/json");
        ApplyKey(request);

        using HttpResponseMessage response = await Client.SendAsync(request, token).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new CatalogException(ExtractError(body, (int)response.StatusCode));
        }

        return string.IsNullOrWhiteSpace(body)
            ? app
            : JsonSerializer.Deserialize<AppDto>(body, Json) ?? app;
    }

    public static async Task DeleteAsync(string id, CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{NormalizeUrl(Url)}/api/apps/{id}");
        ApplyKey(request);

        using HttpResponseMessage response = await Client.SendAsync(request, token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            throw new CatalogException(ExtractError(body, (int)response.StatusCode));
        }
    }

    /// <summary>把服务器清单导给「直链工具」用（工具只认名字和页面地址）。</summary>
    public static void ExportToolCatalog(IReadOnlyList<AppEntry> entries)
    {
        try
        {
            var list = new List<object>();

            foreach (AppEntry entry in entries)
            {
                list.Add(new { Name = entry.Name, Page = entry.HomeUrl, HasLink = entry.HasDirectLink });
            }

            Directory.CreateDirectory(Folder);
            File.WriteAllText(
                Path.Combine(Folder, "catalog.json"),
                JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // 导出失败不影响主流程
        }
    }

    private static void ApplyKey(HttpRequestMessage request)
    {
        string key = Key?.Trim() ?? string.Empty;

        if (key.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        }
    }

    private static string ExtractError(string body, int status)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);

                if (doc.RootElement.TryGetProperty("errors", out JsonElement errors) &&
                    errors.ValueKind == JsonValueKind.Array)
                {
                    var lines = new List<string>();

                    foreach (JsonElement item in errors.EnumerateArray())
                    {
                        lines.Add(item.GetString() ?? string.Empty);
                    }

                    if (lines.Count > 0)
                    {
                        return string.Join("\n", lines);
                    }
                }

                if (doc.RootElement.TryGetProperty("error", out JsonElement error))
                {
                    return error.GetString() ?? $"HTTP {status}";
                }
            }
            catch (JsonException)
            {
                // 不是 JSON 就退回状态码
            }
        }

        return status == 401 ? "API Key 不对（在「服务器」里重新填一次）" : $"HTTP {status}";
    }
}
