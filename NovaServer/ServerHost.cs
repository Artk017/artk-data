using System.IO.Compression;
using System.Text.Json;

namespace NovaServer;

/// <summary>
/// 清单服务本体：读 server.json、准备 data 目录、挂 Kestrel 路由。
/// 无窗口模式（--headless）和带界面的 Artk Server.exe 都用它，区别只是谁把进程吊住。
/// </summary>
internal sealed class ServerHost
{
    private readonly ServerConfig _config;
    private readonly string _configPath;
    private readonly string _baseDir;
    private readonly string _homeDir;
    private readonly CatalogStore _store;
    private readonly GitHubPublisher _publisher;
    private WebApplication? _app;

    private ServerHost(ServerConfig config, string configPath, string baseDir, string homeDir, string dataPath)
    {
        _config = config;
        _configPath = configPath;
        _baseDir = baseDir;
        _homeDir = homeDir;
        _store = new CatalogStore(dataPath);
        _publisher = new GitHubPublisher(dataPath, config);
    }

    public ServerConfig Config => _config;

    public string ConfigPath => _configPath;

    public string BaseDirectory => _baseDir;

    /// <summary>配置 / 数据的根目录（安装版就是安装目录），卸载时按它清理。</summary>
    public string HomeDirectory => _homeDir;

    public CatalogStore Store => _store;

    public GitHubPublisher Publisher => _publisher;

    public int Port => _config.Port;

    /// <summary>读配置；文件不存在就生成一份默认的（和客户端内置密钥一致）。</summary>
    public static ServerHost Load()
    {
        string baseDir = AppContext.BaseDirectory;

        // 安装包（Velopack）会把程序放在 <安装目录>\current 下，升级时整个 current 会被替换。
        // 配置和数据放到安装根目录，升级后端口 / 密钥 / 清单都还在。
        string homeDir = ResolveHomeDir(baseDir);
        SeedData(homeDir, baseDir);

        string configPath = Path.Combine(homeDir, "server.json");

        ServerConfig config;

        if (File.Exists(configPath))
        {
            try
            {
                config = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(configPath), Json.Options) ?? new ServerConfig();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[警告] server.json 读不了（{ex.Message}），用默认配置");
                ServerLog.Warning($"server.json 读取失败，已改用默认配置：{ex.Message}");
                config = new ServerConfig();
            }
        }
        else
        {
            // 用固定密钥，和客户端内置的一致：客户端不用填，换机器部署也不用手动同步
            config = new ServerConfig { ApiKey = ServerConfig.DefaultApiKey };

            try
            {
                File.WriteAllText(configPath, JsonSerializer.Serialize(config, Json.Options));
                Console.WriteLine($"[初始化] 已生成配置文件 {configPath}");
                ServerLog.Info($"已生成默认配置：{configPath}");
            }
            catch (Exception ex)
            {
                // 目录只读（比如放在 Program Files）时也别拦着界面上改设置
                Console.WriteLine($"[警告] 无法生成 server.json（{ex.Message}），本次用默认配置");
                ServerLog.Warning($"无法写入 server.json，本次使用默认配置：{ex.Message}");
            }
        }

        string dataPath = Path.IsPathRooted(config.DataFile)
            ? config.DataFile
            : Path.Combine(homeDir, config.DataFile);

        ServerLog.Info($"配置：{configPath}");
        ServerLog.Info($"数据文件：{dataPath}");

        return new ServerHost(config, configPath, baseDir, homeDir, dataPath);
    }

    /// <summary>
    /// 配置 / 数据的根目录：默认就是 exe 所在目录；
    /// 从安装包装的（exe 在 current 里）用上一层，跟着安装目录走，升级不会被覆盖。
    /// 也支持环境变量 ARTK_SERVER_HOME 强制指定。
    /// </summary>
    private static string ResolveHomeDir(string appDir)
    {
        string? custom = Environment.GetEnvironmentVariable("ARTK_SERVER_HOME");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return Path.GetFullPath(custom.Trim());
        }

        string root = Path.GetDirectoryName(appDir.TrimEnd(Path.DirectorySeparatorChar)) ?? appDir;
        return File.Exists(Path.Combine(root, "Update.exe")) ? root : appDir;
    }

    /// <summary>首次从安装包装时，把随包带来的 data 文件（catalog.json / *.reg）复制到安装根目录。</summary>
    private static void SeedData(string homeDir, string appDir)
    {
        if (string.Equals(homeDir, appDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            string src = Path.Combine(appDir, "data");
            string dst = Path.Combine(homeDir, "data");
            if (!Directory.Exists(src) || Directory.Exists(dst))
            {
                return;
            }

            Directory.CreateDirectory(dst);
            foreach (string file in Directory.GetFiles(src))
            {
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[警告] 初始化 data 目录失败（{ex.Message}），稍后会尝试从数据仓库下载");
            ServerLog.Warning($"初始化 data 目录失败：{ex.Message}");
        }
    }

    /// <summary>启动前准备：data 目录补齐（缺的从数据仓库下载），清单文件加载 / 初始化。</summary>
    public async Task PrepareAsync(bool refreshData)
    {
        await DataSync.EnsureAsync(_store.FilePath, _config, refreshData);
        _store.Initialize();
        ServerLog.Info($"数据准备完成：{Path.GetDirectoryName(_store.FilePath)}");
    }

    /// <summary>建 Kestrel 应用（接口 + 配置接口）。只建一次。</summary>
    public WebApplication BuildApp()
    {
        if (_app is not null)
        {
            return _app;
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = _baseDir,
        });

        builder.WebHost.UseUrls($"http://0.0.0.0:{_config.Port}");
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        });

        WebApplication app = builder.Build();
        MapRoutes(app);
        _app = app;
        return app;
    }

    public async Task StartAsync()
    {
        await BuildApp().StartAsync();
        ServerLog.Success($"清单服务已启动：http://localhost:{_config.Port}");
    }

    public async Task StopAsync()
    {
        _publisher.Stop();

        if (_app is null)
        {
            return;
        }

        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(3));
            ServerLog.Info("清单服务已停止");
        }
        catch (Exception)
        {
            // 退出阶段停不掉也无所谓
        }
    }

    private void MapRoutes(WebApplication app)
    {
        ServerConfig config = _config;
        CatalogStore store = _store;
        GitHubPublisher publisher = _publisher;

        // ── 鉴权：校验 X-Api-Key（或 Bearer），读接口按 RequireKeyForRead 一起卡 ──
        bool KeyOk(HttpContext ctx)
        {
            string expected = (config.ApiKey ?? string.Empty).Trim();

            if (expected.Length == 0)
            {
                // 明确清空密钥 = 不校验（只建议在完全可信的内网里这么干）
                return true;
            }

            string? provided = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();

            if (string.IsNullOrEmpty(provided))
            {
                string? auth = ctx.Request.Headers.Authorization.FirstOrDefault();
                if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    provided = auth["Bearer ".Length..].Trim();
                }
            }

            if (string.IsNullOrEmpty(provided))
            {
                return false;
            }

            // 定长比较，别让响应时间泄露密钥前缀
            byte[] a = System.Text.Encoding.UTF8.GetBytes(provided);
            byte[] b = System.Text.Encoding.UTF8.GetBytes(expected);
            return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
        }

        IResult Denied() => Results.Json(new { error = "需要正确的 API Key（X-Api-Key 头）" }, statusCode: 401);

        // ── 读接口 ──────────────────────────────────────────────────────
        app.MapGet("/api/health", async (HttpContext ctx) =>
        {
            if (config.RequireKeyForRead && !KeyOk(ctx))
            {
                return Denied();
            }

            CatalogFile data = await store.ReadAsync();
            return Results.Ok(new
            {
                ok = true,
                version = data.Version,
                updatedAt = data.UpdatedAt,
                count = data.Items.Count,
                serverTime = DateTimeOffset.Now,
            });
        });

        app.MapGet("/api/apps", async (HttpContext ctx) =>
        {
            if (config.RequireKeyForRead && !KeyOk(ctx))
            {
                return Denied();
            }

            CatalogFile data = await store.ReadAsync();
            var list = data.Items
                .Where(a => a.Enabled)
                .OrderBy(a => a.Sort)
                .ThenBy(a => a.Name, StringComparer.Ordinal)
                .ToList();

            return Results.Ok(list);
        });

        app.MapGet("/api/apps/all", async (HttpContext ctx) =>
        {
            if (config.RequireKeyForRead && !KeyOk(ctx))
            {
                return Denied();
            }

            CatalogFile data = await store.ReadAsync();
            return Results.Ok(data.Items.OrderBy(a => a.Sort).ToList());
        });

        app.MapGet("/api/apps/{id}", async (string id, HttpContext ctx) =>
        {
            if (config.RequireKeyForRead && !KeyOk(ctx))
            {
                return Denied();
            }

            AppItem? item = await store.GetAsync(id);
            return item is null ? Results.NotFound(new { error = "没有这款软件" }) : Results.Ok(item);
        });

        /// <summary>桌面端同步用：带上版本号，没变就省一次比较。</summary>
        app.MapGet("/api/catalog", async (HttpContext ctx, int? since) =>
        {
            if (config.RequireKeyForRead && !KeyOk(ctx))
            {
                return Denied();
            }

            CatalogFile data = await store.ReadAsync();

            var apps = data.Items
                .Where(a => a.Enabled)
                .OrderBy(a => a.Sort)
                .ThenBy(a => a.Name, StringComparer.Ordinal)
                .ToList();

            if (since is int known && known == data.Version)
            {
                return Results.Ok(new CatalogResponse
                {
                    Version = data.Version,
                    UpdatedAt = data.UpdatedAt,
                    Count = apps.Count,
                    Apps = new List<AppItem>(),
                });
            }

            return Results.Ok(new CatalogResponse
            {
                Version = data.Version,
                UpdatedAt = data.UpdatedAt,
                Count = apps.Count,
                Apps = apps,
            });
        });

        // ── 写接口 ──────────────────────────────────────────────────────
        app.MapPost("/api/apps", async (AppItem item, HttpContext ctx) =>
        {
            if (!KeyOk(ctx))
            {
                return Denied();
            }

            List<string> errors = CatalogStore.Validate(item);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { errors });
            }

            AppItem created = await store.CreateAsync(item);
            return Results.Created($"/api/apps/{created.Id}", created);
        });

        app.MapPut("/api/apps/{id}", async (string id, AppItem item, HttpContext ctx) =>
        {
            if (!KeyOk(ctx))
            {
                return Denied();
            }

            List<string> errors = CatalogStore.Validate(item);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { errors });
            }

            AppItem? updated = await store.UpdateAsync(id, item);
            return updated is null ? Results.NotFound(new { error = "没有这款软件" }) : Results.Ok(updated);
        });

        app.MapDelete("/api/apps/{id}", async (string id, HttpContext ctx) =>
        {
            if (!KeyOk(ctx))
            {
                return Denied();
            }

            bool removed = await store.DeleteAsync(id);
            return removed ? Results.NoContent() : Results.NotFound(new { error = "没有这款软件" });
        });

        app.MapPost("/api/apps/bulk", async (List<AppItem> items, HttpContext ctx) =>
        {
            if (!KeyOk(ctx))
            {
                return Denied();
            }

            if (items.Count == 0)
            {
                return Results.BadRequest(new { errors = new[] { "列表是空的" } });
            }

            var allErrors = new List<string>();
            foreach (AppItem item in items)
            {
                List<string> itemErrors = CatalogStore.Validate(item);
                if (itemErrors.Count > 0)
                {
                    allErrors.Add($"{item.Name}: {string.Join("；", itemErrors)}");
                }
            }

            if (allErrors.Count > 0)
            {
                return Results.BadRequest(new { errors = allErrors });
            }

            int count = await store.ReplaceAllAsync(items);
            return Results.Ok(new { replaced = count });
        });

        app.MapPost("/api/apps/reorder", async (List<string> ids, HttpContext ctx) =>
        {
            if (!KeyOk(ctx))
            {
                return Denied();
            }

            await store.ReorderAsync(ids);
            return Results.Ok(new { ok = true });
        });

        // ── 资源文件下载（.reg 等），放在 exe 同级的 data 目录 ────────────────
        string[] ListData(string dir)
        {
            try
            {
                return Directory.Exists(dir)
                    ? Directory.GetFiles(dir).Select(f => Path.GetFileName(f) ?? f).ToArray()
                    : Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // 解析 data 目录：永远和清单文件待在一起（安装版即安装根目录下的 data，升级不会丢），
        // 这样上传的文件和自动发布监听的目录是同一个，改完立刻会同步出去。
        string? DataRoot(bool create)
        {
            string root = Path.GetFullPath(
                Path.GetDirectoryName(_store.FilePath) ?? Path.Combine(_homeDir, "data"));

            if (Directory.Exists(root))
            {
                return root;
            }

            if (!create)
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(root);
                return root;
            }
            catch (Exception ex)
            {
                ServerLog.Error($"创建 data 目录失败：{ex.Message}");
                return null;
            }
        }

        // 把软件名收拾成安全的目录名（去掉路径分隔符/非法字符）
        string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                s = s.Replace(c, '_');
            }

            s = s.Replace('/', '_').Replace('\\', '_');
            return string.IsNullOrWhiteSpace(s) ? "app" : s;
        }

        // 可选映射表 data\regmap.json：{ "软件名": ["b.reg", "sub\\c.reg"] }，把任意文件名的根目录 reg 归到某软件
        Dictionary<string, List<string>> ReadRegMap(string root)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(root, "regmap.json");

            if (!File.Exists(path))
            {
                return map;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return map;
                }

                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var list = new List<string>();
                    foreach (JsonElement item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            string? value = item.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                list.Add(value!);
                            }
                        }
                    }

                    if (list.Count > 0)
                    {
                        map[prop.Name] = list;
                    }
                }
            }
            catch
            {
                // 映射表写坏了就忽略，不影响其它配置
            }

            return map;
        }

        app.MapGet("/data/{name}", (string name, HttpContext ctx) =>
        {
            if (config.RequireKeyForRead && !KeyOk(ctx))
            {
                return Denied();
            }

            // 兼容两种部署：exe 同级的 data，和「当前工作目录」的 data
            string[] roots =
            {
                Path.GetFullPath(Path.Combine(_baseDir, "data")),
                Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "data")),
            };

            foreach (string root in roots)
            {
                string full = Path.GetFullPath(Path.Combine(root, name));
                string rootWithSep = root + Path.DirectorySeparatorChar;

                // 防目录穿越：必须落在 data 目录里，且文件确实存在
                if (full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    return Results.File(File.ReadAllBytes(full), "application/octet-stream", Path.GetFileName(full));
                }
            }

            // 找不到：把实际查找的目录和目录里的文件一起返回，方便排查
            string primary = roots[0];
            return Results.NotFound(new
            {
                error = "文件不存在",
                dataDir = primary,
                dataDirExists = Directory.Exists(primary),
                files = ListData(primary),
            });
        });

        // ── 每个软件的 .reg 配置：列出 / 下载 / 上传（一个软件可多个、文件名任意）──
        app.MapGet("/api/regs/{appName}", (string appName, HttpContext ctx) =>
        {
            if (config.RequireKeyForRead && !KeyOk(ctx))
            {
                return Denied();
            }

            string? root = DataRoot(false);
            var files = new List<string>();
            var extra = new List<string>();
            string? legacy = null;

            if (root is not null)
            {
                string safe = SafeName(appName);
                string legacyPath = Path.Combine(root, safe + ".reg");
                if (File.Exists(legacyPath))
                {
                    legacy = Path.GetFileName(legacyPath);
                }

                string folder = Path.Combine(root, safe);
                if (Directory.Exists(folder))
                {
                    files.AddRange(Directory.GetFiles(folder, "*.reg")
                        .Select(f => Path.GetFileName(f) ?? f)
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                }

                // 根目录 {软件}.reg 与子目录里同名文件重复时，以子目录（后上传的）为准，避免同一个 reg 执行两遍
                if (legacy is not null && files.Contains(legacy, StringComparer.OrdinalIgnoreCase))
                {
                    legacy = null;
                }

                Dictionary<string, List<string>> regMap = ReadRegMap(root);
                if (regMap.TryGetValue(appName, out List<string>? mapped) && mapped is not null)
                {
                    foreach (string rel in mapped)
                    {
                        string normalized = rel.Replace('\\', '/').TrimStart('/');
                        string full = Path.GetFullPath(Path.Combine(root, normalized));
                        string prefix = root + Path.DirectorySeparatorChar;

                        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                        {
                            continue;
                        }

                        // 已经由 legacy / files 覆盖的就不重复加
                        if (string.Equals(normalized, legacy, StringComparison.OrdinalIgnoreCase)
                            || files.Contains(Path.GetFileName(full), StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        extra.Add(normalized);
                    }
                }
            }

            return Results.Ok(new { app = appName, legacy, files, extra });
        });

        app.MapGet("/api/regs/{appName}/{file}", (string appName, string file, HttpContext ctx) =>
        {
            if (config.RequireKeyForRead && !KeyOk(ctx))
            {
                return Denied();
            }

            string? root = DataRoot(false);
            if (root is null)
            {
                return Results.NotFound(new { error = "data 目录不存在" });
            }

            string folder = Path.Combine(root, SafeName(appName));
            string full = Path.GetFullPath(Path.Combine(folder, Path.GetFileName(file)));
            string prefix = folder + Path.DirectorySeparatorChar;

            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            {
                return Results.File(File.ReadAllBytes(full), "application/octet-stream", Path.GetFileName(full));
            }

            return Results.NotFound(new { error = "文件不存在" });
        });

        app.MapGet("/api/regfile", (string? path, HttpContext ctx) =>
        {
            if (config.RequireKeyForRead && !KeyOk(ctx))
            {
                return Denied();
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return Results.BadRequest(new { error = "缺少 path 参数" });
            }

            string? root = DataRoot(false);
            if (root is null)
            {
                return Results.NotFound(new { error = "data 目录不存在" });
            }

            string normalized = path.Replace('\\', '/').TrimStart('/');
            if (!normalized.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "只允许下载 .reg 文件" });
            }

            string full = Path.GetFullPath(Path.Combine(root, normalized));
            string prefix = root + Path.DirectorySeparatorChar;

            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            {
                return Results.File(File.ReadAllBytes(full), "application/octet-stream", Path.GetFileName(full));
            }

            return Results.NotFound(new { error = "文件不存在" });
        });

        app.MapPost("/api/regs/{appName}", async (string appName, HttpContext ctx) =>
        {
            if (!KeyOk(ctx))
            {
                return Denied();
            }

            string? root = DataRoot(true);
            if (root is null)
            {
                return Results.Json(new { error = "服务器 data 目录不可用" }, statusCode: 500);
            }

            string folder = Path.Combine(root, SafeName(appName));
            Directory.CreateDirectory(folder);

            string requested = ctx.Request.Query["name"].FirstOrDefault() ?? $"upload-{DateTime.Now:yyyyMMdd-HHmmss}.reg";
            string fileName = Path.GetFileName(requested);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"upload-{DateTime.Now:yyyyMMdd-HHmmss}.reg";
            }

            if (!fileName.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
            {
                fileName += ".reg";
            }

            string dest = Path.Combine(folder, fileName);
            int index = 2;
            while (File.Exists(dest))
            {
                dest = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(fileName)} ({index}).reg");
                index++;
            }

            try
            {
                using var fs = File.Create(dest);
                await ctx.Request.Body.CopyToAsync(fs);
                if (fs.Length == 0)
                {
                    fs.Close();
                    File.Delete(dest);
                    return Results.BadRequest(new { error = "上传内容为空" });
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = "保存失败：" + ex.Message }, statusCode: 500);
            }

            ServerLog.Success($"已上传配置：{SafeName(appName)} / {Path.GetFileName(dest)}");
            return Results.Ok(new { app = SafeName(appName), file = Path.GetFileName(dest) });
        });

        // ── 微软拼音词库：客户端「微软词汇」上传 / 下载 ─────────────────────
        string? VocabFile(bool create)
        {
            string? root = DataRoot(create);
            if (root is null)
            {
                return null;
            }

            string folder = Path.Combine(root, "vocab");
            if (create)
            {
                Directory.CreateDirectory(folder);
            }

            return Path.Combine(folder, "pinyin-vocab.zip");
        }

        // 从 zip 读出条目数与原始大小；文件损坏时 Ok = false
        (bool Ok, DateTimeOffset UploadedAt, int Count, long Size, List<string> Files) ReadVocab(string path)
        {
            var names = new List<string>();
            long size = 0;

            try
            {
                using ZipArchive archive = ZipFile.OpenRead(path);
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    names.Add(entry.FullName);
                    size += entry.Length;
                }
            }
            catch
            {
                return (false, default, 0, 0, names);
            }

            return (true, new DateTimeOffset(File.GetLastWriteTime(path)), names.Count, size, names);
        }

        app.MapGet("/api/vocab", (HttpContext ctx) =>
        {
            if (config.RequireKeyForRead && !KeyOk(ctx))
            {
                return Denied();
            }

            string? path = VocabFile(false);
            if (path is null || !File.Exists(path))
            {
                return Results.Ok(new { exists = false });
            }

            (bool ok, DateTimeOffset uploadedAt, int count, long size, List<string> files) = ReadVocab(path);
            if (!ok)
            {
                return Results.Json(new { error = "服务器上的词库压缩包已损坏" }, statusCode: 500);
            }

            return Results.Ok(new { exists = true, uploadedAt, count, size, files });
        });

        app.MapGet("/api/vocab/download", (HttpContext ctx) =>
        {
            if (config.RequireKeyForRead && !KeyOk(ctx))
            {
                return Denied();
            }

            string? path = VocabFile(false);
            if (path is null || !File.Exists(path))
            {
                return Results.NotFound(new { error = "服务器上还没有上传过微软词汇" });
            }

            return Results.File(File.ReadAllBytes(path), "application/zip", "pinyin-vocab.zip");
        });

        app.MapPost("/api/vocab", async (HttpContext ctx) =>
        {
            if (!KeyOk(ctx))
            {
                return Denied();
            }

            string? path = VocabFile(true);
            if (path is null)
            {
                return Results.Json(new { error = "服务器 data 目录不可用" }, statusCode: 500);
            }

            using var buffer = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(buffer);

            if (buffer.Length == 0)
            {
                return Results.BadRequest(new { error = "上传内容为空" });
            }

            buffer.Position = 0;
            var names = new List<string>();
            long size = 0;

            try
            {
                using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = entry.FullName;
                    // 微软拼音词库目录是整目录同步的，文件名不挑，只挡路径穿越
                    bool safeName = name.Length > 0 && Path.GetFileName(name) == name && entry.Length > 0;

                    if (!safeName)
                    {
                        return Results.BadRequest(new { error = $"压缩包里有不合法的文件：{name}" });
                    }

                    names.Add(name);
                    size += entry.Length;
                }
            }
            catch (InvalidDataException)
            {
                return Results.BadRequest(new { error = "上传的不是有效的 zip 压缩包" });
            }

            if (names.Count == 0)
            {
                return Results.BadRequest(new { error = "压缩包里没有词库文件" });
            }

            buffer.Position = 0;
            string temp = path + ".upload";

            try
            {
                using (var fs = File.Create(temp))
                {
                    await buffer.CopyToAsync(fs);
                }

                File.Move(temp, path, true);
            }
            catch (Exception ex)
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // 清理失败不影响返回
                }

                return Results.Json(new { error = "保存失败：" + ex.Message }, statusCode: 500);
            }

            ServerLog.Success($"已上传微软词汇：{string.Join("、", names)}（{size:N0} 字节）");

            return Results.Ok(new
            {
                ok = true,
                uploadedAt = new DateTimeOffset(File.GetLastWriteTime(path)),
                count = names.Count,
                size,
                files = names,
            });
        });

        // 没有网页设置页了：根路径回一句纯文本，免得访问者以为是坏了的网页
        app.MapGet("/", () => Results.Text(
            "Artk Server 在运行。设置请直接打开 Artk Server.exe 的界面；接口见 /api/health。",
            "text/plain; charset=utf-8"));

        // 界面用的读写接口（/api/settings）
        SettingsApi.Map(app, config, _configPath, store, publisher, _store.FilePath, KeyOk, Denied);

        // 垃圾清理页保存的勾选（/api/cleaner-settings）
        CleanerSettingsApi.Map(app, config, _store.FilePath, KeyOk, Denied);

        // ── 发布到 GitHub（清单改动 → Release 资产） ────────────────────────
        app.MapGet("/api/publish", (HttpContext ctx) =>
        {
            if (config.RequireKeyForRead && !KeyOk(ctx))
            {
                return Denied();
            }

            return Results.Ok(publisher.Status());
        });

        app.MapPost("/api/publish", async (HttpContext ctx) =>
        {
            if (!KeyOk(ctx))
            {
                return Denied();
            }

            (bool ok, string message) = await publisher.PublishNowAsync(force: true);
            return ok
                ? Results.Ok(new { ok, message })
                : Results.Json(new { ok, message }, statusCode: 502);
        });
    }

    /// <summary>无窗口模式启动时打印的一小段横幅。</summary>
    public void PrintBanner()
    {
        Console.WriteLine();
        Console.WriteLine("========================================================");
        Console.WriteLine("  Artk Server（后台模式）");
        Console.WriteLine($"  监听:    http://0.0.0.0:{_config.Port}  （本机 http://localhost:{_config.Port}）");
        Console.WriteLine($"  数据:    {_store.FilePath}");
        Console.WriteLine($"  密钥:    {(string.IsNullOrWhiteSpace(_config.ApiKey) ? "未设置（写接口不校验）" : _config.ApiKey)}");
        Console.WriteLine($"  发布:    {(_config.AutoPublish ? (_publisher.Enabled ? "开 · 清单改动后自动传 GitHub" : "开但缺 token，已跳过") : "关（server.json 里 autoPublish / publishToken）")}");
        Console.WriteLine("  界面:    用 Artk Server.exe（不带参数）打开设置界面");
        Console.WriteLine("  接口:    /api/health  /api/catalog  /api/regs  /api/cleaner-settings  /api/publish  /api/settings");
        Console.WriteLine("========================================================");
        Console.WriteLine();
    }
}
