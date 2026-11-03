using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NovaServer;

/// <summary>本机清单服务的 HTTP 客户端：界面只通过它读写设置，和外部跑着的同名服务也能对上。</summary>
internal sealed class ServerClient
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _baseUrl;

    public ServerClient(int port, string apiKey)
    {
        _baseUrl = $"http://127.0.0.1:{port}";
        Key = (apiKey ?? string.Empty).Trim();
    }

    /// <summary>当前使用的 API Key；界面改了密钥后跟着换，后续请求才不会 401。</summary>
    public string Key { get; set; }

    public string BaseUrl => _baseUrl;

    public async Task<ServerSettings> GetSettingsAsync()
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/api/settings", null).ConfigureAwait(false);
        string body = await ReadOkAsync(response, "读取服务器设置").ConfigureAwait(false);

        return JsonSerializer.Deserialize<ServerSettings>(body, ReadOptions)
               ?? throw new InvalidOperationException("服务器返回的设置是空的");
    }

    public async Task<SaveResult> SaveAsync(Dictionary<string, object?> patch)
    {
        string json = JsonSerializer.Serialize(patch, WriteOptions);
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/settings", json).ConfigureAwait(false);
        string body = await ReadOkAsync(response, "保存设置").ConfigureAwait(false);

        return JsonSerializer.Deserialize<SaveResult>(body, ReadOptions) ?? new SaveResult();
    }

    public async Task<string> PublishNowAsync()
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/publish", string.Empty).ConfigureAwait(false);
        string body = await ReadOkAsync(response, "发布清单").ConfigureAwait(false);

        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("message", out JsonElement message)
            ? message.GetString() ?? "已上传"
            : "已上传";
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json)
    {
        var request = new HttpRequestMessage(method, _baseUrl + path);

        if (!string.IsNullOrEmpty(Key))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", Key);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        try
        {
            return await _http.SendAsync(request).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ServerUnreachableException($"连不上 {_baseUrl}（{ex.Message}）");
        }
        catch (TaskCanceledException)
        {
            throw new ServerUnreachableException($"连接 {_baseUrl} 超时");
        }
    }

    private static async Task<string> ReadOkAsync(HttpResponseMessage response, string action)
    {
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ServerUnauthorizedException("API Key 不对（服务器拒绝了请求）");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{action}失败：{ErrorText(body, response.StatusCode)}");
        }

        return body;
    }

    private static string ErrorText(string body, HttpStatusCode status)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("error", out JsonElement error) && error.GetString() is { Length: > 0 } text)
            {
                return text;
            }

            if (doc.RootElement.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (JsonElement item in errors.EnumerateArray())
                {
                    if (item.GetString() is { Length: > 0 } value)
                    {
                        parts.Add(value);
                    }
                }

                if (parts.Count > 0)
                {
                    return string.Join("；", parts);
                }
            }
        }
        catch (JsonException)
        {
            // 不是 JSON 就回状态码
        }

        return $"HTTP {(int)status}";
    }
}

internal sealed class ServerUnreachableException : Exception
{
    public ServerUnreachableException(string message)
        : base(message)
    {
    }
}

internal sealed class ServerUnauthorizedException : Exception
{
    public ServerUnauthorizedException(string message)
        : base(message)
    {
    }
}

/// <summary>GET /api/settings 的返回体（只挑界面要用的字段）。</summary>
internal sealed class ServerSettings
{
    public ServerSection Server { get; set; } = new();

    public PublishSection Publish { get; set; } = new();

    public CatalogSection Catalog { get; set; } = new();

    public List<DataFileItem> Files { get; set; } = new();

    public string ConfigPath { get; set; } = string.Empty;
}

internal sealed class ServerSection
{
    public int Port { get; set; }

    public string ApiKeyMasked { get; set; } = string.Empty;

    public bool RequireKeyForRead { get; set; }

    public string DataFile { get; set; } = string.Empty;

    public string ResolvedDataFile { get; set; } = string.Empty;

    public string DataDir { get; set; } = string.Empty;

    public string DataBaseUrl { get; set; } = string.Empty;

    public bool RefreshDataOnStart { get; set; }
}

internal sealed class PublishSection
{
    public bool AutoPublish { get; set; }

    public bool Enabled { get; set; }

    public bool HasToken { get; set; }

    public string TokenMasked { get; set; } = string.Empty;

    public string Repo { get; set; } = string.Empty;

    public string Tag { get; set; } = string.Empty;

    public bool RegFiles { get; set; }

    public string ApiBase { get; set; } = string.Empty;

    public DateTimeOffset? LastPublishedAt { get; set; }

    public string LastUploaded { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;
}

internal sealed class CatalogSection
{
    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Count { get; set; }
}

internal sealed class SaveResult
{
    public List<string> Applied { get; set; } = new();

    public List<string> RestartRequired { get; set; } = new();

    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>数据文件列表的一行（界面直接绑定）。</summary>
public sealed class DataFileItem
{
    public string Name { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string SizeText => Size switch
    {
        >= 1048576 => $"{Size / 1048576d:0.0} MB",
        >= 1024 => $"{Size / 1024d:0.0} KB",
        _ => $"{Size} B",
    };

    public string UpdatedText => UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss");
}
