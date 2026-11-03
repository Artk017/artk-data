using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaServer;

/// <summary>
/// 把服务器上的 <c>data/catalog.json</c>（可选再带上 <c>*.reg</c>）自动发布回 GitHub Release 资产，
/// 让数据仓库始终是「最新清单」的备份 / 分发源：别的机器装服务器时会从那里下载，
/// 这边一改就同步上去，不用手动传文件。
///
/// 触发方式：
///   1) 自动：监听 data 目录，改动后等 2.5 秒（合并连续写入）再发；
///   2) 手动：<c>--publish-now</c> 参数、或 <c>POST /api/publish</c>（要 X-Api-Key）。
///
/// 只发变化过的文件：按 SHA-256 比对，状态记在 <c>data/.publish-state.json</c>。
/// 全程不抛异常，失败只打日志（<c>[发布] …</c>），不影响清单服务本身。
/// </summary>
internal sealed class GitHubPublisher
{
    private const string StateFileName = ".publish-state.json";
    private const string CatalogName = "catalog.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _dataDir;
    private readonly ServerConfig _config;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _debounceLock = new();

    private CancellationTokenSource? _pending;
    private FileSystemWatcher? _watcher;

    private DateTimeOffset? _lastPublishedAt;
    private string _lastError = string.Empty;
    private string _lastUploaded = string.Empty;

    public GitHubPublisher(string dataFilePath, ServerConfig config)
    {
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataFilePath)) ?? AppContext.BaseDirectory;
        _config = config;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ArtkTools-Server/1.0 (+https://github.com/Artk017/artk-data)");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        string token = Token();

        if (token.Length > 0)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    // ── 配置解析 ────────────────────────────────────────────────────────

    private string Token()
    {
        string token = (_config.PublishToken ?? string.Empty).Trim();

        if (token.Length == 0)
        {
            // 私有仓库本来就要一个能读的 token，有 contents:write 时同一个就能用
            token = (_config.DataToken ?? string.Empty).Trim();
        }

        return token;
    }

    /// <summary>解析 owner/repo：server.json 里写「owner/repo」，否则从 dataBaseUrl 里抠，最后兜底 Artk017/artk-data。</summary>
    private (string Owner, string Repo) Repo()
    {
        string raw = (_config.PublishRepo ?? string.Empty).Trim().Trim('/');

        if (raw.Length == 0)
        {
            raw = (_config.DataBaseUrl ?? string.Empty).Trim();
        }

        // https://github.com/<owner>/<repo>/releases/download/<tag>/…
        if (raw.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int index = Array.FindIndex(parts, p => p.Equals("github.com", StringComparison.OrdinalIgnoreCase));

            if (index >= 0 && parts.Length >= index + 3)
            {
                return (parts[index + 1], parts[index + 2]);
            }
        }

        if (raw.Contains('/') && !raw.Contains("://", StringComparison.Ordinal))
        {
            string[] parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return (parts[0], parts[1]);
            }
        }

        return ("Artk017", "artk-data");
    }

    private string Tag()
    {
        string tag = (_config.PublishTag ?? string.Empty).Trim();

        if (tag.Length > 0)
        {
            return tag;
        }

        // 从 dataBaseUrl 的 …/releases/download/<tag>/ 里取
        string raw = (_config.DataBaseUrl ?? string.Empty).Trim();
        int index = raw.IndexOf("/releases/download/", StringComparison.OrdinalIgnoreCase);

        if (index >= 0)
        {
            string rest = raw[(index + "/releases/download/".Length)..].Trim('/');
            string[] parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                return parts[0];
            }
        }

        return "artk-tools";
    }

    private string ApiBase()
    {
        string raw = (_config.PublishApiBase ?? string.Empty).Trim().TrimEnd('/');
        return raw.Length > 0 ? raw : "https://api.github.com";
    }

    /// <summary>上传资产走另一个域名（github.com 是 uploads.github.com，企业版是 &lt;host&gt;/api/uploads）。</summary>
    private string UploadBase()
    {
        string api = ApiBase();

        if (api.Equals("https://api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return "https://uploads.github.com";
        }

        return api.Contains("/api/v3", StringComparison.OrdinalIgnoreCase)
            ? api.Replace("/api/v3", "/api/uploads", StringComparison.OrdinalIgnoreCase)
            : api;
    }

    public bool Enabled => _config.AutoPublish && Token().Length > 0;

    /// <summary>是否配了能用的 token（数据来源：publishToken，回退 dataToken）。</summary>
    public bool HasToken => Token().Length > 0;

    /// <summary>给界面看的 token 掩码，别把完整 token 回给浏览器。</summary>
    public string TokenPreview => Mask(Token());

    public string RepoSlug
    {
        get
        {
            (string owner, string repo) = Repo();
            return $"{owner}/{repo}";
        }
    }

    public string TagName => Tag();

    public DateTimeOffset? LastPublishedAt => _lastPublishedAt;

    public string LastUploaded => _lastUploaded;

    public string LastError => _lastError;

    /// <summary>只露头尾，够认出来是哪一把就行。</summary>
    public static string Mask(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string text = value.Trim();
        return text.Length <= 14 ? new string('•', text.Length) : $"{text[..10]}…{text[^4..]}";
    }

    /// <summary>给 /api/publish 和启动横幅用的一行状态。</summary>
    public object Status()
    {
        (string owner, string repo) = Repo();

        return new
        {
            enabled = Enabled,
            repo = $"{owner}/{repo}",
            tag = Tag(),
            lastPublishedAt = _lastPublishedAt,
            lastUploaded = _lastUploaded,
            lastError = _lastError,
        };
    }

    // ── 启动 / 停止 ────────────────────────────────────────────────────

    /// <summary>挂上目录监听。没开自动发布（或没配 token）就只打一行日志。</summary>
    public void Start()
    {
        (string owner, string repo) = Repo();
        string tag = Tag();

        if (!_config.AutoPublish)
        {
            Console.WriteLine("[发布] 自动发布未开启（server.json 里 \"autoPublish\": true 才开）");
            return;
        }

        if (Token().Length == 0)
        {
            Console.WriteLine("[发布] 自动发布已开启，但没配 GitHub token（\"publishToken\"），跳过");
            ServerLog.Warning("自动发布已开启，但没有配置 GitHub token，已跳过");
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(_dataDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };

            _watcher.Changed += (_, e) => Queue(e.Name);
            _watcher.Created += (_, e) => Queue(e.Name);
            _watcher.Renamed += (_, e) => Queue(e.Name);
            _watcher.EnableRaisingEvents = true;

            Console.WriteLine($"[发布] 已监听 {_dataDir}，清单变化后自动发到 {owner}/{repo} @ {tag}");
            ServerLog.Info($"自动发布已开启：{owner}/{repo} @ {tag}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[发布] 监听失败：{ex.Message}");
            ServerLog.Error($"自动发布监听失败：{ex.Message}");
        }
    }

    public void Stop()
    {        try
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }
        catch (Exception)
        {
            // 退出阶段失败无所谓
        }
    }

    /// <summary>
    /// 界面上改了发布配置后热生效：重新挂监听（发起地址、tag、token、开关都会跟着换）。
    /// 不用重启服务；正在发的那一次会等它自己跑完。
    /// </summary>
    public void Reload()
    {
        Stop();

        // 换了 token 要重建 HttpClient 的 Authorization 头
        _http.DefaultRequestHeaders.Authorization = Token().Length > 0
            ? new AuthenticationHeaderValue("Bearer", Token())
            : null;

        Start();
    }

    private void Queue(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return;
        }

        // 事件里的名字可能是子目录里的文件（vocab/pinyin-vocab.zip），按叶子名过滤
        string leaf = Path.GetFileName(fileName.Replace('\\', '/'));

        if (leaf.StartsWith('.') ||
            leaf.EndsWith(".download", StringComparison.OrdinalIgnoreCase) ||
            leaf.EndsWith(".upload", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_config.PublishRegFiles && leaf.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_debounceLock)
        {
            try
            {
                _pending?.Cancel();
            }
            catch (Exception)
            {
                // 已经释放过就算了
            }

            _pending = new CancellationTokenSource();
            CancellationToken token = _pending.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    // catalog.json 是「写临时文件 + 替换」，一次保存会来好几个事件，等安静下来再发
                    await Task.Delay(TimeSpan.FromMilliseconds(2500), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await PublishNowAsync(force: false).ConfigureAwait(false);
            });
        }
    }

    // ── 发布 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 发布当前 data 目录里的清单。force = false 时只发和上次不一样的文件。
    /// 返回（成功与否, 一句话说明），给 /api/publish 和命令行用。
    /// </summary>
    public async Task<(bool Ok, string Message)> PublishNowAsync(bool force, CancellationToken token = default)
    {
        if (Token().Length == 0)
        {
            return Fail("没配 GitHub token（server.json 里的 \"publishToken\"）");
        }

        await _gate.WaitAsync(token).ConfigureAwait(false);

        try
        {
            (string owner, string repo) = Repo();
            string tag = Tag();

            List<FileInfo> files = CollectFiles();

            if (files.Count == 0)
            {
                return Fail($"data 目录里没有可发布的文件：{_dataDir}");
            }

            Dictionary<string, string> assetNames = BuildAssetNames(files);
            Dictionary<string, string> state = LoadState();
            var changed = new List<(FileInfo File, string Asset, string Sha)>();

            foreach (FileInfo file in files)
            {
                string asset = assetNames[file.FullName];
                string sha = await Sha256Async(file.FullName, token).ConfigureAwait(false);

                if (force || !state.TryGetValue(asset, out string? known) || !known.Equals(sha, StringComparison.OrdinalIgnoreCase))
                {
                    changed.Add((file, asset, sha));
                }
            }

            if (changed.Count == 0)
            {
                _lastError = string.Empty;
                Console.WriteLine("[发布] 数据没有变化，不用上传");
                ServerLog.Info("自动发布：数据没有变化，不用上传");
                return (true, "数据没有变化，不用上传");
            }

            (long releaseId, string releaseError) = await EnsureReleaseAsync(owner, repo, tag, token).ConfigureAwait(false);

            if (releaseId <= 0)
            {
                return Fail($"找不到也没建成 Release：{releaseError}（token 需要这个仓库的 Contents: Read and write）");
            }

            Dictionary<string, long> assets = await ListAssetsAsync(owner, repo, releaseId, token).ConfigureAwait(false);

            var uploaded = new List<string>();

            foreach ((FileInfo file, string asset, string sha) in changed)
            {
                if (assets.TryGetValue(asset, out long assetId))
                {
                    await DeleteAssetAsync(owner, repo, assetId, token).ConfigureAwait(false);
                }

                await UploadAssetAsync(owner, repo, releaseId, file, asset, token).ConfigureAwait(false);

                state[asset] = sha;
                uploaded.Add($"{asset}（{Size(file.Length)}）");
            }

            SaveState(state);

            _lastPublishedAt = DateTimeOffset.Now;
            _lastError = string.Empty;
            _lastUploaded = string.Join("、", uploaded);

            string message = $"已上传 {uploaded.Count} 个文件到 {owner}/{repo} @ {tag}：{_lastUploaded}";
            Console.WriteLine("[发布] " + message);
            ServerLog.Success("自动发布：" + message);

            return (true, message);
        }
        catch (OperationCanceledException)
        {
            return Fail("发布被取消");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private (bool Ok, string Message) Fail(string message)
    {
        _lastError = message;
        Console.WriteLine("[发布] 失败：" + message);
        ServerLog.Error("发布失败：" + message);
        return (false, message);
    }

    /// <summary>data 目录下所有数据都要发（catalog.json、.reg、vocab 词库……），不含临时文件和发布状态。</summary>
    private List<FileInfo> CollectFiles()
    {
        var files = new List<FileInfo>();

        try
        {
            foreach (string path in Directory.GetFiles(_dataDir, "*", SearchOption.AllDirectories))
            {
                var file = new FileInfo(path);
                string name = file.Name;

                // 自己写的发布状态、上传/下载中的临时文件不发布
                if (name.StartsWith('.') ||
                    name.EndsWith(".download", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".upload", StringComparison.OrdinalIgnoreCase) ||
                    file.Length == 0)
                {
                    continue;
                }

                if (name.EndsWith(".reg", StringComparison.OrdinalIgnoreCase) && !_config.PublishRegFiles)
                {
                    continue;
                }

                files.Add(file);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[发布] 读不了 data 目录：{ex.Message}");
        }

        return files;
    }

    /// <summary>
    /// 给每个文件定一个 Release 资产名：根目录文件沿用数据仓库的名字；
    /// 子目录里的文件（如 vocab/pinyin-vocab.zip）用「子目录_文件名」，避免和别的目录重名。
    /// </summary>
    private Dictionary<string, string> BuildAssetNames(List<FileInfo> files)
    {
        var duplicated = files
            .GroupBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (FileInfo file in files)
        {
            string relative = Path.GetRelativePath(_dataDir, file.FullName).Replace('\\', '/');
            bool nested = relative.Contains('/');

            string asset = DataSync.RemoteName(file.Name);

            if (nested || duplicated.Contains(file.Name))
            {
                asset = relative.Replace('/', '_');
            }

            names[file.FullName] = asset;
        }

        return names;
    }

    /// <summary>取 tag 对应的 Release，没有就建一个。返回 id（0 = 失败）和失败原因（给界面看）。</summary>
    private async Task<(long Id, string Error)> EnsureReleaseAsync(string owner, string repo, string tag, CancellationToken token)
    {
        using HttpResponseMessage existing = await _http
            .GetAsync($"{ApiBase()}/repos/{owner}/{repo}/releases/tags/{Uri.EscapeDataString(tag)}", token)
            .ConfigureAwait(false);

        if (existing.IsSuccessStatusCode)
        {
            return (ReadId(await existing.Content.ReadAsStringAsync(token).ConfigureAwait(false)), string.Empty);
        }

        string lookupError = string.Empty;

        if (existing.StatusCode != HttpStatusCode.NotFound)
        {
            string body = await existing.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            lookupError = $"HTTP {(int)existing.StatusCode} {Trim(body)}";
            Console.WriteLine($"[发布] 查 Release 失败：{lookupError}");
        }

        // 不存在就建一个（tag 也一起建出来）
        var payload = new JsonObject
        {
            ["tag_name"] = tag,
            ["name"] = "Artk Tools 数据",
            ["body"] = "Artk Tools 服务器自动发布：catalog.json（+ 可选的 .reg 配置）。",
            ["draft"] = false,
            ["prerelease"] = false,
        };

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage created = await _http
            .PostAsync($"{ApiBase()}/repos/{owner}/{repo}/releases", content, token)
            .ConfigureAwait(false);

        string response = await created.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!created.IsSuccessStatusCode)
        {
            string error = $"HTTP {(int)created.StatusCode} {Trim(response)}";
            Console.WriteLine($"[发布] 建 Release 失败：{error}");
            return (0, string.IsNullOrEmpty(lookupError) ? error : lookupError);
        }

        long id = ReadId(response);
        Console.WriteLine($"[发布] 已创建 Release {tag}（id {id}）");
        return (id, string.Empty);
    }

    private async Task<Dictionary<string, long>> ListAssetsAsync(string owner, string repo, long releaseId, CancellationToken token)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        using HttpResponseMessage response = await _http
            .GetAsync($"{ApiBase()}/repos/{owner}/{repo}/releases/{releaseId}/assets?per_page=100", token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return map;
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));

        foreach (JsonElement asset in document.RootElement.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out JsonElement name) && asset.TryGetProperty("id", out JsonElement id))
            {
                map[name.GetString() ?? string.Empty] = id.GetInt64();
            }
        }

        return map;
    }

    private async Task DeleteAssetAsync(string owner, string repo, long assetId, CancellationToken token)
    {
        using HttpResponseMessage response = await _http
            .DeleteAsync($"{ApiBase()}/repos/{owner}/{repo}/releases/assets/{assetId}", token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[发布] 删旧资产失败：HTTP {(int)response.StatusCode}");
        }
    }

    private async Task UploadAssetAsync(string owner, string repo, long releaseId, FileInfo file, string assetName, CancellationToken token)
    {
        byte[] bytes = await File.ReadAllBytesAsync(file.FullName, token).ConfigureAwait(false);

        string url = $"{UploadBase()}/repos/{owner}/{repo}/releases/{releaseId}/assets" +
                     $"?name={Uri.EscapeDataString(assetName)}&label={Uri.EscapeDataString("Artk Tools 自动发布")}";

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using HttpResponseMessage response = await _http.PostAsync(url, content, token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            throw new InvalidOperationException($"上传 {assetName} 失败：HTTP {(int)response.StatusCode} {Trim(body)}");
        }
    }

    private static long ReadId(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("id", out JsonElement id) && id.TryGetInt64(out long value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // 不是 JSON（比如代理返回的 HTML 错误页），当作失败
        }

        return 0;
    }

    private static string Trim(string body)
    {
        string text = System.Text.RegularExpressions.Regex.Replace(body ?? string.Empty, @"\s+", " ").Trim();
        return text.Length > 220 ? text[..220] + "…" : text;
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.0} MB"
        : $"{bytes / 1024d:0.0} KB";

    private static async Task<string> Sha256Async(string path, CancellationToken token)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ── 发布状态（记住上次发了什么，避免重复上传） ────────────────────────

    private string StatePath => Path.Combine(_dataDir, StateFileName);

    private Dictionary<string, string> LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StatePath)) ?? new(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[发布] 读状态失败（忽略）：{ex.Message}");
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveState(Dictionary<string, string> state)
    {
        try
        {
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, Json));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[发布] 写状态失败（下次会重发一遍）：{ex.Message}");
        }
    }
}
