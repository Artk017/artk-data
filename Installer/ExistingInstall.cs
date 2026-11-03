using System.IO;
using Microsoft.Win32;

namespace NovaSetup;

internal sealed record ExistingInstall(string InstallDir, string? Version);

/// <summary>
/// 查找已安装的同一个产品（按 Velopack 的 packId 找卸载项，在 HKCU/HKLM 的 Uninstall 下）。
/// 找到后安装程序会更新原有目录，而不是装第二份。
/// </summary>
internal static class ExistingInstallDetector
{
    private static string PackId => ProductInfo.Current.PackId;

    private static readonly string[] SubKeys =
    {
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
        @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    private static readonly RegistryHive[] Hives = { RegistryHive.CurrentUser, RegistryHive.LocalMachine };

    public static ExistingInstall? Find()
    {
        foreach (RegistryHive hive in Hives)
        {
            foreach (string sub in SubKeys)
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                    using RegistryKey? uninstall = baseKey.OpenSubKey(sub);
                    using RegistryKey? app = uninstall?.OpenSubKey(PackId);
                    if (app is null)
                    {
                        continue;
                    }

                    string? version = app.GetValue("DisplayVersion") as string;
                    string? location = app.GetValue("InstallLocation") as string;
                    string? uninstallString = app.GetValue("UninstallString") as string;

                    string? dir = TryDir(location) ?? TryDirFromUninstall(uninstallString);
                    if (dir is not null)
                    {
                        return new ExistingInstall(dir, version);
                    }
                }
                catch
                {
                    // 某个 hive 读不到就跳过
                }
            }
        }

        return null;
    }

    private static string? TryDir(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(location.Trim());
            return File.Exists(Path.Combine(full, "Update.exe")) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryDirFromUninstall(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString))
        {
            return null;
        }

        string raw = uninstallString.Trim();
        string exe;

        if (raw.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = raw.IndexOf('"', 1);
            exe = end > 1 ? raw[1..end] : raw.Trim('"');
        }
        else
        {
            int space = raw.IndexOf(' ');
            exe = space > 0 ? raw[..space] : raw;
        }

        try
        {
            string? dir = Path.GetDirectoryName(exe);
            return dir is not null && File.Exists(Path.Combine(dir, "Update.exe")) ? dir : null;
        }
        catch
        {
            return null;
        }
    }
}
