namespace NovaSetup;

/// <summary>桌面快捷方式的增删（通过 WScript.Shell COM，避免额外依赖）。</summary>
internal static class Shortcuts
{
    private static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    private static string? PublicDesktop
    {
        get
        {
            string p = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            return string.IsNullOrEmpty(p) ? null : p;
        }
    }

    private static IEnumerable<string> DesktopDirs()
    {
        yield return UserDesktop;

        string? pub = PublicDesktop;
        if (!string.IsNullOrEmpty(pub) && !string.Equals(pub, UserDesktop, StringComparison.OrdinalIgnoreCase))
        {
            yield return pub;
        }
    }

    /// <summary>找到桌面上指向安装目录内的所有快捷方式。</summary>
    public static List<string> FindDesktopShortcuts(string installDir)
    {
        var result = new List<string>();
        string root = installDir.TrimEnd('\\', '/');

        foreach (string dir in DesktopDirs())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string lnk in Directory.EnumerateFiles(dir, "*.lnk"))
            {
                string? target = TryGetTarget(lnk);
                if (!string.IsNullOrEmpty(target) &&
                    target!.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(lnk);
                }
            }
        }

        return result;
    }

    public static void RemoveDesktopShortcuts(string installDir)
    {
        foreach (string lnk in FindDesktopShortcuts(installDir))
        {
            try
            {
                File.Delete(lnk);
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>确保桌面有指向该程序的快捷方式（Velopack 已建则跳过）。</summary>
    public static void EnsureDesktopShortcut(string installDir, string exe)
    {
        if (!File.Exists(exe))
        {
            return;
        }

        if (FindDesktopShortcuts(installDir).Count > 0)
        {
            return;
        }

        string lnkPath = Path.Combine(UserDesktop, ProductInfo.Current.ShortcutName);
        try
        {
            CreateShortcut(lnkPath, exe, Path.GetDirectoryName(exe)!, ProductInfo.Current.DisplayName);
        }
        catch
        {
            // 忽略
        }
    }

    private static string? TryGetTarget(string lnkPath)
    {
        try
        {
            Type? type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            return (string)shortcut.TargetPath;
        }
        catch
        {
            return null;
        }
    }

    private static void CreateShortcut(string lnkPath, string target, string workingDir, string description)
    {
        Type type = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell 不可用");

        dynamic shell = Activator.CreateInstance(type)!;
        dynamic shortcut = shell.CreateShortcut(lnkPath);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = workingDir;
        shortcut.IconLocation = target + ",0";
        shortcut.Description = description;
        shortcut.Save();
    }

    /// <summary>
    /// 改名（Nova → Artk Tools / NovaServer → Artk Server）后清掉遗留的旧快捷方式。
    /// 只删快捷方式，不动安装目录里的任何文件。
    /// </summary>
    public static void RemoveLegacyShortcuts()
    {
        foreach (string dir in DesktopDirs())
        {
            TryDeleteFile(Path.Combine(dir, "Nova.lnk"));
            TryDeleteFile(Path.Combine(dir, "NovaServer.lnk"));
        }

        foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            string programs = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(programs))
            {
                continue;
            }

            TryDeleteFile(Path.Combine(programs, "Nova.lnk"));
            TryDeleteFile(Path.Combine(programs, "Nova", "Nova.lnk"));
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 快捷方式删不掉不影响安装
        }
    }
}
