using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace WinUIDemo.Services;

/// <summary>注册表根键映射，识别与清理阶段共用。</summary>
public static class CleanerRegistry
{
    public static RegistryKey? OpenHive(string hive) => hive switch
    {
        "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
        "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
        "HKU" or "HKEY_USERS" => Registry.Users,
        "HKCC" or "HKEY_CURRENT_CONFIG" => Registry.CurrentConfig,
        "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
        _ => null
    };
}

/// <summary>
/// 判断一条规则对应的应用是否安装。多个 Detect / DetectFile 之间是“或”，命中任意一个即可。
/// 没有识别条件的规则视为未安装，不会出现在界面上。
/// </summary>
public sealed class CleanerDetector
{
    private readonly CleanerPathExpander _expander = new();

    public bool IsInstalled(CleanerEntry entry)
    {
        if (entry.SpecialDetect is not null)
        {
            // 已知代号直接用结果；未知代号继续走 Detect / DetectFile
            if (TryCheckSpecialDetect(entry.SpecialDetect, out bool result))
            {
                return result;
            }
        }

        foreach (var reg in entry.DetectKeys)
        {
            if (CheckRegistry(reg))
            {
                return true;
            }
        }

        foreach (var file in entry.DetectFiles)
        {
            if (CheckFile(file))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CheckRegistry(string regPath)
    {
        try
        {
            var (hive, subKey, valueName) = SplitRegPath(regPath);
            using var key = CleanerRegistry.OpenHive(hive)?.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                return false;
            }

            return valueName is null || key.GetValue(valueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static (string Hive, string SubKey, string? ValueName) SplitRegPath(string path)
    {
        string regPath = path;
        string? valueName = null;

        var pipeIdx = path.LastIndexOf('|');
        if (pipeIdx >= 0)
        {
            regPath = path[..pipeIdx];
            valueName = path[(pipeIdx + 1)..];
        }

        var slashIdx = regPath.IndexOf('\\');
        var hive = slashIdx >= 0 ? regPath[..slashIdx].ToUpperInvariant() : regPath.ToUpperInvariant();
        var subKey = slashIdx >= 0 ? regPath[(slashIdx + 1)..] : "";
        return (hive, subKey, valueName);
    }

    private bool CheckFile(string rawPath)
    {
        try
        {
            foreach (var p in _expander.ResolvePaths(rawPath))
            {
                if (File.Exists(p) || Directory.Exists(p))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 路径不可访问就当没安装
        }

        return false;
    }

    /// <summary>SpecialDetect 是常见应用的简写，例如 DET_CHROME。</summary>
    private bool TryCheckSpecialDetect(string code, out bool result)
    {
        switch (code.ToUpperInvariant())
        {
            case "DET_CHROME":
                result = CheckFile(@"%LocalAppData%\Google\Chrome\User Data");
                return true;
            case "DET_FIREFOX":
                result = CheckFile(@"%AppData%\Mozilla\Firefox");
                return true;
            case "DET_IE":
                result = CheckRegistry(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\IEXPLORE.EXE");
                return true;
            case "DET_THUNDERBIRD":
                result = CheckFile(@"%AppData%\Thunderbird");
                return true;
            case "DET_OPERA":
                result = CheckFile(@"%AppData%\Opera Software\Opera Stable");
                return true;
            case "DET_EDGE":
                result = CheckFile(@"%LocalAppData%\Microsoft\Edge\User Data");
                return true;
            case "DET_WINSTORE":
                result = CheckFile(@"%LocalAppData%\Packages");
                return true;
            default:
                result = false;
                return false;
        }
    }
}
