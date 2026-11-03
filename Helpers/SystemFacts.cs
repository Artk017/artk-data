using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WinUIDemo.Helpers;

/// <summary>
/// 从注册表 / Win32 读取这台机器的固定信息。
/// 走注册表而不是 WMI：不需要 System.Management，也不用等 CPU/GPU 查询，
/// 打开窗口就是即时结果。
/// </summary>
internal static class SystemFacts
{
    public static string DeviceName => Environment.MachineName;

    /// <summary>处理器型号，例如 "Intel(R) Core(TM) i7-12700H @ 2.30GHz"。</summary>
    public static string ProcessorName { get; } = ReadProcessor();

    /// <summary>已安装的物理内存，例如 "31.9 GB"。</summary>
    public static string MemoryText { get; } = ReadMemory();

    /// <summary>显卡型号，多块卡用「 / 」连接。</summary>
    public static string GraphicsText { get; } = ReadGraphics();

    /// <summary>系统名称 + 版本，例如 "Windows 11 专业版 24H2（Build 26100.2314）"。</summary>
    public static string OsText { get; } = ReadOs();

    private static string ReadProcessor()
    {
        const string path = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

        if (ReadRegistryValue(RegistryHive.LocalMachine, path, "ProcessorNameString") is { Length: > 0 } name)
        {
            return Squeeze(name);
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") is { Length: > 0 } identifier
            ? Squeeze(identifier)
            : "未知";
    }

    private static string ReadMemory()
    {
        if (!GetPhysicallyInstalledSystemMemory(out ulong kilobytes) || kilobytes == 0)
        {
            return "未知";
        }

        double gigabytes = kilobytes / 1024.0 / 1024.0;
        return gigabytes.ToString("0.0") + " GB";
    }

    private static string ReadGraphics()
    {
        const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        var names = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(classKey);
            if (key is null)
            {
                return "未知";
            }

            foreach (string subKeyName in key.GetSubKeyNames())
            {
                // 只有 0000 / 0001 … 这类四位数编号才是显卡实例
                if (subKeyName.Length != 4)
                {
                    continue;
                }

                using var adapter = key.OpenSubKey(subKeyName);
                if (adapter?.GetValue("DriverDesc") is not string description || description.Length == 0)
                {
                    continue;
                }

                description = Squeeze(description);
                if (!names.Contains(description))
                {
                    names.Add(description);
                }
            }
        }
        catch (Exception)
        {
            return "未知";
        }

        return names.Count == 0 ? "未知" : string.Join(" / ", names);
    }

    private static string ReadOs()
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        string product = ReadRegistryValue(RegistryHive.LocalMachine, path, "ProductName") ?? "Windows";
        string version = ReadRegistryValue(RegistryHive.LocalMachine, path, "DisplayVersion") ?? string.Empty;
        string build = ReadRegistryValue(RegistryHive.LocalMachine, path, "CurrentBuildNumber") ?? string.Empty;
        string revision = ReadRegistryValue(RegistryHive.LocalMachine, path, "UBR") ?? string.Empty;

        // Windows 11 上 ProductName 仍然写着 "Windows 10 ..."，按 build 号纠正
        if (int.TryParse(build, out int buildNumber) && buildNumber >= 22000)
        {
            product = product.Replace("Windows 10", "Windows 11");
        }

        var text = new StringBuilder(Squeeze(product));

        if (version.Length > 0)
        {
            text.Append(' ').Append(version);
        }

        if (build.Length > 0)
        {
            text.Append("（Build ").Append(build);
            if (revision.Length > 0)
            {
                text.Append('.').Append(revision);
            }

            text.Append('）');
        }

        return text.ToString();
    }

    private static string? ReadRegistryValue(RegistryHive hive, string path, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>注册表里的型号常带首尾空白和连续空格。</summary>
    private static string Squeeze(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool lastWasSpace = false;

        foreach (char c in value.Trim())
        {
            bool isSpace = c is ' ' or '\t';

            if (isSpace)
            {
                if (lastWasSpace)
                {
                    continue;
                }

                builder.Append(' ');
            }
            else
            {
                builder.Append(c);
            }

            lastWasSpace = isSpace;
        }

        return builder.ToString();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);
}
