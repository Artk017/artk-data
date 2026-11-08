using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArtkTools.Services;

/// <summary>服务器上保存的清理勾选。</summary>
public sealed class CleanerRemoteSettings
{
    public bool Ok { get; init; }

    /// <summary>服务器是否保存过一份设置（没保存过时 Selected 为空）。</summary>
    public bool Exists { get; init; }

    public List<string> Selected { get; init; } = new();

    public DateTimeOffset? UpdatedAt { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 垃圾清理勾选的服务器端存取：走清单服务同一台服务器、同一个 API Key，
/// 接口是 /api/cleaner-settings，服务端落在 data\cleaner-settings.json。
/// </summary>
public static class CleanerSettingsServer
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>读取服务器上保存的勾选。连不上时 Ok = false，页面退回本地设置。</summary>
    public static async Task<CleanerRemoteSettings> FetchAsync(CancellationToken token = default)
    {
        if (!CatalogServer.Configured)
        {
            return new CleanerRemoteSettings { Message = "还没配置服务器地址" };
        }

        string url = $"{CatalogServer.NormalizeUrl(CatalogServer.Url)}/api/cleaner-settings";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyKey(request);

            using HttpResponseMessage response = await Client.SendAsync(request, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                return new CleanerRemoteSettings
                {
                    Message = status == 404 ? "服务端版本较旧（HTTP 404）" : $"服务器返回 {status}",
                };
            }

            using JsonDocument doc = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));

            var root = doc.RootElement;
            bool exists = root.TryGetProperty("exists", out JsonElement e) && e.ValueKind == JsonValueKind.True;
            var selected = new List<string>();

            if (root.TryGetProperty("selected", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        string? name = item.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            selected.Add(name.Trim());
                        }
                    }
                }
            }

            DateTimeOffset? updatedAt = null;
            if (root.TryGetProperty("updatedAt", out JsonElement at) && at.ValueKind == JsonValueKind.String)
            {
                try
                {
                    updatedAt = DateTimeOffset.Parse(at.GetString() ?? string.Empty);
                }
                catch (FormatException)
                {
                    // 时间格式不认识就不显示
                }
            }

            return new CleanerRemoteSettings
            {
                Ok = true,
                Exists = exists,
                Selected = selected,
                UpdatedAt = updatedAt,
            };
        }
        catch (TaskCanceledException)
        {
            return new CleanerRemoteSettings { Message = "服务器响应超时" };
        }
        catch (Exception ex)
        {
            return new CleanerRemoteSettings { Message = "连不上：" + ex.Message };
        }
    }

    /// <summary>把当前勾选的项目名提交到服务器。</summary>
    public static async Task<(bool Ok, string Message)> SaveAsync(
        IEnumerable<string> selected,
        CancellationToken token = default)
    {
        if (!CatalogServer.Configured)
        {
            return (false, "还没配置服务器地址");
        }

        string url = $"{CatalogServer.NormalizeUrl(CatalogServer.Url)}/api/cleaner-settings";
        var names = selected.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { selected = names, source = "ArtkTools" }, Json),
                Encoding.UTF8,
                "application/json");
            ApplyKey(request);

            using HttpResponseMessage response = await Client.SendAsync(request, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false, ExtractError(body, (int)response.StatusCode));
            }

            return (true, $"已保存 {names.Count} 项到服务器");
        }
        catch (TaskCanceledException)
        {
            return (false, "服务器响应超时");
        }
        catch (Exception ex)
        {
            return (false, "连不上：" + ex.Message);
        }
    }

    private static void ApplyKey(HttpRequestMessage request)
    {
        string key = CatalogServer.Key?.Trim() ?? string.Empty;

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

        return status == 401
            ? "密钥不对（服务器拒绝了请求）"
            : status == 404
                ? "服务器上没有清理设置接口（服务端版本较旧）"
                : $"HTTP {status}";
    }
}
