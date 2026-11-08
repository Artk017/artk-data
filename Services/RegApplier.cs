using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArtkTools.Services;

/// <summary>
/// 应用 .reg 配置：从清单服务器下载并 reg.exe 导入。服务器端每个软件可有多个 .reg：
/// 旧版根目录的 data\{软件名}.reg + 新版 data\{软件名}\*.reg（通过 /api/regs 上传）。
/// </summary>
public static class RegApplier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>旧的固定文件名接口：{服务器}/data/{文件名}。</summary>
    public static async Task<(bool Ok, string Message)> ApplyFromServerAsync(string fileName, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return (false, "没有指定配置文件名。");
        }

        if (!CatalogServer.Configured)
        {
            return (false, "还没配置清单服务器地址，无法下载配置文件。");
        }

        string baseUrl = CatalogServer.NormalizeUrl(CatalogServer.Url);
        return await DownloadAndImportAsync($"{baseUrl}/data/{Uri.EscapeDataString(fileName)}", token).ConfigureAwait(false);
    }

    /// <summary>应用服务器上某软件的单个配置：{服务器}/api/regs/{软件}/{文件}。</summary>
    public static async Task<(bool Ok, string Message)> ApplyServerFileAsync(string app, string file, CancellationToken token = default)
    {
        if (!CatalogServer.Configured)
        {
            return (false, "还没配置清单服务器地址。");
        }

        string baseUrl = CatalogServer.NormalizeUrl(CatalogServer.Url);
        string url = $"{baseUrl}/api/regs/{Uri.EscapeDataString(app)}/{Uri.EscapeDataString(file)}";
        return await DownloadAndImportAsync(url, token).ConfigureAwait(false);
    }

    /// <summary>应用服务器上某软件的单个任意路径配置：{服务器}/api/regfile?path={相对路径}。</summary>
    public static async Task<(bool Ok, string Message)> ApplyServerRawAsync(string relativePath, CancellationToken token = default)
    {
        if (!CatalogServer.Configured)
        {
            return (false, "还没配置清单服务器地址。");
        }

        string baseUrl = CatalogServer.NormalizeUrl(CatalogServer.Url);
        string url = $"{baseUrl}/api/regfile?path={Uri.EscapeDataString(relativePath)}";
        return await DownloadAndImportAsync(url, token).ConfigureAwait(false);
    }

    /// <summary>向服务器上传一个 .reg 到该软件名下（需要 API Key）。</summary>
    public static async Task<(bool Ok, string Message)> UploadAsync(string app, string localPath, CancellationToken token = default)
    {
        if (!CatalogServer.Configured)
        {
            return (false, "还没配置清单服务器地址，无法上传。");
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(localPath, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, "读取失败：" + ex.Message);
        }

        if (bytes.Length == 0)
        {
            return (false, "文件是空的。");
        }

        string baseUrl = CatalogServer.NormalizeUrl(CatalogServer.Url);
        string name = Path.GetFileName(localPath);
        string url = $"{baseUrl}/api/regs/{Uri.EscapeDataString(app)}?name={Uri.EscapeDataString(name)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            ApplyKey(request);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return (true, "已上传到服务器。");
            }

            return (false, $"上传失败：HTTP {(int)response.StatusCode}（{body}）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, "上传出错：" + ex.Message);
        }
    }

    /// <summary>列出服务器上某软件的配置：旧版 legacy 文件名 + 子目录文件 + 映射表里的任意文件。</summary>
    public static async Task<(bool Ok, string? Legacy, List<string> Files, List<string> Extra, string Message)> ListServerAsync(
        string app,
        CancellationToken token = default)
    {
        var empty = new List<string>();

        if (!CatalogServer.Configured)
        {
            return (false, null, empty, empty, "还没配置清单服务器地址。");
        }

        string baseUrl = CatalogServer.NormalizeUrl(CatalogServer.Url);
        string url = $"{baseUrl}/api/regs/{Uri.EscapeDataString(app)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyKey(request);

            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, null, empty, empty, $"获取配置列表失败：HTTP {(int)response.StatusCode}");
            }

            string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);

            string? legacy = doc.RootElement.TryGetProperty("legacy", out JsonElement le) && le.ValueKind == JsonValueKind.String
                ? le.GetString()
                : null;

            List<string> files = ReadStringArray(doc.RootElement, "files");
            List<string> extra = ReadStringArray(doc.RootElement, "extra");

            return (true, legacy, files, extra, "ok");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, null, empty, empty, "获取配置列表出错：" + ex.Message);
        }
    }

    private static List<string> ReadStringArray(JsonElement root, string property)
    {
        var list = new List<string>();

        if (root.TryGetProperty(property, out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string? value = item.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        list.Add(value!);
                    }
                }
            }
        }

        return list;
    }

    /// <summary>
    /// 应用服务器上该软件的全部配置：旧版 {软件名}.reg + 子目录里的所有 .reg 依次导入。
    /// 服务器一个都没有时，退回旧的固定文件名下载逻辑。
    /// </summary>
    public static async Task<(bool Ok, string Message)> ApplyAllFromServerAsync(
        string app,
        string legacyFileName,
        CancellationToken token = default)
    {
        (bool ok, string? legacy, List<string> files, List<string> extra, string message) =
            await ListServerAsync(app, token).ConfigureAwait(false);

        if (!ok)
        {
            return (false, message);
        }

        var applied = new List<string>();
        var failures = new List<string>();

        if (!string.IsNullOrEmpty(legacy))
        {
            (bool fileOk, string fileMessage) = await ApplyFromServerAsync(legacy!, token).ConfigureAwait(false);
            if (fileOk)
            {
                applied.Add(legacy!);
            }
            else
            {
                failures.Add($"{legacy}：{fileMessage}");
            }
        }

        foreach (string file in files)
        {
            (bool fileOk, string fileMessage) = await ApplyServerFileAsync(app, file, token).ConfigureAwait(false);
            if (fileOk)
            {
                applied.Add(file);
            }
            else
            {
                failures.Add($"{file}：{fileMessage}");
            }
        }

        foreach (string rel in extra)
        {
            (bool fileOk, string fileMessage) = await ApplyServerRawAsync(rel, token).ConfigureAwait(false);
            if (fileOk)
            {
                applied.Add(rel);
            }
            else
            {
                failures.Add($"{rel}：{fileMessage}");
            }
        }

        if (applied.Count == 0 && failures.Count == 0)
        {
            // 列表为空：退回旧的固定文件名
            return await ApplyFromServerAsync(legacyFileName, token).ConfigureAwait(false);
        }

        if (failures.Count == 0)
        {
            return (true, $"已应用 {applied.Count} 个配置：{string.Join("、", applied)}");
        }

        string head = applied.Count > 0 ? $"成功 {applied.Count} 个，失败 {failures.Count} 个。" : "全部失败。";
        return (false, head + "\n" + string.Join("\n", failures));
    }

    private static async Task<(bool Ok, string Message)> DownloadAndImportAsync(string url, CancellationToken token)
    {
        byte[] bytes;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyKey(request);

            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"下载失败：服务器返回 {(int)response.StatusCode}（{Uri.UnescapeDataString(new Uri(url).AbsolutePath)}）");
            }

            bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, "下载失败：" + ex.Message);
        }

        if (bytes.Length == 0)
        {
            return (false, "服务器返回的配置文件是空的。");
        }

        return await ImportAsync(NormalizeRegBytes(bytes), token).ConfigureAwait(false);
    }

    private static void ApplyKey(HttpRequestMessage request)
    {
        string key = CatalogServer.Key;
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        }
    }

    private static async Task<(bool Ok, string Message)> ImportAsync(byte[] bytes, CancellationToken token)
    {
        string temp = Path.Combine(Path.GetTempPath(), "nova-" + Guid.NewGuid().ToString("N") + ".reg");

        try
        {
            await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, "写入临时文件失败：" + ex.Message);
        }

        try
        {
            var psi = new ProcessStartInfo("reg.exe", $"import \"{temp}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return (false, "无法启动 reg.exe。");
            }

            string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                return (true, "设置已应用。");
            }

            return (false, "导入失败：" + (string.IsNullOrWhiteSpace(error) ? $"退出码 {process.ExitCode}" : error.Trim()));
        }
        catch (Exception ex)
        {
            return (false, "导入出错：" + ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // 临时文件删不掉无所谓
            }
        }
    }

    /// <summary>
    /// 把 .reg 字节归一化成 reg.exe 能稳定导入的形式：
    ///   - UTF-8 带 BOM：去掉 BOM，转成 UTF-16 LE（带 BOM）；
    ///   - 无 BOM 但含非 ASCII 的合法 UTF-8：转成 UTF-16 LE（带 BOM）；
    ///   - 其余（ASCII 无 BOM / UTF-16 / ANSI）原样保留。
    /// </summary>
    private static byte[] NormalizeRegBytes(byte[] raw)
    {
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
        {
            string text = Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
            return ToUtf16LeWithBom(text);
        }

        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
        {
            return raw; // 已经是 UTF-16 LE，reg.exe 认
        }

        if (!IsAscii(raw) && IsValidUtf8(raw))
        {
            return ToUtf16LeWithBom(Encoding.UTF8.GetString(raw));
        }

        return raw;
    }

    private static byte[] ToUtf16LeWithBom(string text)
    {
        byte[] body = Encoding.Unicode.GetBytes(text);
        var result = new byte[body.Length + 2];
        result[0] = 0xFF;
        result[1] = 0xFE;
        Buffer.BlockCopy(body, 0, result, 2, body.Length);
        return result;
    }

    private static bool IsAscii(byte[] data)
    {
        foreach (byte b in data)
        {
            if (b >= 0x80)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidUtf8(byte[] data)
    {
        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(data);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
