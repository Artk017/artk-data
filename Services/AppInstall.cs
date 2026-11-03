using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinUIDemo.Services;

/// <summary>
/// 安装相关的两件事：
///   1) 识别某个软件是否已安装（扫卸载注册表，看 DisplayName）；
///   2) 静默安装下载好的安装包（.msi 用 msiexec /qn；.exe 尽力用 /S），并等到装完。
/// 「无感安装」只对支持静默参数的安装包有效；不支持时调用方回退到手动运行。
/// </summary>
public static class AppInstall
{
    private static readonly string[] UninstallRoots =
    {
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
        @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    private static readonly (RegistryHive Hive, string Root)[] Locations =
    {
        (RegistryHive.CurrentUser, UninstallRoots[0]),
        (RegistryHive.LocalMachine, UninstallRoots[0]),
        (RegistryHive.LocalMachine, UninstallRoots[1]),
    };

    /// <summary>该软件是否已安装。</summary>
    public static bool IsInstalled(string appName) => FindInstalledName(appName) is not null;

    /// <summary>扫卸载注册表，返回命中的 DisplayName；没有返回 null。</summary>
    public static string? FindInstalledName(string appName)
    {
        string target = Normalize(appName);
        if (target.Length == 0)
        {
            return null;
        }

        // 用「整名」和「剥掉位数 / 驱动等后缀」的几个候选键去比，
        // 这样清单写「PotPlayer 64 位」「HECATE驱动」也能对上显示名为 PotPlayer / HECATE 的软件。
        List<string> keys = BuildKeys(target);

        foreach ((RegistryHive hive, string root) in Locations)
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using RegistryKey? uninstall = baseKey.OpenSubKey(root);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (string sub in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? entry = uninstall.OpenSubKey(sub);
                    if (entry?.GetValue("DisplayName") is not string display || display.Length == 0)
                    {
                        continue;
                    }

                    string normalized = Normalize(display);
                    foreach (string key in keys)
                    {
                        if (MatchKey(normalized, key))
                        {
                            return display;
                        }
                    }
                }
            }
            catch
            {
                // 某个 hive 读不了就跳过
            }
        }

        return null;
    }

    /// <summary>找该软件的主程序 exe（读卸载项的 DisplayIcon / InstallLocation），取不到返回 null。用于显示图标。</summary>
    public static string? FindExecutable(string appName)
    {
        string target = Normalize(appName);
        if (target.Length == 0)
        {
            return null;
        }

        List<string> keys = BuildKeys(target);

        foreach ((RegistryHive hive, string root) in Locations)
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using RegistryKey? uninstall = baseKey.OpenSubKey(root);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (string sub in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? entry = uninstall.OpenSubKey(sub);
                    if (entry?.GetValue("DisplayName") is not string display || display.Length == 0)
                    {
                        continue;
                    }

                    string normalized = Normalize(display);
                    bool hit = false;

                    foreach (string key in keys)
                    {
                        if (MatchKey(normalized, key))
                        {
                            hit = true;
                            break;
                        }
                    }

                    if (!hit)
                    {
                        continue;
                    }

                    string? exe = ResolveExe(entry);
                    if (exe is not null)
                    {
                        return exe;
                    }
                }
            }
            catch
            {
                // 某个 hive 读不了就跳过
            }
        }

        return null;
    }

    private static string? ResolveExe(RegistryKey entry)
    {
        // DisplayIcon 常见形式："C:\path\app.exe,0" 或带引号
        if (entry.GetValue("DisplayIcon") is string icon && icon.Length > 0)
        {
            string path = CleanIconPath(icon);
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                return path;
            }
        }

        if (entry.GetValue("InstallLocation") is string location && location.Length > 0)
        {
            string dir = location.Trim().Trim('"');

            if (Directory.Exists(dir))
            {
                try
                {
                    string[] exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
                    string? first = null;

                    foreach (string exe in exes)
                    {
                        string name = Path.GetFileNameWithoutExtension(exe);

                        if (name.Contains("unins", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        first ??= exe;

                        if (name.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("player", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("game", StringComparison.OrdinalIgnoreCase))
                        {
                            return exe;
                        }
                    }

                    if (first is not null)
                    {
                        return first;
                    }
                }
                catch
                {
                    // 目录读不了就算了
                }
            }
        }

        return null;
    }

    private static string CleanIconPath(string icon)
    {
        string path = icon.Trim();
        int comma = path.LastIndexOf(',');
        if (comma > 2)
        {
            path = path[..comma];
        }

        return path.Trim().Trim('"');
    }

    /// <summary>
    /// 静默安装。成功（进程正常退出或轮询到已安装）返回 true。
    /// 超时（多半是弹了 GUI 等用户）或失败返回 false，交回调用方处理。
    /// <paramref name="installDir"/> 非空时把目标目录传给安装器（NSIS /D=、Inno /DIR=、MSI INSTALLDIR）。
    /// </summary>
    public static async Task<bool> InstallAsync(string installerPath, string appName, string? installDir = null, TimeSpan? timeout = null)
    {
        if (!File.Exists(installerPath))
        {
            return false;
        }

        string? dir = string.IsNullOrWhiteSpace(installDir) ? null : installDir!.Trim().TrimEnd('\\', '/');
        if (dir is not null)
        {
            try
            {
                // 子目录可能还不存在，静默安装前先建好
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // 建不了就让安装器自己去建
            }
        }

        string ext = Path.GetExtension(installerPath).ToLowerInvariant();

        ProcessStartInfo psi;
        if (ext == ".msi")
        {
            string args = $"/i \"{installerPath}\" /qn /norestart";
            if (dir is not null)
            {
                args += $" INSTALLDIR=\"{dir}\" TARGETDIR=\"{dir}\" APPLICATIONFOLDER=\"{dir}\"";
            }

            psi = new ProcessStartInfo("msiexec.exe", args) { UseShellExecute = false, CreateNoWindow = true };
        }
        else
        {
            // NSIS：/S 静默、/D= 指定目录（必须放最后且不加引号）；
            // Inno：/VERYSILENT 静默、/DIR="..." 指定目录。
            // 两套参数一起给，各家会忽略自己不认识的那些，尽量兼容更多安装器。
            string args = dir is null
                ? "/S /VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
                : $"/S /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"{dir}\" /D={dir}";

            psi = new ProcessStartInfo(installerPath, args) { UseShellExecute = true };
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch
        {
            return false;
        }

        if (process is null)
        {
            return false;
        }

        using (process)
        {
            DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));
            bool sawInstalled = false;

            while (!process.HasExited && DateTime.UtcNow < deadline)
            {
                if (IsInstalled(appName))
                {
                    // 注册表已经写进来了，但安装器通常还在复制文件；
                    // 继续等它退出，文件解锁后调用方才能安全删掉安装包。
                    sawInstalled = true;
                }

                await Task.Delay(500).ConfigureAwait(false);
            }

            if (process.HasExited)
            {
                return process.ExitCode == 0 || sawInstalled || IsInstalled(appName);
            }

            // 到点了进程还没退：多半弹了 GUI 在等用户，交回界面，别强杀。
            return sawInstalled || IsInstalled(appName);
        }
    }

    /// <summary>清单名里常带的「位数 / 驱动」之类后缀，比对前先剥掉。</summary>
    private static readonly string[] Suffixes = { "64位", "32位", "64bit", "32bit", "驱动" };

    /// <summary>整名 + 逐级剥掉后缀的候选键（去重、保序）。</summary>
    private static List<string> BuildKeys(string normalizedTarget)
    {
        var keys = new List<string> { normalizedTarget };
        var seen = new HashSet<string>(StringComparer.Ordinal) { normalizedTarget };

        string current = normalizedTarget;
        bool trimmed = true;

        while (trimmed)
        {
            trimmed = false;

            foreach (string suffix in Suffixes)
            {
                if (current.Length > suffix.Length && current.EndsWith(suffix, StringComparison.Ordinal))
                {
                    current = current[..^suffix.Length];
                    if (seen.Add(current))
                    {
                        keys.Add(current);
                    }

                    trimmed = true;
                    break;
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// 单个候选键是否命中显示名：
    ///   - 完全相同；或
    ///   - 足够长（≥6）时，显示名包含该键（覆盖显示名带版本号 / 厂商前缀的情况）。
    /// 不做「显示名更短」方向的反向包含，避免 NVIDIA 驱动被当成 NVIDIA App 之类误判。
    /// </summary>
    private static bool MatchKey(string normalizedDisplay, string key)
    {
        if (normalizedDisplay == key)
        {
            return true;
        }

        return key.Length >= 6 && normalizedDisplay.Contains(key, StringComparison.Ordinal);
    }

    // ---------------- 安装子目录名（只用 ASCII，避免中文路径出问题） ----------------

    /// <summary>常见中文名软件映射到 ASCII 目录名。</summary>
    private static readonly Dictionary<string, string> FolderAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["微信"] = "WeChat",
        ["QQ音乐"] = "QQMusic",
        ["网易云音乐"] = "NetEaseCloudMusic",
        ["奇游电竞加速器"] = "QiYou",
        ["KOOK"] = "KOOK",
        ["QQ"] = "QQ",
        ["Steam"] = "Steam",
        ["Bandizip"] = "Bandizip",
        ["PotPlayer 64 位"] = "PotPlayer",
        ["Visual Studio Code"] = "VSCode",
        ["NVIDIA App"] = "NVIDIAApp",
        ["Logitech G HUB"] = "LogitechGHUB",
        ["HECATE驱动"] = "HECATE",
    };

    /// <summary>
    /// 在基目录下按「一个软件一个子文件夹」拼出安装目录，例如
    /// base = G:\dow\6666、软件 = 网易云音乐 → G:\dow\6666\NetEaseCloudMusic。
    /// 子文件夹名只保留 ASCII，避免中文路径带来的兼容性问题。
    /// </summary>
    public static string BuildInstallDir(string baseDir, string appName, string? id)
    {
        return Path.Combine(baseDir.Trim().TrimEnd('\\', '/'), SafeFolderName(appName, id));
    }

    private static string SafeFolderName(string appName, string? id)
    {
        string name = (appName ?? string.Empty).Trim();

        if (name.Length > 0 && FolderAliases.TryGetValue(name, out string? alias) && alias.Length > 0)
        {
            return alias;
        }

        // 服务端条目通常有英文 slug 的 Id，优先用它
        string candidate = !string.IsNullOrWhiteSpace(id) && IsAscii(id!.Trim()) ? id!.Trim() : name;

        var sb = new StringBuilder(candidate.Length);
        foreach (char c in candidate)
        {
            if (c < 128 && (char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                sb.Append(c);
            }

            // 空格、中文及其它符号都丢掉
        }

        string clean = sb.ToString().Trim('.', '-', '_');
        return clean.Length == 0 ? "App" : clean;
    }

    private static bool IsAscii(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        foreach (char c in s)
        {
            if (c >= 128)
            {
                return false;
            }
        }

        return true;
    }

    private static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }
}
