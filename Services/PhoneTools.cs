using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArtkTools.Services;

public enum PhoneConnectionState
{
    None,
    System,
    Fastboot,
    Fastbootd,
    Recovery,
    Sideload,
    Unauthorized,
    Offline,
    Unknown,
}

public sealed record PhoneInfo
{
    public PhoneConnectionState State { get; init; } = PhoneConnectionState.None;
    public string StateText { get; init; } = "未检测到设备";
    public string Serial { get; init; } = "—";
    public string DeviceName { get; init; } = "—";
    public string Codename { get; init; } = "—";
    public string AndroidVersion { get; init; } = "—";
    public string BootloaderLock { get; init; } = "—";
    public string SystemVersion { get; init; } = "—";
    public string KernelVersion { get; init; } = "—";
    public string AbPartition { get; init; } = "—";
    public string Selinux { get; init; } = "—";
    public string BatteryStatus { get; init; } = "—";
    public string BatteryLevel { get; init; } = "—";
    public string BatteryVoltage { get; init; } = "—";
    public string BatteryTemp { get; init; } = "—";
    public string BatteryLife { get; init; } = "—";
    public double MemoryPercent { get; init; }
    public string MemoryText { get; init; } = "—";
    public bool HasMemory { get; init; }
    public double StoragePercent { get; init; }
    public string StorageText { get; init; } = "—";
    public bool HasStorage { get; init; }
    public bool ToolsMissing { get; init; }
    public string? ToolPath { get; init; }
}

public enum PhoneAction
{
    Reboot,
    SoftReboot,
    EnterFastboot,
    EnterFastbootd,
}

/// <summary>
/// 通过 adb / fastboot 读取已连接安卓设备的状态与信息。
/// 工具查找顺序：NOVA_ADB/NOVA_FASTBOOT 环境变量 → 程序目录 tools\platform-tools → PATH →
/// %LOCALAPPDATA%\Android\Sdk\platform-tools → 常见目录。
/// </summary>
public static class PhoneTools
{
    public static string? AdbPath { get; private set; }

    public static string? FastbootPath { get; private set; }

    public static bool ToolsFound => AdbPath is not null || FastbootPath is not null;

    private static bool _adbServerReset;

    // 本进程正在跑的 adb / fastboot 命令（安装 APK、查询设备等），退出时统一掐掉
    private static readonly object ProcessGate = new();
    private static readonly HashSet<Process> Running = new();

    /// <summary>应用退出时调用：结束所有还在跑的 adb / fastboot 命令，并顺手关掉 adb server。</summary>
    public static void Shutdown()
    {
        List<Process> running;

        lock (ProcessGate)
        {
            running = Running.ToList();
        }

        foreach (Process process in running)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 进程刚好退出或无权结束，忽略
            }
        }

        lock (ProcessGate)
        {
            Running.Clear();
        }

        // adb 是客户端 / 服务端结构：只结束命令行客户端的话 adb.exe 服务会一直常驻。
        // 关闭软件时主动 kill-server，避免手机工具关掉后还留一个后台进程。
        string? adb = AdbPath;

        if (adb is null)
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(adb)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            psi.ArgumentList.Add("kill-server");

            using Process? process = Process.Start(psi);
            process?.WaitForExit(2000);
        }
        catch
        {
            // 结束失败不影响退出
        }
    }

    public static async Task<PhoneInfo> QueryAsync(CancellationToken token = default)
    {
        LocateTools();

        if (!ToolsFound)
        {
            return new PhoneInfo
            {
                ToolsMissing = true,
                StateText = "未找到 adb / fastboot",
            };
        }

        try
        {
            // adb server 是常驻后台进程，会继承启动它的客户端的当前目录。
            // 从安装目录启动的 Nova 会让 adb.exe 一直占着安装目录，Velopack 更新时无法重命名目录。
            // 这里首次查询前重启一次 server（子进程工作目录已固定到临时目录），之后不再动它。
            if (AdbPath is not null && !_adbServerReset)
            {
                _adbServerReset = true;
                await RunAsync(AdbPath, new[] { "kill-server" }, 4000, token).ConfigureAwait(false);
            }

            // 1) fastboot / fastbootd：优先判断，因为这类设备不会出现在 adb 列表里
            if (FastbootPath is not null)
            {
                (bool fbOk, string fbOut) = await RunAsync(FastbootPath, new[] { "devices" }, 4000, token).ConfigureAwait(false);
                string? fbSerial = fbOk ? ParseFastbootDevices(fbOut) : null;

                if (fbSerial is not null)
                {
                    (_, string varsOut) = await RunAsync(
                        FastbootPath, new[] { "-s", fbSerial, "getvar", "all" }, 7000, token).ConfigureAwait(false);

                    Dictionary<string, string> vars = ParseGetvar(varsOut);
                    bool userspace = vars.TryGetValue("is-userspace", out string? iu)
                        && string.Equals(iu?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);

                    return BuildFastbootInfo(fbSerial, vars, userspace);
                }
            }

            // 2) adb：普通系统 / recovery / sideload / 未授权 / 离线
            if (AdbPath is not null)
            {
                (bool adbOk, string devOut) = await RunAsync(AdbPath, new[] { "devices", "-l" }, 4000, token).ConfigureAwait(false);

                if (adbOk && ParseAdbDevices(devOut) is { } dev)
                {
                    PhoneConnectionState state = dev.State switch
                    {
                        "device" => PhoneConnectionState.System,
                        "recovery" => PhoneConnectionState.Recovery,
                        "sideload" => PhoneConnectionState.Sideload,
                        "unauthorized" => PhoneConnectionState.Unauthorized,
                        "offline" => PhoneConnectionState.Offline,
                        _ => PhoneConnectionState.Unknown,
                    };

                    if (state is PhoneConnectionState.Unauthorized or PhoneConnectionState.Offline)
                    {
                        return new PhoneInfo
                        {
                            State = state,
                            StateText = StateTextOf(state),
                            Serial = dev.Serial,
                            ToolPath = AdbPath,
                        };
                    }

                    return await BuildAndroidInfoAsync(dev, state, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 查询失败按未连接处理，避免页面崩
        }

        return new PhoneInfo
        {
            StateText = "未检测到设备",
            ToolPath = AdbPath ?? FastbootPath,
        };
    }

    // ---------------- 构建信息 ----------------

    private static async Task<PhoneInfo> BuildAndroidInfoAsync(AdbDevice dev, PhoneConnectionState state, CancellationToken token)
    {
        string adb = AdbPath!;

        Task<(bool Ok, string Out)> propsTask = RunAsync(adb, new[] { "-s", dev.Serial, "shell", "getprop" }, 6000, token);
        Task<(bool Ok, string Out)> kernelTask = RunAsync(adb, new[] { "-s", dev.Serial, "shell", "uname", "-r" }, 4000, token);
        Task<(bool Ok, string Out)> selinuxTask = RunAsync(adb, new[] { "-s", dev.Serial, "shell", "getenforce" }, 4000, token);
        Task<(bool Ok, string Out)> batteryTask = RunAsync(adb, new[] { "-s", dev.Serial, "shell", "dumpsys", "battery" }, 6000, token);
        Task<(bool Ok, string Out)> memTask = RunAsync(adb, new[] { "-s", dev.Serial, "shell", "cat", "/proc/meminfo" }, 5000, token);
        Task<(bool Ok, string Out)> dfTask = RunAsync(adb, new[] { "-s", dev.Serial, "shell", "df", "/data" }, 5000, token);
        Task<(bool Ok, string Out)> chargeFullTask = RunAsync(adb, new[] { "-s", dev.Serial, "shell", "cat", "/sys/class/power_supply/battery/charge_full" }, 4000, token);
        Task<(bool Ok, string Out)> chargeDesignTask = RunAsync(adb, new[] { "-s", dev.Serial, "shell", "cat", "/sys/class/power_supply/battery/charge_full_design" }, 4000, token);

        await Task.WhenAll(propsTask, kernelTask, selinuxTask, batteryTask, memTask, dfTask, chargeFullTask, chargeDesignTask).ConfigureAwait(false);

        Dictionary<string, string> props = propsTask.Result.Ok
            ? ParseGetprop(propsTask.Result.Out)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string kernel = kernelTask.Result.Ok ? CleanLine(kernelTask.Result.Out) : string.Empty;
        if (kernel.Length == 0)
        {
            kernel = Prop(props, "ro.kernel.version");
        }

        string selinux = selinuxTask.Result.Ok ? CleanLine(selinuxTask.Result.Out) : string.Empty;

        Dictionary<string, string> bat = batteryTask.Result.Ok
            ? ParseKeyValue(batteryTask.Result.Out)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        BatteryReadout battery = ReadBattery(bat, chargeFullTask.Result.Out, chargeDesignTask.Result.Out);
        (bool memOk, double memPercent, string memText) = ReadMemory(memTask.Result.Out);
        (bool storeOk, double storePercent, string storeText) = ReadStorage(dfTask.Result.Out);

        return new PhoneInfo
        {
            State = state,
            StateText = StateTextOf(state),
            Serial = dev.Serial,
            DeviceName = FirstNonEmpty(
                Prop(props, "ro.product.marketname"),
                Prop(props, "ro.product.model"),
                Prop(props, "ro.product.vendor.model"),
                dev.Model),
            Codename = FirstNonEmpty(
                Prop(props, "ro.product.device"),
                Prop(props, "ro.product.vendor.device"),
                Prop(props, "ro.product.system.device"),
                Prop(props, "ro.build.product"),
                dev.Device),
            AndroidVersion = BuildAndroidVersion(props),
            BootloaderLock = BuildBootloaderLock(props),
            SystemVersion = BuildSystemVersion(props),
            KernelVersion = kernel.Length > 0 ? kernel : "—",
            AbPartition = BuildAbPartition(props),
            Selinux = BuildSelinux(props, selinux),
            BatteryStatus = battery.Status,
            BatteryLevel = battery.Level,
            BatteryVoltage = battery.Voltage,
            BatteryTemp = battery.Temp,
            BatteryLife = battery.Life,
            MemoryPercent = memPercent,
            MemoryText = memText,
            HasMemory = memOk,
            StoragePercent = storePercent,
            StorageText = storeText,
            HasStorage = storeOk,
            ToolPath = adb,
        };
    }

    private static PhoneInfo BuildFastbootInfo(string serial, Dictionary<string, string> vars, bool fastbootd)
    {
        string stateText = fastbootd ? "Fastbootd 模式" : "Fastboot 模式";

        string codename = FirstNonEmpty(Get(vars, "product"), Get(vars, "variant"), "—");
        string unlocked = Get(vars, "unlocked");
        string bl = unlocked.Length == 0
            ? "—"
            : unlocked.Equals("yes", StringComparison.OrdinalIgnoreCase)
                ? "已解锁"
                : "未解锁（已锁定）";

        string slots = Get(vars, "slot-count");
        string current = Get(vars, "current-slot");
        string ab;
        if (slots.Length > 0 && slots != "1")
        {
            ab = current.Length > 0 ? $"支持（{slots} 槽，当前 {current}）" : $"支持（{slots} 槽）";
        }
        else if (slots == "1")
        {
            ab = "不支持（单分区）";
        }
        else
        {
            ab = current.Length > 0 ? $"支持（当前槽位 {current}）" : "—";
        }

        string fbVoltage = Get(vars, "battery-voltage");
        string fbSoc = Get(vars, "battery-soc");

        return new PhoneInfo
        {
            State = fastbootd ? PhoneConnectionState.Fastbootd : PhoneConnectionState.Fastboot,
            StateText = stateText,
            Serial = serial,
            Codename = codename,
            BootloaderLock = bl,
            AbPartition = ab,
            BatteryLevel = long.TryParse(fbSoc, out long soc) && soc > 0 ? $"{soc}%" : "—",
            BatteryVoltage = long.TryParse(fbVoltage, out long mv) && mv > 0 ? $"{mv} mV（{mv / 1000.0:0.00} V）" : "—",
            ToolPath = FastbootPath,
        };
    }

    /// <summary>执行任意 adb / fastboot 命令（调用方负责校验工具名），返回完整输出。</summary>
    public static Task<(bool Ok, string Output)> RunRawAsync(string exe, string[] args, int timeoutMs = 30000, CancellationToken token = default)
        => RunAsync(exe, args, timeoutMs, token);

    /// <summary>把本地 APK 安装到设备（adb install -r）。</summary>
    public static async Task<(bool Ok, string Output)> InstallApkAsync(PhoneInfo info, string apkPath, CancellationToken token = default)
    {
        LocateTools();

        if (AdbPath is null)
        {
            return (false, "未找到 adb，请先安装 platform-tools。");
        }

        if (string.IsNullOrWhiteSpace(info.Serial) || info.Serial == "—")
        {
            return (false, "没有已连接的设备。");
        }

        if (info.State is not (PhoneConnectionState.System or PhoneConnectionState.Recovery or PhoneConnectionState.Sideload))
        {
            return (false, "设备当前不在 adb 模式，无法安装 APK。");
        }

        string[] args = { "-s", info.Serial, "install", "-r", apkPath };

        return await RunAsync(AdbPath, args, 600000, token).ConfigureAwait(false);
    }

    /// <summary>执行重启 / 进入 fastboot 等快捷命令。</summary>
    public static async Task<(bool Ok, string Message)> RunActionAsync(PhoneInfo info, PhoneAction action, CancellationToken token = default)
    {
        LocateTools();

        string serial = info.Serial;
        if (string.IsNullOrWhiteSpace(serial) || serial == "—")
        {
            return (false, "没有已连接的设备。");
        }

        bool adbMode = info.State is PhoneConnectionState.System or PhoneConnectionState.Recovery or PhoneConnectionState.Sideload;
        bool fbMode = info.State is PhoneConnectionState.Fastboot or PhoneConnectionState.Fastbootd;

        if (!adbMode && !fbMode)
        {
            return (false, "设备未授权或离线，先让手机允许 USB 调试。");
        }

        string? adb = AdbPath;
        string? fb = FastbootPath;

        if (adbMode)
        {
            if (adb is null)
            {
                return (false, "找不到 adb。");
            }

            string[] args = action switch
            {
                PhoneAction.Reboot => new[] { "-s", serial, "reboot" },
                PhoneAction.SoftReboot => new[] { "-s", serial, "reboot", "userspace" },
                PhoneAction.EnterFastboot => new[] { "-s", serial, "reboot", "bootloader" },
                PhoneAction.EnterFastbootd => new[] { "-s", serial, "reboot", "fastboot" },
                _ => Array.Empty<string>(),
            };

            (bool ok, string output) = await RunAsync(adb, args, 15000, token).ConfigureAwait(false);
            string msg = action switch
            {
                PhoneAction.Reboot => "已发送重启命令，设备正在重启…",
                PhoneAction.SoftReboot => "已发送软重启命令（用户空间重启），几秒后系统界面会重新加载…",
                PhoneAction.EnterFastboot => "已发送命令，设备正在进入 Fastboot…",
                PhoneAction.EnterFastbootd => "已发送命令，设备正在进入 Fastbootd…",
                _ => "已执行。",
            };

            return ok ? (true, msg) : (false, "命令执行失败：" + CleanLine(output));
        }

        if (fb is null)
        {
            return (false, "找不到 fastboot。");
        }

        switch (action)
        {
            case PhoneAction.SoftReboot:
                return (false, "Fastboot 模式下不支持软重启，请先重启到系统。");

            case PhoneAction.EnterFastboot when info.State == PhoneConnectionState.Fastboot:
                return (false, "设备已经在 Fastboot 模式。");

            case PhoneAction.EnterFastbootd when info.State == PhoneConnectionState.Fastbootd:
                return (false, "设备已经在 Fastbootd 模式。");
        }

        string[] fbArgs = action switch
        {
            PhoneAction.Reboot => new[] { "-s", serial, "reboot" },
            PhoneAction.EnterFastboot => new[] { "-s", serial, "reboot", "bootloader" },
            PhoneAction.EnterFastbootd => new[] { "-s", serial, "reboot", "fastboot" },
            _ => Array.Empty<string>(),
        };

        (bool fbOk, string fbOut) = await RunAsync(fb, fbArgs, 15000, token).ConfigureAwait(false);
        string fbMsg = action switch
        {
            PhoneAction.Reboot => "已发送重启命令，设备正在重启…",
            PhoneAction.EnterFastboot => "已发送命令，设备正在进入 Fastboot…",
            PhoneAction.EnterFastbootd => "已发送命令，设备正在进入 Fastbootd…",
            _ => "已执行。",
        };

        return fbOk ? (true, fbMsg) : (false, "命令执行失败：" + CleanLine(fbOut));
    }

    private static string BuildAndroidVersion(Dictionary<string, string> props)
    {
        string release = Prop(props, "ro.build.version.release");
        string sdk = Prop(props, "ro.build.version.sdk");

        if (release.Length == 0 && sdk.Length == 0)
        {
            return "—";
        }

        if (release.Length == 0)
        {
            return $"SDK {sdk}";
        }

        return sdk.Length > 0 ? $"{release}（SDK {sdk}）" : release;
    }

    private static string BuildBootloaderLock(Dictionary<string, string> props)
    {
        string flashLocked = Prop(props, "ro.boot.flash.locked");
        if (flashLocked.Length > 0)
        {
            return flashLocked == "1" ? "未解锁（已锁定）" : "已解锁";
        }

        string secure = Prop(props, "ro.secureboot.lockstate");
        if (secure.Length > 0)
        {
            return secure.Equals("locked", StringComparison.OrdinalIgnoreCase) ? "未解锁（已锁定）" : "已解锁";
        }

        string verified = Prop(props, "ro.boot.verifiedbootstate");
        if (verified.Length > 0)
        {
            return verified.Equals("green", StringComparison.OrdinalIgnoreCase) ? "未解锁（已锁定）" : "已解锁";
        }

        return "—";
    }

    private static string BuildSystemVersion(Dictionary<string, string> props)
    {
        var parts = new List<string>();

        string osName = FirstNonEmpty(
            Prop(props, "ro.mi.os.version.name"),
            Prop(props, "ro.miui.ui.version.name"),
            Prop(props, "ro.build.display.id"),
            Prop(props, "ro.build.version.incremental"));

        if (osName.Length > 0)
        {
            parts.Add(osName);
        }

        string patch = Prop(props, "ro.build.version.security_patch");
        if (patch.Length > 0)
        {
            parts.Add("补丁 " + patch);
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "—";
    }

    private static string BuildAbPartition(Dictionary<string, string> props)
    {
        string slot = Prop(props, "ro.boot.slot_suffix");
        bool abUpdate = Prop(props, "ro.build.ab_update").Equals("true", StringComparison.OrdinalIgnoreCase);
        bool virtualAb = Prop(props, "ro.virtual_ab.enabled").Equals("true", StringComparison.OrdinalIgnoreCase);

        if (abUpdate || slot.Length > 0)
        {
            string kind = virtualAb ? "虚拟 A/B" : "A/B";
            return slot.Length > 0 ? $"{kind}（当前槽位 {slot}）" : kind;
        }

        return "不支持（非 A/B）";
    }

    private static string BuildSelinux(Dictionary<string, string> props, string enforced)
    {
        string value = enforced.Length > 0 ? enforced : Prop(props, "ro.boot.selinux");

        if (value.Length == 0)
        {
            return "—";
        }

        if (value.Equals("Enforcing", StringComparison.OrdinalIgnoreCase))
        {
            return "强制（Enforcing）";
        }

        if (value.Equals("Permissive", StringComparison.OrdinalIgnoreCase))
        {
            return "宽容（Permissive）";
        }

        if (value.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return "已关闭（Disabled）";
        }

        return value;
    }

    // ---------------- 电池 / 内存 / 存储 ----------------

    private sealed record BatteryReadout(string Status, string Level, string Voltage, string Temp, string Life);

    private static BatteryReadout ReadBattery(Dictionary<string, string> bat, string chargeFullOut, string chargeDesignOut)
    {
        bool powered = Get(bat, "AC powered").Equals("true", StringComparison.OrdinalIgnoreCase)
            || Get(bat, "USB powered").Equals("true", StringComparison.OrdinalIgnoreCase)
            || Get(bat, "Wireless powered").Equals("true", StringComparison.OrdinalIgnoreCase);

        string status = Get(bat, "status") switch
        {
            "2" => "充电中",
            "3" => "未充电（放电中）",
            "4" => "未充电（已连接）",
            "5" => "已充满",
            _ => powered ? "充电中" : "未充电（放电中）",
        };

        string level = "—";
        if (long.TryParse(Get(bat, "level"), out long lv) && long.TryParse(Get(bat, "scale"), out long scale) && scale > 0)
        {
            level = $"{lv * 100 / scale}%";
        }

        string voltage = "—";
        if (long.TryParse(Get(bat, "Max charging voltage"), out long chargeUv) && chargeUv > 0)
        {
            // 充电电压（微伏），优先于电池电压
            voltage = $"{chargeUv / 1000} mV（{chargeUv / 1000000.0:0.00} V）";
        }
        else if (long.TryParse(Get(bat, "voltage"), out long mv) && mv > 0)
        {
            voltage = $"{mv} mV（{mv / 1000.0:0.00} V）";
        }

        string temp = "—";
        if (long.TryParse(Get(bat, "temperature"), out long tenths))
        {
            temp = $"{tenths / 10.0:0.0} °C";
        }

        string life = "—";
        long full = ParseLong(chargeFullOut);
        long design = ParseLong(chargeDesignOut);
        if (full > 0 && design > 0 && full <= design * 1.5)
        {
            int pct = (int)Math.Round(full * 100.0 / design);
            life = $"{pct}%（约 {full / 1000} / {design / 1000} mAh）";
        }
        else
        {
            life = "—（系统未提供）";
        }

        return new BatteryReadout(status, level, voltage, temp, life);
    }

    private static (bool Ok, double Percent, string Text) ReadMemory(string output)
    {
        long total = 0;
        long available = -1;

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                total = FirstNumber(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                available = FirstNumber(line);
            }
        }

        if (total <= 0)
        {
            return (false, 0, "—");
        }

        long used = available >= 0 ? total - available : -1;
        if (used < 0)
        {
            return (false, 0, "—");
        }

        double percent = Math.Round(used * 100.0 / total, 1);
        return (true, percent, $"已用 {Gb(used)} / {Gb(total)}");
    }

    private static (bool Ok, double Percent, string Text) ReadStorage(string output)
    {
        long total = -1;
        long used = -1;

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("Filesystem", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // df 的列：Filesystem | 1K-blocks(总) | Used | Available | Use% | Mounted
            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                continue;
            }

            if (long.TryParse(parts[1], out long t) && long.TryParse(parts[2], out long u))
            {
                total = t;
                used = u;
            }
        }

        if (total <= 0 || used < 0)
        {
            return (false, 0, "—");
        }

        double percent = Math.Round(used * 100.0 / total, 1);
        return (true, percent, $"已用 {Gb(used)} / {Gb(total)}");
    }

    private static Dictionary<string, string> ParseKeyValue(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (key.Length > 0 && !map.ContainsKey(key))
            {
                map[key] = value;
            }
        }

        return map;
    }

    private static long ParseLong(string output)
    {
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (long.TryParse(line, out long value))
            {
                return value;
            }
        }

        return 0;
    }

    private static long FirstNumber(string line)
    {
        var digits = new StringBuilder();
        foreach (char c in line)
        {
            if (char.IsDigit(c))
            {
                digits.Append(c);
            }
            else if (digits.Length > 0)
            {
                break;
            }
        }

        return digits.Length > 0 && long.TryParse(digits.ToString(), out long value) ? value : 0;
    }

    private static string Gb(long kb)
    {
        double gb = kb / 1048576.0;
        return gb >= 100 ? $"{gb:0} GB" : $"{gb:0.0} GB";
    }

    private static string StateTextOf(PhoneConnectionState state) => state switch
    {
        PhoneConnectionState.System => "系统运行中",
        PhoneConnectionState.Fastboot => "Fastboot 模式",
        PhoneConnectionState.Fastbootd => "Fastbootd 模式",
        PhoneConnectionState.Recovery => "Recovery 模式",
        PhoneConnectionState.Sideload => "Sideload 模式",
        PhoneConnectionState.Unauthorized => "未授权（请在手机上允许 USB 调试）",
        PhoneConnectionState.Offline => "设备离线",
        PhoneConnectionState.Unknown => "状态未知",
        _ => "未检测到设备",
    };

    // ---------------- 工具定位 ----------------

    private static void LocateTools()
    {
        AdbPath ??= FindTool("adb", Environment.GetEnvironmentVariable("NOVA_ADB"));
        FastbootPath ??= FindTool("fastboot", Environment.GetEnvironmentVariable("NOVA_FASTBOOT"));
    }

    private static string? FindTool(string name, string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var candidates = new List<string>();
        string exe = name + ".exe";
        string baseDir = AppContext.BaseDirectory;

        candidates.Add(Path.Combine(baseDir, "tools", "platform-tools", exe));
        candidates.Add(Path.Combine(baseDir, "platform-tools", exe));
        candidates.Add(Path.Combine(baseDir, "tools", exe));

        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    candidates.Add(Path.Combine(dir.Trim(), exe));
                }
                catch
                {
                    // 路径非法就跳过
                }
            }
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(localAppData, "Android", "Sdk", "platform-tools", exe));

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(userProfile, "platform-tools", exe));

        candidates.Add($@"C:\platform-tools\{exe}");
        candidates.Add($@"D:\platform-tools\{exe}");
        candidates.Add($@"D:\adb\{exe}");
        candidates.Add($@"C:\adb\{exe}");

        foreach (string candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // 忽略非法路径
            }
        }

        return null;
    }

    // ---------------- 解析 ----------------

    private sealed record AdbDevice(string Serial, string State, string? Model, string? Device);

    private static AdbDevice? ParseAdbDevices(string output)
    {
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) || line.StartsWith("*", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].Equals("adb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string serial = parts[0];
            string state = parts[1];

            // 只认真正的设备状态行，避免把 adb 自身的提示行（server version 等）当成设备
            bool known = state.Equals("device", StringComparison.OrdinalIgnoreCase)
                || state.Equals("recovery", StringComparison.OrdinalIgnoreCase)
                || state.Equals("sideload", StringComparison.OrdinalIgnoreCase)
                || state.Equals("unauthorized", StringComparison.OrdinalIgnoreCase)
                || state.Equals("offline", StringComparison.OrdinalIgnoreCase)
                || state.Equals("bootloader", StringComparison.OrdinalIgnoreCase);

            if (!known)
            {
                continue;
            }

            string? model = null;
            string? device = null;

            for (int i = 2; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                {
                    model = parts[i]["model:".Length..].Replace('_', ' ');
                }
                else if (parts[i].StartsWith("device:", StringComparison.OrdinalIgnoreCase))
                {
                    device = parts[i]["device:".Length..];
                }
            }

            return new AdbDevice(serial, state, model, device);
        }

        return null;
    }

    private static string? ParseFastbootDevices(string output)
    {
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1].Contains("fastboot", StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }

        return null;
    }

    private static Dictionary<string, string> ParseGetprop(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length < 4 || line[0] != '[')
            {
                continue;
            }

            int close = line.IndexOf("]: [", StringComparison.Ordinal);
            if (close <= 1)
            {
                continue;
            }

            string key = line[1..close];
            int valueStart = close + 4;
            int valueEnd = line.LastIndexOf(']');
            string value = valueEnd >= valueStart ? line[valueStart..valueEnd] : string.Empty;
            map[key] = value;
        }

        return map;
    }

    private static Dictionary<string, string> ParseGetvar(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (key.Length > 0 && !key.StartsWith("fastboot", StringComparison.OrdinalIgnoreCase))
            {
                map[key] = value;
            }
        }

        return map;
    }

    private static string Prop(Dictionary<string, string> props, string key)
    {
        return props.TryGetValue(key, out string? value) ? value.Trim() : string.Empty;
    }

    private static string Get(Dictionary<string, string> map, string key)
    {
        return map.TryGetValue(key, out string? value) ? value.Trim() : string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "—";
    }

    private static string CleanLine(string output)
    {
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return string.Empty;
    }

    // ---------------- 进程 ----------------

    private static async Task<(bool Ok, string Output)> RunAsync(string exe, string[] args, int timeoutMs, CancellationToken token)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // adb server 会继承这个目录，绝不能是安装目录，否则会锁住 Nova 的安装文件夹导致更新失败
            WorkingDirectory = Path.GetTempPath(),
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        var output = new StringBuilder();

        try
        {
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };

            if (!process.Start())
            {
                return (false, string.Empty);
            }

            // 记进运行表：退出应用时统一结束，避免安装 / 查询命令在后台残留
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                lock (ProcessGate)
                {
                    Running.Remove(process);
                }
            };

            lock (ProcessGate)
            {
                Running.Add(process);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(timeoutMs);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 进程已退出
                }

                return (false, output.ToString());
            }

            return (process.ExitCode == 0, output.ToString());
        }
        catch (Exception)
        {
            return (false, output.ToString());
        }
    }
}
