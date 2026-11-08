using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArtkTools.Services;

/// <summary>服务器上保存的微软词汇信息（GET /api/vocab 的返回体）。</summary>
public sealed class PinyinVocabInfo
{
    public bool Exists { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    public int Count { get; set; }

    public long Size { get; set; }

    public List<string> Files { get; set; } = new();
}

/// <summary>
/// 微软拼音词库的云端备份：把 %APPDATA%\Microsoft\InputMethod\Chs 下的
/// ChsPinyin*（.dat / .lex / .bak）打包上传到 NovaServer，或把服务器上的词库导回本机。
/// 只碰微软拼音，不动其它输入法。
/// </summary>
public static class PinyinVocab
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>微软拼音词库目录（罗姆的，不是 Local）。</summary>
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "InputMethod", "Chs");

    /// <summary>导入前旧词库的备份目录。</summary>
    private static string BackupRoot => Path.Combine(AppSettings.DataFolder, "pinyin-vocab-backup");

    /// <summary>
    /// 词库目录里的文件都要一起传：只传自造词词库时，别的机器上残留的旧缓存会让新词库不生效；
    /// 导入时也是整目录替换，所以这里只挡掉路径穿越，不挑文件名。
    /// </summary>
    private static bool IsVocabFile(string name)
    {
        return name.Length > 0 && Path.GetFileName(name) == name;
    }

    /// <summary>本机需要上传 / 备份的词库文件（微软拼音目录里的全部文件，子目录不动）。</summary>
    public static string[] LocalFiles()
    {
        var files = new List<string>();

        try
        {
            if (!Directory.Exists(Folder))
            {
                return Array.Empty<string>();
            }

            foreach (string path in Directory.GetFiles(Folder))
            {
                if (IsVocabFile(Path.GetFileName(path)))
                {
                    files.Add(path);
                }
            }
        }
        catch
        {
            // 目录读不了就当作没有词库
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files.ToArray();
    }

    public static (int Count, long Size) LocalStats()
    {
        string[] files = LocalFiles();
        long size = 0;

        foreach (string path in files)
        {
            try
            {
                size += new FileInfo(path).Length;
            }
            catch
            {
                // 单个文件读不了就跳过
            }
        }

        return (files.Length, size);
    }

    /// <summary>读服务器上的词汇信息；服务器上还没有时返回 Exists = false。</summary>
    public static async Task<PinyinVocabInfo> FetchInfoAsync(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/vocab");
        ApplyKey(request);

        using HttpResponseMessage response = await Client.SendAsync(request, token).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ErrorOf(body, (int)response.StatusCode));
        }

        return string.IsNullOrWhiteSpace(body)
            ? new PinyinVocabInfo()
            : JsonSerializer.Deserialize<PinyinVocabInfo>(body, Json) ?? new PinyinVocabInfo();
    }

    /// <summary>把本机微软拼音词库打包上传到服务器（覆盖上一份）。</summary>
    public static async Task<(bool Ok, string Message)> UploadAsync(CancellationToken token = default)
    {
        string[] files = LocalFiles();

        if (files.Length == 0)
        {
            return (false, "本机没有找到微软拼音词库");
        }

        byte[] bundle = CreateBundle(files);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/vocab");
        request.Content = new ByteArrayContent(bundle);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        ApplyKey(request);

        using HttpResponseMessage response = await Client.SendAsync(request, token).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return (false, ErrorOf(body, (int)response.StatusCode));
        }

        long size = 0;
        foreach (string path in files)
        {
            try
            {
                size += new FileInfo(path).Length;
            }
            catch
            {
                // 统计失败不影响上传结果
            }
        }

        return (true, $"已上传 {files.Length} 个词库文件 · {FormatSize(size)}");
    }

    /// <summary>从服务器下载词库并导入本机，导入前自动备份旧词库。</summary>
    public static async Task<(bool Ok, string Message)> ImportAsync(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/vocab/download");
        ApplyKey(request);

        using HttpResponseMessage response = await Client.SendAsync(request, token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return (false, ErrorOf(body, (int)response.StatusCode));
        }

        byte[] bundle = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        List<(string Name, byte[] Data)> entries = ReadBundle(bundle);

        if (entries.Count == 0)
        {
            return (false, "服务器上的词库是空的");
        }

        return await Task.Run(() => Apply(entries)).ConfigureAwait(false);
    }

    /// <summary>只把服务器上的词库存成文件（不碰本机输入法），方便自己拿去导入或备份。</summary>
    public static async Task<(bool Ok, string Message)> SaveToFileAsync(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/vocab/download");
        ApplyKey(request);

        using HttpResponseMessage response = await Client.SendAsync(request, token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return (false, ErrorOf(body, (int)response.StatusCode));
        }

        byte[] bundle = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        List<(string Name, byte[] Data)> entries = ReadBundle(bundle);

        if (entries.Count == 0)
        {
            return (false, "服务器上的词库是空的");
        }

        return await Task.Run(() => SaveFiles(entries)).ConfigureAwait(false);
    }

    // ---------------- 打包 / 解包 ----------------

    private static byte[] CreateBundle(string[] files)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string path in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal);
                using Stream target = entry.Open();
                using var source = File.OpenRead(path);
                source.CopyTo(target);
            }
        }

        return buffer.ToArray();
    }

    private static List<(string Name, byte[] Data)> ReadBundle(byte[] bundle)
    {
        var entries = new List<(string Name, byte[] Data)>();

        using var buffer = new MemoryStream(bundle);
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName;

            // 只认根目录下的微软拼音词库，防止压缩包把文件写到别处
            if (name.Length == 0 ||
                Path.GetFileName(name) != name ||
                !name.StartsWith("ChsPinyin", StringComparison.OrdinalIgnoreCase) ||
                !IsVocabFile(name))
            {
                continue;
            }

            using Stream source = entry.Open();
            using var data = new MemoryStream();
            source.CopyTo(data);
            entries.Add((name, data.ToArray()));
        }

        return entries;
    }

    /// <summary>把词库写到「下载」文件夹（没有就写到桌面），方便自己拿去导入或备份。</summary>
    private static (bool Ok, string Message) SaveFiles(List<(string Name, byte[] Data)> entries)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        if (!Directory.Exists(dir))
        {
            dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        try
        {
            Directory.CreateDirectory(dir);

            var saved = new List<string>();

            foreach ((string name, byte[] data) in entries)
            {
                string destination = Path.Combine(dir, name);
                File.WriteAllBytes(destination, data);
                saved.Add(destination);
            }

            return (true, "已保存到 " + string.Join("、", saved));
        }
        catch (Exception ex)
        {
            return (false, "保存失败：" + ex.Message);
        }
    }

    private static (bool Ok, string Message) Apply(List<(string Name, byte[] Data)> entries)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string backupDir = Path.Combine(BackupRoot, stamp);
        int backed = 0;

        // 先备份旧词库，导入失败也能找回来
        foreach (string path in LocalFiles())
        {
            try
            {
                Directory.CreateDirectory(backupDir);
                File.Copy(path, Path.Combine(backupDir, Path.GetFileName(path)), true);
                backed++;
            }
            catch
            {
                // 单个文件备份失败不阻断导入
            }
        }

        bool stopped = StopInputMethod();

        // 词库是按「整个目录」生效的：目录里残留的旧缓存会让新词库看起来没起作用，
        // 所以先把这个目录里的文件全部清掉（OnlineRoaming 这类子目录不动），再把上传的内容写回去。
        _ = ClearFolder();

        int written = 0;
        string? error = null;

        foreach ((string name, byte[] data) in entries)
        {
            string destination = Path.Combine(Folder, name);
            string temp = destination + ".import";

            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllBytes(temp, data);

                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.Move(temp, destination, true);
                        written++;
                        break;
                    }
                    catch (IOException) when (attempt < 9)
                    {
                        // 输入法可能还在收尾：再多等几轮，中途再催一次让它退出
                        if (attempt == 4)
                        {
                            StopInputMethod();
                        }

                        Thread.Sleep(250);
                    }
                    catch (Exception ex)
                    {
                        TryDelete(temp);
                        error = $"{name}：{ex.Message}";
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                error = $"{name}：{ex.Message}";
            }
        }

        if (written == 0)
        {
            // 清空之后一个都没写进去，把备份还原回去，别把输入法停在半空
            RestoreBackup(backupDir);
            return (false, error ?? "没有任何词库文件写入成功");
        }

        if (error is not null)
        {
            RestoreBackup(backupDir);
        }

        string message = $"已导入 {written} 个词库文件";

        if (stopped)
        {
            message += "，微软拼音已重新加载";
        }
        else
        {
            message += "；微软拼音正在使用旧词库，重启电脑后生效";
        }

        if (backed > 0)
        {
            message += $"，旧词库已备份到 {backupDir}";
        }

        if (error is not null)
        {
            message += "\n" + error;
        }

        return (true, message);
    }

    /// <summary>清空词库目录里的文件（不递归，保留子目录）。返回删掉的数量。</summary>
    private static int ClearFolder()
    {
        int removed = 0;

        try
        {
            foreach (string path in Directory.GetFiles(Folder))
            {
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    try
                    {
                        File.Delete(path);
                        removed++;
                        break;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(200);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Thread.Sleep(200);
                    }
                }
            }
        }
        catch
        {
            // 目录都不在就直接往下写
        }

        return removed;
    }

    /// <summary>导入失败时把备份还原到词库目录。</summary>
    private static void RestoreBackup(string backupDir)
    {
        try
        {
            if (!Directory.Exists(backupDir))
            {
                return;
            }

            foreach (string path in Directory.GetFiles(backupDir))
            {
                try
                {
                    File.Copy(path, Path.Combine(Folder, Path.GetFileName(path)), true);
                }
                catch
                {
                    // 尽力而为
                }
            }
        }
        catch
        {
            // 尽力而为
        }
    }

    /// <summary>
    /// 结束微软拼音进程，让它下次输入时重新加载刚写入的词库。
    /// 进程受系统保护，普通权限可能杀不掉；杀不掉也继续写，文件本身允许共享写入。
    /// </summary>
    private static bool StopInputMethod()
    {
        bool stopped = false;

        try
        {
            string taskkill = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");

            using var process = Process.Start(new ProcessStartInfo(taskkill, "/f /im ChsIME.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });

            if (process is not null)
            {
                process.WaitForExit(3000);

                // 成功杀掉，或本来就没在运行，都算这次导入不需要再等重启
                stopped = process.ExitCode == 0 ||
                          Process.GetProcessesByName("ChsIME").Length == 0;
            }
        }
        catch
        {
            // 杀不掉就继续写
        }

        Thread.Sleep(200);
        return stopped;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 临时文件清理失败不影响结果
        }
    }

    // ---------------- 网络辅助 ----------------

    private static string BaseUrl => CatalogServer.NormalizeUrl(CatalogServer.Url);

    private static void ApplyKey(HttpRequestMessage request)
    {
        string key = CatalogServer.Key?.Trim() ?? string.Empty;

        if (key.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        }
    }

    private static string ErrorOf(string body, int status)
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

        if (status == 401)
        {
            return "API Key 不对（服务器拒绝了请求）";
        }

        return status == 404
            ? "服务器还没更新，找不到词汇接口"
            : $"HTTP {status}";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024.0:0.0} MB";
        }

        return $"{bytes / 1024.0:0.#} KB";
    }
}
