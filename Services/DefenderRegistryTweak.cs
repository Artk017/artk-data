using System;
using System.Collections.Generic;
using System.Security.Principal;
using Microsoft.Win32;

namespace ArtkTools.Services;

/// <summary>
/// 「关闭防病毒(注册表)」开关：不开组策略界面，直接按表往注册表里写/删一整组配置。
/// <para>
/// 开 = 按下表创建目录、写值（该改的改）；关 = 删掉这些值，<b>只有我们自己建的目录才顺带删掉</b>
/// （目录里如果还有别人写的东西就保留，不连坐），CI 那条不是删值而是把值改回 2。
/// </para>
/// <para>
/// 和「关闭防病毒」那条开关有重叠（都碰 <c>DisableAntiSpyware</c> 和 <c>ServiceKeepAlive</c>），
/// 关掉这个会把那边需要的前置值一起删掉，所以那边会跟着显示为关，这是预期行为。
/// </para>
/// </summary>
public static class DefenderRegistryTweak
{
    /// <summary>提权实例的命令行开关，后面跟 <c>0</c>（删）/ <c>1</c>（写）。</summary>
    public const string ElevationSwitch = "--set-defender-reg";

    private const string DefenderRoot = @"SOFTWARE\Policies\Microsoft\Windows Defender";

    /// <summary>一条要写的配置：哪个键、什么值名、写什么、关掉时怎么恢复。</summary>
    private sealed class Entry
    {
        public required string Path { get; init; }

        public required string Name { get; init; }

        public required object Value { get; init; }

        public RegistryValueKind Kind { get; init; } = RegistryValueKind.DWord;

        /// <summary>关掉时要写回的值；null 表示直接删掉这个值。</summary>
        public object? Restore { get; init; }

        /// <summary>这个目录是我们建的，关掉时若已被清空就一并删掉。</summary>
        public bool DeleteKeyWhenEmpty { get; init; }

        /// <summary>这个值本身是组策略项：还要同步写本地组策略文件，否则 gpedit 里显示「未配置」。</summary>
        public bool IsGpoPolicy { get; init; }

        public bool Matches(RegistryKey key)
        {
            object? actual = key.GetValue(Name);
            if (actual is null)
            {
                return false;
            }

            return Kind == RegistryValueKind.String
                ? string.Equals(Convert.ToString(actual), Convert.ToString(Value), StringComparison.Ordinal)
                : Convert.ToInt32(actual) == Convert.ToInt32(Value);
        }
    }

    private static readonly Entry[] Settings =
    {
        // 1. Windows Defender 根键：Defender 的几个总开关
        Root("DisableAntiSpyware", 1),
        Root("DisableRealtimeMonitoring", 1),
        Root("DisableAntiVirus", 1),
        Root("DisableSpecialRunningModes", 1),
        Root("DisableRoutinelyTakingAction", 1),
        Root("ServiceKeepAlive", 0),

        // 2. Real-Time Protection 目录（我们建的，关掉时删目录）
        Sub("Real-Time Protection", "DisableBehaviorMonitoring", 1),
        Sub("Real-Time Protection", "DisableOnAccessProtection", 1),
        Sub("Real-Time Protection", "DisableScanOnRealtimeEnable", 1),
        Sub("Real-Time Protection", "DisableRealtimeMonitoring", 1),

        // 3. Signature Updates 目录
        Sub("Signature Updates", "ForceUpdateFromMU", 0),

        // 4. Spynet 目录
        Sub("Spynet", "DisableBlockAtFirstSeen", 1),

        // 5. 资源管理器里屏蔽掉「固定到主页」相关扩展（空字符串值）
        new Entry
        {
            Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked",
            Name = "{e2bf9676-5f8f-435c-97eb-11607a5bedf7}",
            Value = string.Empty,
            Kind = RegistryValueKind.String,
            DeleteKeyWhenEmpty = true,
        },

        // 6. 「固定到主页」只在按 Shift 时出现：pintohome 是系统自带的键，只加一个值，关掉时只删这个值
        new Entry
        {
            Path = @"SOFTWARE\Classes\Folder\shell\pintohome",
            Name = "Extended",
            Value = string.Empty,
            Kind = RegistryValueKind.String,
            DeleteKeyWhenEmpty = false,
        },

        // 7. 智能应用控制（Smart App Control）：不是删值，关掉时要改回 2
        new Entry
        {
            Path = @"SYSTEM\CurrentControlSet\Control\CI\Policy",
            Name = "VerifiedAndReputablePolicyState",
            Value = 0,
            Kind = RegistryValueKind.DWord,
            Restore = 2,
            DeleteKeyWhenEmpty = false,
        },
    };

    private static Entry Root(string name, int value) => new()
    {
        Path = DefenderRoot,
        Name = name,
        Value = value,
        DeleteKeyWhenEmpty = false,
        IsGpoPolicy = true,
    };

    private static Entry Sub(string subKey, string name, int value) => new()
    {
        Path = DefenderRoot + @"\" + subKey,
        Name = name,
        Value = value,
        DeleteKeyWhenEmpty = true,
        IsGpoPolicy = true,
    };

    /// <summary>所有条目都到位才算开；缺一条就显示为关（再点一下会把整组写回）。</summary>
    public static bool TryGetEnabled()
    {
        try
        {
            foreach (Entry entry in Settings)
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(entry.Path, writable: false);
                if (key is null || !entry.Matches(key))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>开 = 写整组，关 = 整组还原。未提权时拉起 runas 提权实例。</summary>
    public static bool Apply(bool enabled, out string error)
    {
        error = string.Empty;

        return IsElevated
            ? SetDirect(enabled, out error)
            : ApplyViaElevatedSelf(enabled, out error);
    }

    private static bool SetDirect(bool enabled, out string error)
    {
        error = string.Empty;

        try
        {
            if (enabled)
            {
                WriteAll();
            }
            else
            {
                RevertAll();
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "没有管理员权限";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void WriteAll()
    {
        foreach (Entry entry in Settings)
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(entry.Path, writable: true)
                ?? throw new InvalidOperationException("打不开 " + entry.Path);

            key.SetValue(entry.Name, entry.Value, entry.Kind);

            // 这些值本身也是组策略项：同步进本地组策略文件，
            // 否则 gpedit 里显示「未配置」，下次组策略刷新还会把值删掉/改回去
            if (entry.IsGpoPolicy)
            {
                LocalGroupPolicy.SetDword(entry.Path, entry.Name, Convert.ToInt32(entry.Value));
            }
        }
    }

    private static void RevertAll()
    {
        // 先逐个删值 / 改回原值，再回头清理空目录（目录必须等句柄关掉才能删）
        var touched = new List<string>();

        foreach (Entry entry in Settings)
        {
            using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(entry.Path, writable: true))
            {
                if (key is not null)
                {
                    if (entry.Restore is null)
                    {
                        key.DeleteValue(entry.Name, throwOnMissingValue: false);
                    }
                    else
                    {
                        key.SetValue(entry.Name, entry.Restore, entry.Kind);
                    }
                }
            }

            if (entry.IsGpoPolicy)
            {
                LocalGroupPolicy.Remove(entry.Path, entry.Name);
            }

            if (entry.DeleteKeyWhenEmpty && !touched.Contains(entry.Path))
            {
                touched.Add(entry.Path);
            }
        }

        foreach (string path in touched)
        {
            // 只有目录空到只剩壳才删：里面还有别人的东西就不动
            using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(path, writable: false))
            {
                if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0)
                {
                    continue;
                }
            }

            Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    /// <summary>当前进程是否已经是管理员。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static bool ApplyViaElevatedSelf(bool enabled, out string error)
    {
        error = string.Empty;

        try
        {
            string exe = Environment.ProcessPath ?? string.Empty;
            if (exe.Length == 0)
            {
                error = "拿不到程序路径";
                return false;
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{ElevationSwitch} {(enabled ? "1" : "0")}",
            };

            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                error = "无法启动提权实例";
                return false;
            }

            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return true;
            }

            error = "提权实例返回 " + process.ExitCode;
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            error = "已取消提权";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
