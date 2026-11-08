using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ArtkTools.Services;

/// <summary>
/// 解析 Winapp2.ini。格式类似 INI，但多项值用编号键：
/// FileKey1=... / FileKey2=... / Detect / Detect1 / ExcludeKey1 ...
/// </summary>
public sealed class CleanerParser
{
    private static readonly Regex RxFileKey = new(@"^FileKey\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxRegKey = new(@"^RegKey\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxExcludeKey = new(@"^ExcludeKey\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxDetect = new(@"^Detect\d*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxDetectFile = new(@"^DetectFile\d*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<CleanerEntry> Parse(string content)
    {
        var entries = new List<CleanerEntry>();
        CleanerEntry? current = null;

        // 同时按 \r 和 \n 切分：数据库里有 CRLF，也有只带 \r 的行
        foreach (var rawLine in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                if (current is not null && IsValid(current))
                {
                    entries.Add(current);
                }

                var name = line[1..^1].Trim();

                // 跳过数据库自身的头部块
                if (name.StartsWith("Winapp2", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("version", StringComparison.OrdinalIgnoreCase))
                {
                    current = null;
                    continue;
                }

                // 去掉社区条目标记的尾部 " *"
                current = new CleanerEntry { Name = name.TrimEnd('*').TrimEnd() };
                continue;
            }

            if (current is null)
            {
                continue;
            }

            var eqIdx = line.IndexOf('=');
            if (eqIdx < 0)
            {
                continue;
            }

            var key = line[..eqIdx].Trim();
            var value = line[(eqIdx + 1)..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (key.Equals("LangSecRef", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var n))
                {
                    current.LangSecRef = n;
                }
            }
            else if (key.Equals("SpecialDetect", StringComparison.OrdinalIgnoreCase))
            {
                current.SpecialDetect = value;
            }
            else if (key.Equals("Warning", StringComparison.OrdinalIgnoreCase))
            {
                current.Warning = value;
            }
            else if (key.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                current.Default = value.Equals("True", StringComparison.OrdinalIgnoreCase);
            }
            else if (RxDetect.IsMatch(key))
            {
                current.DetectKeys.Add(value);
            }
            else if (RxDetectFile.IsMatch(key))
            {
                current.DetectFiles.Add(value);
            }
            else if (RxFileKey.IsMatch(key))
            {
                current.FileKeys.Add(FileKeyEntry.Parse(value));
            }
            else if (RxRegKey.IsMatch(key))
            {
                current.RegKeys.Add(RegKeyEntry.Parse(value));
            }
            else if (RxExcludeKey.IsMatch(key))
            {
                current.ExcludeKeys.Add(ExcludeKeyEntry.Parse(value));
            }
        }

        if (current is not null && IsValid(current))
        {
            entries.Add(current);
        }

        return entries;
    }

    /// <summary>有识别条件、也有东西可清，才算一条有效规则。</summary>
    private static bool IsValid(CleanerEntry e) =>
        (e.DetectKeys.Count > 0 || e.DetectFiles.Count > 0 || e.SpecialDetect is not null) &&
        (e.FileKeys.Count > 0 || e.RegKeys.Count > 0);

    public async Task<List<CleanerEntry>> ParseFileAsync(string filePath)
    {
        using var reader = new StreamReader(filePath);
        var content = await reader.ReadToEndAsync();
        return Parse(content);
    }
}
