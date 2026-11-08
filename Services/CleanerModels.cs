using System;
using System.Collections.Generic;

namespace ArtkTools.Services;

/// <summary>FileKey 的清理方式。</summary>
public enum FileKeyFlag
{
    /// <summary>只扫顶层文件。</summary>
    None,

    /// <summary>递归扫描所有子目录。</summary>
    Recurse,

    /// <summary>递归扫描，并在清理后删除空目录。</summary>
    RemoveSelf
}

/// <summary>ExcludeKey 保护的对象类型。</summary>
public enum ExcludeType
{
    File,
    Path,
    Reg
}

/// <summary>Winapp2.ini 中的一条清理规则（一个应用/项目）。</summary>
public sealed class CleanerEntry
{
    public string Name { get; set; } = "";

    /// <summary>CCleaner 分类代码，如 3025 = Windows。</summary>
    public int? LangSecRef { get; set; }

    /// <summary>用于判断“是否安装”的注册表路径（任一命中即算安装）。</summary>
    public List<string> DetectKeys { get; set; } = new();

    /// <summary>用于判断“是否安装”的文件路径（任一命中即算安装）。</summary>
    public List<string> DetectFiles { get; set; } = new();

    /// <summary>已知应用的识别代号，例如 DET_CHROME。</summary>
    public string? SpecialDetect { get; set; }

    public List<FileKeyEntry> FileKeys { get; set; } = new();

    public List<RegKeyEntry> RegKeys { get; set; } = new();

    public List<ExcludeKeyEntry> ExcludeKeys { get; set; } = new();

    /// <summary>规则自带的风险提示。</summary>
    public string? Warning { get; set; }

    /// <summary>默认是否勾选。</summary>
    public bool Default { get; set; } = true;

    /// <summary>匹配到具体分类（中文名 + 排序号）。</summary>
    public CleanerCategory Category { get; set; } = new("其他应用", 2000);
}

/// <summary>分类信息。</summary>
public sealed record CleanerCategory(string Name, int Order);

/// <summary>FileKey 解析结果：目录 + 文件过滤 + 递归方式。</summary>
public sealed class FileKeyEntry
{
    /// <summary>要扫描的目录，允许 %环境变量% 与路径段通配符。</summary>
    public string Path { get; set; } = "";

    /// <summary>分号分隔的文件名过滤，默认 *.*。</summary>
    public string Pattern { get; set; } = "*.*";

    public FileKeyFlag Flag { get; set; } = FileKeyFlag.None;

    public static FileKeyEntry Parse(string value)
    {
        var parts = value.Split('|');
        var entry = new FileKeyEntry { Path = parts[0].Trim() };

        if (parts.Length == 2)
        {
            var p = parts[1].Trim().ToUpperInvariant();
            if (p is "RECURSE" or "REMOVESELF")
            {
                entry.Flag = p == "RECURSE" ? FileKeyFlag.Recurse : FileKeyFlag.RemoveSelf;
            }
            else
            {
                entry.Pattern = parts[1].Trim();
            }
        }
        else if (parts.Length > 2)
        {
            entry.Pattern = parts[1].Trim();
            entry.Flag = parts[2].Trim().ToUpperInvariant() switch
            {
                "RECURSE" => FileKeyFlag.Recurse,
                "REMOVESELF" => FileKeyFlag.RemoveSelf,
                _ => FileKeyFlag.None
            };
        }

        return entry;
    }
}

/// <summary>RegKey 解析结果：整棵子树或单个值。</summary>
public sealed class RegKeyEntry
{
    public string KeyPath { get; set; } = "";

    /// <summary>为空表示删除整个键，否则只删这个值。</summary>
    public string? ValueName { get; set; }

    public static RegKeyEntry Parse(string value)
    {
        var idx = value.IndexOf('|');
        if (idx >= 0)
        {
            return new RegKeyEntry
            {
                KeyPath = value[..idx].Trim(),
                ValueName = value[(idx + 1)..].Trim()
            };
        }

        return new RegKeyEntry { KeyPath = value.Trim() };
    }
}

/// <summary>ExcludeKey 解析结果。</summary>
public sealed class ExcludeKeyEntry
{
    public ExcludeType Type { get; set; } = ExcludeType.File;

    public string Path { get; set; } = "";

    /// <summary>目录内的文件名/通配符；为空表示整棵目录都被保护。</summary>
    public string? Pattern { get; set; }

    public static ExcludeKeyEntry Parse(string value)
    {
        var parts = value.Split('|');
        var entry = new ExcludeKeyEntry();

        if (parts.Length > 0)
        {
            entry.Type = parts[0].Trim().ToUpperInvariant() switch
            {
                "FILE" => ExcludeType.File,
                "PATH" => ExcludeType.Path,
                "REG" => ExcludeType.Reg,
                _ => ExcludeType.File
            };
        }

        if (parts.Length > 1)
        {
            entry.Path = parts[1].Trim();
        }

        if (parts.Length > 2)
        {
            entry.Pattern = parts[2].Trim();
        }

        return entry;
    }
}

/// <summary>一个项目的扫描结果，扫描完成后再交给清理阶段删除。</summary>
public sealed class ScanResult
{
    public CleanerEntry Entry { get; set; } = null!;

    public List<string> FilesToDelete { get; set; } = new();

    public List<RegistryItemToDelete> RegistryToDelete { get; set; } = new();

    public long TotalBytes { get; set; }

    public string FormattedSize => FormatBytes(TotalBytes);

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024L * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }

        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

/// <summary>待删除的注册表项：KeyPath 整键，或 KeyPath 下指定的 ValueName。</summary>
public sealed class RegistryItemToDelete
{
    public string KeyPath { get; set; } = "";

    public string? ValueName { get; set; }

    public override string ToString() =>
        ValueName is not null ? $"{KeyPath} → {ValueName}" : KeyPath;
}
