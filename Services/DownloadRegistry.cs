using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WinUIDemo.Services;

/// <summary>
/// 记录每个软件已下载安装包的路径，持久化到 %LOCALAPPDATA%\Nova\downloads.json。
/// 这样退出应用再打开，只要安装包还在下载文件夹里，就能认出「已下载」而不是让人重新下一遍。
/// 键用条目的 Id（没有则用名字）。
/// </summary>
public static class DownloadRegistry
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static Dictionary<string, string> _map = Load();

    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova");

    private static string FilePath => Path.Combine(Folder, "downloads.json");

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath))
                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // 损坏就当空的
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void Persist()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_map, JsonOptions));
        }
        catch
        {
            // 写盘失败不影响本次使用
        }
    }

    /// <summary>取已记录的安装包路径；文件已不存在则顺手清掉并返回 null。</summary>
    public static string? Get(string key)
    {
        lock (Gate)
        {
            if (!_map.TryGetValue(key, out string? path))
            {
                return null;
            }

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                return path;
            }

            _map.Remove(key);
            Persist();
            return null;
        }
    }

    public static void Set(string key, string path)
    {
        lock (Gate)
        {
            _map[key] = path;
            Persist();
        }
    }

    public static void Remove(string key)
    {
        lock (Gate)
        {
            if (_map.Remove(key))
            {
                Persist();
            }
        }
    }
}
