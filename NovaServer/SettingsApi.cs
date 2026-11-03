using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaServer;

/// <summary>
/// 服务器端设置接口：把 server.json 里的配置读成给 NovaServer 界面用的 JSON，
/// 也负责把界面提交的改动写回 server.json。
///
/// 原则：
///   - 读写都要 X-Api-Key（和别的接口一致）；
///   - token / 密钥只回掩码，界面上留空表示「不改」；
///   - 能热生效的（发布配置、密钥校验开关）立刻生效，端口 / 数据源这类要重启的如实告诉你。
/// </summary>
internal static class SettingsApi
{
    /// <summary>界面提交的补丁：null 表示这一项没提交（区别于「提交了空值」）。</summary>
    private sealed class Patch
    {
        public int? Port { get; set; }

        public string? ApiKey { get; set; }

        public bool? RequireKeyForRead { get; set; }

        public string? DataBaseUrl { get; set; }

        public bool? RefreshDataOnStart { get; set; }

        public bool? AutoPublish { get; set; }

        public string? PublishRepo { get; set; }

        public string? PublishTag { get; set; }

        public string? PublishToken { get; set; }

        public bool? PublishRegFiles { get; set; }

        public string? PublishApiBase { get; set; }
    }

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static void Map(
        WebApplication app,
        ServerConfig config,
        string configPath,
        CatalogStore store,
        GitHubPublisher publisher,
        string dataFilePath,
        Func<HttpContext, bool> keyOk,
        Func<IResult> denied)
    {
        app.MapGet("/api/settings", async (HttpContext ctx) =>
        {
            if (!keyOk(ctx))
            {
                return denied();
            }

            CatalogFile data = await store.ReadAsync();
            string dataDir = Path.GetDirectoryName(Path.GetFullPath(dataFilePath)) ?? AppContext.BaseDirectory;

            return Results.Ok(new
            {
                ok = true,
                server = new
                {
                    port = config.Port,
                    apiKeyMasked = GitHubPublisher.Mask(config.ApiKey),
                    requireKeyForRead = config.RequireKeyForRead,
                    dataFile = config.DataFile,
                    resolvedDataFile = Path.GetFullPath(dataFilePath),
                    dataDir,
                    dataBaseUrl = config.DataBaseUrl,
                    refreshDataOnStart = config.RefreshDataOnStart,
                },
                publish = new
                {
                    autoPublish = config.AutoPublish,
                    enabled = publisher.Enabled,
                    hasToken = publisher.HasToken,
                    tokenMasked = publisher.TokenPreview,
                    repo = publisher.RepoSlug,
                    tag = publisher.TagName,
                    regFiles = config.PublishRegFiles,
                    apiBase = config.PublishApiBase,
                    lastPublishedAt = publisher.LastPublishedAt,
                    lastUploaded = publisher.LastUploaded,
                    lastError = publisher.LastError,
                },
                catalog = new
                {
                    version = data.Version,
                    updatedAt = data.UpdatedAt,
                    count = data.Items.Count,
                },
                files = ListFiles(dataDir),
                configPath,
                now = DateTimeOffset.Now,
            });
        });

        app.MapPost("/api/settings", async (HttpContext ctx) =>
        {
            if (!keyOk(ctx))
            {
                return denied();
            }

            string body;

            using (var reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            Patch? patch;

            try
            {
                patch = JsonSerializer.Deserialize<Patch>(body, WriteOptions);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = "请求内容不是合法 JSON：" + ex.Message });
            }

            if (patch is null)
            {
                return Results.BadRequest(new { error = "请求内容为空" });
            }

            var errors = new List<string>();
            var restart = new List<string>();
            var applied = new List<string>();
            bool publishChanged = false;
            string? newKey = null;

            // ── 端口 ──────────────────────────────────────────────
            if (patch.Port is int port)
            {
                if (port is < 1 or > 65535)
                {
                    errors.Add("端口要在 1–65535 之间");
                }
                else if (port != config.Port)
                {
                    config.Port = port;
                    restart.Add($"端口（重启后监听 {port}）");
                }
            }

            // ── 密钥 ──────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(patch.ApiKey))
            {
                string key = patch.ApiKey.Trim();

                if (key.Length < 8)
                {
                    errors.Add("API Key 至少 8 个字符");
                }
                else if (!key.Equals(config.ApiKey, StringComparison.Ordinal))
                {
                    config.ApiKey = key;
                    newKey = key;
                    applied.Add("API Key");
                }
            }

            if (patch.RequireKeyForRead is bool requireKey)
            {
                config.RequireKeyForRead = requireKey;
                applied.Add("读接口密钥校验");
            }

            // ── 数据源（要重启） ───────────────────────────────────
            if (patch.DataBaseUrl is not null)
            {
                string url = patch.DataBaseUrl.Trim();

                if (url.Length > 0 && !uri_ok(url))
                {
                    errors.Add("数据源地址要以 http:// 或 https:// 开头");
                }
                else if (!url.Equals(config.DataBaseUrl, StringComparison.Ordinal))
                {
                    config.DataBaseUrl = url;
                    restart.Add("数据源地址");
                }
            }

            if (patch.RefreshDataOnStart is bool refresh)
            {
                config.RefreshDataOnStart = refresh;
                applied.Add("启动时强制刷新数据");
            }

            // ── 发布到 GitHub（热生效） ────────────────────────────
            if (patch.AutoPublish is bool autoPublish && autoPublish != config.AutoPublish)
            {
                config.AutoPublish = autoPublish;
                publishChanged = true;
                applied.Add(autoPublish ? "开启自动发布" : "关闭自动发布");
            }

            if (patch.PublishRepo is not null)
            {
                string repo = patch.PublishRepo.Trim().Trim('/');

                if (repo.Length > 0 && !RepoOk(repo))
                {
                    errors.Add("仓库要写成 owner/repo，比如 Artk017/artk-data");
                }
                else if (!repo.Equals(config.PublishRepo, StringComparison.Ordinal))
                {
                    config.PublishRepo = repo;
                    publishChanged = true;
                    applied.Add("发布仓库");
                }
            }

            if (patch.PublishTag is not null)
            {
                string tag = patch.PublishTag.Trim();

                if (tag.Contains(' ') || tag.Contains('/'))
                {
                    errors.Add("tag 里不能有空格或斜杠");
                }
                else if (!tag.Equals(config.PublishTag, StringComparison.Ordinal))
                {
                    config.PublishTag = tag;
                    publishChanged = true;
                    applied.Add("发布 tag");
                }
            }

            if (!string.IsNullOrWhiteSpace(patch.PublishToken))
            {
                string token = patch.PublishToken.Trim();

                if (token.Length < 20 || token.Contains(' '))
                {
                    errors.Add("token 看着不像对的（长度不足或有空格）");
                }
                else if (!token.Equals(config.PublishToken, StringComparison.Ordinal))
                {
                    config.PublishToken = token;
                    publishChanged = true;
                    applied.Add("GitHub token");
                }
            }

            if (patch.PublishRegFiles is bool regFiles && regFiles != config.PublishRegFiles)
            {
                config.PublishRegFiles = regFiles;
                publishChanged = true;
                applied.Add(regFiles ? "连 .reg 一起发" : "只发 catalog.json");
            }

            if (patch.PublishApiBase is not null)
            {
                string api = patch.PublishApiBase.Trim().TrimEnd('/');

                if (api.Length > 0 && !uri_ok(api))
                {
                    errors.Add("GitHub API 地址要以 http:// 或 https:// 开头");
                }
                else if (!api.Equals(config.PublishApiBase, StringComparison.Ordinal))
                {
                    config.PublishApiBase = api;
                    publishChanged = true;
                    applied.Add("GitHub API 地址");
                }
            }

            if (errors.Count > 0)
            {
                return Results.BadRequest(new { errors });
            }

            try
            {
                File.WriteAllText(configPath, JsonSerializer.Serialize(config, WriteOptions) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[设置] 写 server.json 失败：{ex.Message}");
                ServerLog.Error($"保存设置失败：{ex.Message}");
                return Results.Json(new { error = "写 server.json 失败：" + ex.Message }, statusCode: 500);
            }

            if (publishChanged)
            {
                publisher.Reload();
            }

            string summary = applied.Count > 0 ? string.Join("、", applied) : "（没有热生效的改动）";
            if (restart.Count > 0)
            {
                summary += $"；需重启：{string.Join("、", restart)}";
            }

            Console.WriteLine($"[设置] 已保存：{summary}");
            ServerLog.Success($"设置已保存：{summary}");

            return Results.Ok(new
            {
                ok = true,
                applied,
                restartRequired = restart,
                apiKey = newKey ?? string.Empty,
            });
        });
    }

    private static bool uri_ok(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool RepoOk(string value)
    {
        string[] parts = value.Split('/');
        return parts.Length == 2 &&
               parts[0].Length > 0 && parts[1].Length > 0 &&
               !value.Contains(' ', StringComparison.Ordinal);
    }

    private static List<object> ListFiles(string dir)
    {
        var list = new List<object>();

        try
        {
            if (!Directory.Exists(dir))
            {
                return list;
            }

            // 连子目录一起列（词汇放在 data\vocab\pinyin-vocab.zip），名字用相对路径，界面上直接看得出位置
            foreach (string path in Directory
                         .GetFiles(dir, "*", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var file = new FileInfo(path);
                string name = Path.GetRelativePath(dir, path).Replace('\\', '/');

                if (file.Name.StartsWith('.') ||
                    file.Name.EndsWith(".upload", StringComparison.OrdinalIgnoreCase) ||
                    file.Name.EndsWith(".download", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                list.Add(new { name, size = file.Length, updatedAt = file.LastWriteTime });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[设置] 列目录失败：{ex.Message}");
        }

        return list;
    }
}
