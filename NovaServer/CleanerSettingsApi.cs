using System.Text.Json;

namespace NovaServer;

/// <summary>
/// 垃圾清理页的「清理项目勾选」接口：客户端把勾选的项目名 POST 上来，
/// 服务端落到 data\cleaner-settings.json；客户端下次打开页面先来读一份，
/// 这样换机器 / 重装后还是同一套勾选。
/// </summary>
internal static class CleanerSettingsApi
{
    /// <summary>客户端提交的勾选列表。</summary>
    private sealed class Payload
    {
        public List<string> Selected { get; set; } = new();

        public string? Source { get; set; }
    }

    /// <summary>落盘结构：勾选列表 + 保存时间 + 来源。</summary>
    private sealed class Stored
    {
        public DateTimeOffset UpdatedAt { get; set; }

        public string Source { get; set; } = string.Empty;

        public List<string> Selected { get; set; } = new();
    }

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Map(
        WebApplication app,
        ServerConfig config,
        string dataFilePath,
        Func<HttpContext, bool> keyOk,
        Func<IResult> denied)
    {
        string SettingsPath() => Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(dataFilePath)) ?? AppContext.BaseDirectory,
            "cleaner-settings.json");

        app.MapGet("/api/cleaner-settings", (HttpContext ctx) =>
        {
            if (config.RequireKeyForRead && !keyOk(ctx))
            {
                return denied();
            }

            string path = SettingsPath();
            if (!File.Exists(path))
            {
                return Results.Ok(new
                {
                    exists = false,
                    count = 0,
                    selected = Array.Empty<string>(),
                    updatedAt = (DateTimeOffset?)null,
                });
            }

            try
            {
                Stored stored = JsonSerializer.Deserialize<Stored>(
                    File.ReadAllText(path), ReadOptions) ?? new Stored();

                return Results.Ok(new
                {
                    exists = true,
                    count = stored.Selected.Count,
                    selected = stored.Selected,
                    updatedAt = stored.UpdatedAt,
                    source = stored.Source,
                });
            }
            catch (Exception ex)
            {
                ServerLog.Error($"读取 cleaner-settings.json 失败：{ex.Message}");
                return Results.Json
                    (
                        new { error = "服务器上的清理设置已损坏：" + ex.Message },
                        statusCode: 500
                    );
            }
        });

        app.MapPost("/api/cleaner-settings", async (HttpContext ctx) =>
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

            Payload? payload;

            try
            {
                payload = JsonSerializer.Deserialize<Payload>(body, ReadOptions);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = "请求内容不是合法 JSON：" + ex.Message });
            }

            if (payload is null)
            {
                return Results.BadRequest(new { error = "请求内容为空" });
            }

            if (payload.Selected.Count > 5000)
            {
                return Results.BadRequest(new { error = "勾选项目过多（上限 5000 项）" });
            }

            // 去空、去重（忽略大小写），保留客户端传来的原始写法
            var selected = new List<string>();

            foreach (string raw in payload.Selected)
            {
                string name = raw?.Trim() ?? string.Empty;

                if (name.Length == 0 || name.Length > 260)
                {
                    continue;
                }

                if (selected.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                selected.Add(name);
            }

            var stored = new Stored
            {
                UpdatedAt = DateTimeOffset.Now,
                Source = (payload.Source ?? string.Empty).Trim(),
                Selected = selected,
            };

            string path = SettingsPath();
            string temp = path + ".upload";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
                File.WriteAllText(temp, JsonSerializer.Serialize(stored, WriteOptions) + Environment.NewLine);
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
                    // 清理临时文件失败不影响返回
                }

                ServerLog.Error($"保存 cleaner-settings.json 失败：{ex.Message}");
                return Results.Json(new { error = "保存失败：" + ex.Message }, statusCode: 500);
            }

            ServerLog.Success($"已保存清理勾选：{selected.Count} 项");

            return Results.Ok(new
            {
                ok = true,
                count = selected.Count,
                updatedAt = stored.UpdatedAt,
            });
        });
    }
}
