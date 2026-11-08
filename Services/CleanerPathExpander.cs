using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ArtkTools.Services;

/// <summary>
/// 负责 FileKey 路径的两件事：
/// 1. 展开 %环境变量% 标记（Winapp2 有自己的一套变量名）；
/// 2. 处理路径段里的 * 通配符，找出实际存在的目录。
/// </summary>
public sealed class CleanerPathExpander
{
    private readonly Dictionary<string, string> _vars = BuildVarMap();

    private static Dictionary<string, string> BuildVarMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                map[$"%{name}%"] = value.TrimEnd('\\', '/');
            }
        }

        Add("AppData", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add("LocalAppData", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Add("LocalLowAppData", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "..", "LocalLow"));
        Add("ProgramFiles", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add("ProgramFiles(x86)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add("ProgramFilesX86", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add("ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Add("CommonAppData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Add("UserProfile", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Add("SystemRoot", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add("WinDir", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add("System", Environment.GetFolderPath(Environment.SpecialFolder.System));
        Add("SystemX86", Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));
        Add("Temp", Path.GetTempPath().TrimEnd('\\', '/'));
        Add("Tmp", Path.GetTempPath().TrimEnd('\\', '/'));
        Add("SystemDrive", Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\') ?? "C:");

        return map;
    }

    public string ExpandVariables(string path)
    {
        foreach (var pair in _vars)
        {
            path = ReplaceIgnoreCase(path, pair.Key, pair.Value);
        }

        // 剩下的未知变量交给系统展开
        path = Environment.ExpandEnvironmentVariables(path);

        // BuildVarMap 会去掉盘符后的反斜杠，而 Windows 里裸 "C:" 表示该盘当前目录，
        // 不是根目录，这里补回分隔符，避免 %SystemDrive%\... 扫错位置
        if (path.Length == 2 && char.IsLetter(path[0]) && path[1] == ':')
        {
            path += Path.DirectorySeparatorChar;
        }

        return path;
    }

    /// <summary>把通配符路径展开成所有实际命中的目录。</summary>
    public List<string> ResolvePaths(string rawPath)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ResolveRecursive(ExpandVariables(rawPath), results);

        // %ProgramFiles% 可能同时存在 x86 版本，两个都试一遍
        if (rawPath.IndexOf("%ProgramFiles%", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var x86Path = ReplaceIgnoreCase(rawPath, "%ProgramFiles%", "%ProgramFiles(x86)%");
            ResolveRecursive(ExpandVariables(x86Path), results);
        }

        return new List<string>(results);
    }

    private static string ReplaceIgnoreCase(string source, string oldValue, string newValue)
    {
        var sb = new StringBuilder();
        int pos = 0;

        while (true)
        {
            int idx = source.IndexOf(oldValue, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                sb.Append(source, pos, source.Length - pos);
                break;
            }

            sb.Append(source, pos, idx - pos);
            sb.Append(newValue);
            pos = idx + oldValue.Length;
        }

        return sb.ToString();
    }

    private static void ResolveRecursive(string path, HashSet<string> results)
    {
        var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.None);

        int wcIdx = Array.FindIndex(parts, p => p.Contains('*') || p.Contains('?'));

        if (wcIdx < 0)
        {
            results.Add(path);
            return;
        }

        var basePath = wcIdx == 0
            ? Path.GetPathRoot(path) ?? ""
            : string.Join("\\", parts[..wcIdx]);

        if (!Directory.Exists(basePath))
        {
            return;
        }

        var wildcard = parts[wcIdx];
        var remaining = parts[(wcIdx + 1)..];

        try
        {
            var matches = remaining.Length == 0
                ? Directory.GetFileSystemEntries(basePath, wildcard)
                : Directory.GetDirectories(basePath, wildcard);

            foreach (var match in matches)
            {
                if (remaining.Length == 0)
                {
                    results.Add(match);
                }
                else
                {
                    ResolveRecursive(Path.Combine(match, string.Join("\\", remaining)), results);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }
}
