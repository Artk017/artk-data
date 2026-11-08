using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace ArtkTools.Services;

/// <summary>
/// 「关闭请勿打扰」：把 Windows 11 通知页里的请勿打扰配置改到位。
///
/// 这些状态没有公开 API，都存在当前用户 CloudStore 的二进制 blob 里：
///   · 主开关 = 请勿打扰当前选中的配置（Unrestricted 表示关闭）
///   · 每条自动规则 = 一段 QuietMoment，blob 里带 C2 14 01 表示「已启用」
/// 改完注册表要重启当前用户的 WpnUserService，否则通知服务仍用旧状态。
/// </summary>
public static class QuietHours
{
    private const string CurrentStore =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current";

    private const string SettingsGroupSuffix = "$windows.data.donotdisturb.quiethourssettings";
    private const string SettingsValueName = "windows.data.donotdisturb.quiethourssettings";

    private const string MomentGroupSuffix = "$windows.data.donotdisturb.quietmoment$quietmomentlist";
    private const string MomentPrefix = "windows.data.donotdisturb.quietmoment$quietmoment";

    private const string ProfileUnrestricted = "Microsoft.QuietHoursProfile.Unrestricted";

    // CloudStore 的规则记录里，「已启用」多出来的三个字节
    private static readonly byte[] EnabledFlag = { 0xC2, 0x14, 0x01 };

    // 每条记录固定的起始标记，启用标志就插在它后面
    private static readonly byte[] RecordHead = { 0xC2, 0x0A, 0x01 };

    private static readonly byte[] ProfilePrefix = Encoding.Unicode.GetBytes("Microsoft.QuietHoursProfile.");

    /// <summary>四条自动规则对应的子键后缀（规则名见 Windows 设置里的「自动启用请勿打扰」）。</summary>
    private static readonly string[] Moments =
    {
        "presentation",
        "fullscreen",
        "postoobe",
        "game",
    };

    // ---------------- 对外 ----------------

    /// <summary>读取「关闭请勿打扰」当前是否已经生效。</summary>
    public static bool TryGetDisabled(out bool disabled)
    {
        disabled = false;

        string? settings = FindSettingsKey();
        if (settings is null)
        {
            return false;
        }

        byte[]? settingsBlob = ReadBlob(settings);
        if (settingsBlob is null || !TryGetProfile(settingsBlob, out string profile))
        {
            return false;
        }

        if (!string.Equals(profile, ProfileUnrestricted, StringComparison.Ordinal))
        {
            // 请勿打扰还开着，开关应当是关的
            return true;
        }

        // 主开关已是关闭：投影 / 全屏 / 功能更新后第一小时应为关，玩游戏时应为开
        foreach (string moment in Moments)
        {
            string? path = FindMomentKey(moment);
            if (path is null)
            {
                return false;
            }

            byte[]? blob = ReadBlob(path);
            if (blob is null)
            {
                return false;
            }

            bool enabled = HasEnabledFlag(blob);
            bool wantEnabled = moment == "game";

            if (enabled != wantEnabled)
            {
                disabled = false;
                return true;
            }
        }

        disabled = true;
        return true;
    }

    /// <summary>
    /// 应用「关闭请勿打扰」：关掉请勿打扰主开关、投影 / 全屏 / 功能更新后一小时，
    /// 只保留玩游戏时自动开启；关闭该开关则恢复这几条自动规则。
    /// </summary>
    public static bool SetDisabled(bool disabled, out string error)
    {
        if (disabled)
        {
            if (!WriteSettingsProfile(ProfileUnrestricted, out error))
            {
                return false;
            }

            foreach (string moment in Moments)
            {
                bool enable = moment == "game";
                if (!WriteMomentEnabled(moment, enable, out error))
                {
                    return false;
                }
            }
        }
        else
        {
            // 关闭「关闭请勿打扰」= 恢复 Windows 的自动请勿打扰规则
            foreach (string moment in Moments)
            {
                if (!WriteMomentEnabled(moment, true, out error))
                {
                    return false;
                }
            }
        }

        error = string.Empty;
        RestartNotificationService();
        return true;
    }

    // ---------------- 注册表定位 ----------------

    private static string? FindSettingsKey()
    {
        string? group = FindGroup(SettingsGroupSuffix);
        if (group is null)
        {
            return null;
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(group);
        if (key is null)
        {
            return null;
        }

        foreach (string name in key.GetSubKeyNames())
        {
            if (string.Equals(name, SettingsValueName, StringComparison.OrdinalIgnoreCase))
            {
                return group + "\\" + name;
            }
        }

        return null;
    }

    private static string? FindMomentKey(string moment)
    {
        string? group = FindGroup(MomentGroupSuffix);
        if (group is null)
        {
            return null;
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(group);
        if (key is null)
        {
            return null;
        }

        string wanted = MomentPrefix + moment;
        foreach (string name in key.GetSubKeyNames())
        {
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return group + "\\" + name;
            }
        }

        return null;
    }

    private static string? FindGroup(string suffix)
    {
        using RegistryKey? store = Registry.CurrentUser.OpenSubKey(CurrentStore);
        if (store is null)
        {
            return null;
        }

        foreach (string name in store.GetSubKeyNames())
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return CurrentStore + "\\" + name;
            }
        }

        return null;
    }

    // ---------------- blob 读写 ----------------

    private static byte[]? ReadBlob(string path)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue("Data") is byte[] data ? data : null;
    }

    private static bool WriteBlob(string path, byte[] data)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, writable: true);
        if (key is null)
        {
            return false;
        }

        key.SetValue("Data", data, RegistryValueKind.Binary);
        return true;
    }

    private static bool TryGetProfile(byte[] blob, out string profile)
    {
        int start = IndexOf(blob, ProfilePrefix);
        if (start < 0)
        {
            profile = string.Empty;
            return false;
        }

        int end = StringEnd(blob, start + ProfilePrefix.Length);
        profile = Encoding.Unicode.GetString(blob, start, end - start);
        return true;
    }

    /// <summary>把选中的配置换成另一个（长度不同时一并改掉前面的字符数）。</summary>
    private static byte[]? ReplaceProfile(byte[] blob, string profile)
    {
        int start = IndexOf(blob, ProfilePrefix);
        if (start < 1)
        {
            return null;
        }

        int end = StringEnd(blob, start + ProfilePrefix.Length);
        var result = new List<byte>(blob.Length);

        // start-1 是字符串的字符数，跟着一起换
        for (int i = 0; i < start - 1; i++)
        {
            result.Add(blob[i]);
        }

        result.Add((byte)profile.Length);
        result.AddRange(Encoding.Unicode.GetBytes(profile));

        for (int i = end; i < blob.Length; i++)
        {
            result.Add(blob[i]);
        }

        return result.ToArray();
    }

    /// <summary>从字符串起点开始，一直读到 UTF-16LE 里不再是 ASCII 字符为止。</summary>
    private static int StringEnd(byte[] blob, int from)
    {
        int index = from;

        while (index + 1 < blob.Length && blob[index] >= 0x20 && blob[index] < 0x7F && blob[index + 1] == 0)
        {
            index += 2;
        }

        return index;
    }

    private static bool HasEnabledFlag(byte[] blob)
    {
        int head = IndexOf(blob, RecordHead);
        if (head < 0 || head + RecordHead.Length + EnabledFlag.Length > blob.Length)
        {
            return false;
        }

        for (int i = 0; i < EnabledFlag.Length; i++)
        {
            if (blob[head + RecordHead.Length + i] != EnabledFlag[i])
            {
                return false;
            }
        }

        return true;
    }

    private static byte[]? SetEnabledFlag(byte[] blob, bool enabled)
    {
        int head = IndexOf(blob, RecordHead);
        if (head < 0)
        {
            return null;
        }

        bool hasFlag = HasEnabledFlag(blob);
        if (hasFlag == enabled)
        {
            return blob;
        }

        var result = new List<byte>(blob.Length + EnabledFlag.Length);

        if (enabled)
        {
            for (int i = 0; i <= head + RecordHead.Length - 1; i++)
            {
                result.Add(blob[i]);
            }

            result.AddRange(EnabledFlag);

            for (int i = head + RecordHead.Length; i < blob.Length; i++)
            {
                result.Add(blob[i]);
            }
        }
        else
        {
            int flag = head + RecordHead.Length;

            for (int i = 0; i < blob.Length; i++)
            {
                if (i >= flag && i < flag + EnabledFlag.Length)
                {
                    continue;
                }

                result.Add(blob[i]);
            }
        }

        return result.ToArray();
    }

    private static int IndexOf(byte[] source, byte[] pattern)
    {
        for (int i = 0; i <= source.Length - pattern.Length; i++)
        {
            bool match = true;

            for (int j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    // ---------------- 写入 ----------------

    private static bool WriteSettingsProfile(string profile, out string error)
    {
        string? path = FindSettingsKey();
        if (path is null)
        {
            error = "系统里找不到请勿打扰的配置项";
            return false;
        }

        byte[]? blob = ReadBlob(path);
        if (blob is null)
        {
            error = "读取请勿打扰配置失败";
            return false;
        }

        byte[]? updated = ReplaceProfile(blob, profile);
        if (updated is null || !WriteBlob(path, updated))
        {
            error = "写入请勿打扰配置失败";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool WriteMomentEnabled(string moment, bool enabled, out string error)
    {
        string? path = FindMomentKey(moment);
        if (path is null)
        {
            error = "系统里找不到这条自动勿扰规则";
            return false;
        }

        byte[]? blob = ReadBlob(path);
        if (blob is null)
        {
            error = "读取自动勿扰规则失败";
            return false;
        }

        byte[]? updated = SetEnabledFlag(blob, enabled);
        if (updated is null)
        {
            error = "这条自动勿扰规则的结构无法识别";
            return false;
        }

        if (!WriteBlob(path, updated))
        {
            error = "写入自动勿扰规则失败";
            return false;
        }

        error = string.Empty;
        return true;
    }

    // ---------------- 通知服务 ----------------

    /// <summary>
    /// 重启当前用户的 WpnUserService，让改动立刻生效。失败也不影响注册表已写入，
    /// 只是可能要等下次登录才刷新，所以这里不往外报错。
    /// </summary>
    private static void RestartNotificationService()
    {
        string? name = FindNotificationServiceName();
        if (name is null)
        {
            return;
        }

        RunSc("stop", name);

        for (int i = 0; i < 30; i++)
        {
            string state = RunSc("query", name);
            if (state.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            Thread.Sleep(150);
        }

        RunSc("start", name);
    }

    private static string? FindNotificationServiceName()
    {
        try
        {
            using RegistryKey? services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null)
            {
                return null;
            }

            foreach (string name in services.GetSubKeyNames())
            {
                if (name.StartsWith("WpnUserService_", StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }
        catch
        {
            // 读不到就算了
        }

        return null;
    }

    private static string RunSc(string verb, string name)
    {
        try
        {
            var info = new ProcessStartInfo("sc.exe", $"{verb} {name}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using Process? process = Process.Start(info);
            if (process is null)
            {
                return string.Empty;
            }

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }
}
