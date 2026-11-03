using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace WinUIDemo.Services;

/// <summary>一次采样得到的系统指标。</summary>
public readonly record struct SystemSnapshot(
    double CpuPercent,
    double MemoryPercent,
    double MemoryUsedGb,
    double MemoryTotalGb,
    double GpuPercent,
    string GpuBusiestEngine,
    double NetworkDownBytesPerSecond,
    double NetworkUpBytesPerSecond,
    double NetworkLinkBitsPerSecond);

/// <summary>
/// 系统指标采样：CPU（GetSystemTimes 差值）、内存（GlobalMemoryStatusEx）、
/// GPU（PDH 的 GPU Engine 计数器）、带宽（网卡 IP 统计差值）。
/// 首次调用只建立基线，结果中的 CPU / 带宽为 0，之后每次调用返回区间平均值。
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;

    // ---- CPU ----
    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasCpuBaseline;

    // ---- 网络 ----
    private readonly Dictionary<string, long> _prevRx = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _prevTx = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _networkClock = Stopwatch.StartNew();
    private double _prevNetworkSeconds;

    // ---- GPU (PDH) ----
    private IntPtr _query;
    private IntPtr _gpuCounter;
    private bool _gpuReady;

    public SystemMonitor()
    {
        TryInitGpu();
    }

    public SystemSnapshot Sample()
    {
        double cpu = ReadCpu();
        (double memPercent, double usedGb, double totalGb) = ReadMemory();
        (double gpu, string engine) = ReadGpu();
        (double down, double up, double link) = ReadNetwork();

        return new SystemSnapshot(cpu, memPercent, usedGb, totalGb, gpu, engine, down, up, link);
    }

    // ---------------- CPU ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    private double ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        ulong idleTicks = ToUInt64(idle);
        ulong kernelTicks = ToUInt64(kernel);
        ulong userTicks = ToUInt64(user);

        if (!_hasCpuBaseline)
        {
            _prevIdle = idleTicks;
            _prevKernel = kernelTicks;
            _prevUser = userTicks;
            _hasCpuBaseline = true;
            return 0;
        }

        // kernel 时间已包含 idle，所以总时间 = kernel + user
        ulong idleDelta = idleTicks - _prevIdle;
        ulong totalDelta = (kernelTicks - _prevKernel) + (userTicks - _prevUser);

        _prevIdle = idleTicks;
        _prevKernel = kernelTicks;
        _prevUser = userTicks;

        if (totalDelta == 0)
        {
            return 0;
        }

        double busy = 1.0 - ((double)idleDelta / totalDelta);
        return Math.Clamp(busy * 100.0, 0, 100);
    }

    private static ulong ToUInt64(FileTime value)
    {
        return ((ulong)value.High << 32) | value.Low;
    }

    // ---------------- 内存 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static (double Percent, double UsedGb, double TotalGb) ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return (0, 0, 0);
        }

        const double bytesPerGb = 1024d * 1024d * 1024d;
        double total = status.TotalPhys / bytesPerGb;
        double used = (status.TotalPhys - status.AvailPhys) / bytesPerGb;
        return (status.MemoryLoad, used, total);
    }

    // ---------------- GPU ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhItem
    {
        public IntPtr Name;
        public uint Status;
        public uint Padding;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    private void TryInitGpu()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out var query) != 0)
            {
                return;
            }

            if (PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out var counter) != 0)
            {
                PdhCloseQuery(query);
                return;
            }

            // 第一次采集只建立一个基线，之后才有值
            PdhCollectQueryData(query);

            _query = query;
            _gpuCounter = counter;
            _gpuReady = true;
        }
        catch
        {
            _gpuReady = false;
        }
    }

    private (double Percent, string Engine) ReadGpu()
    {
        if (!_gpuReady || PdhCollectQueryData(_query) != 0)
        {
            return (0, string.Empty);
        }

        uint size = 0;
        uint count = 0;
        uint status = PdhGetFormattedCounterArray(_gpuCounter, PdhFmtDouble, ref size, ref count, IntPtr.Zero);
        if (status != PdhMoreData || size == 0 || count == 0)
        {
            return (0, string.Empty);
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArray(_gpuCounter, PdhFmtDouble, ref size, ref count, buffer);
            if (status != 0)
            {
                return (0, string.Empty);
            }

            // 实例名形如 pid_1234_luid_0x..._phys_0_eng_0_engtype_3D
            // 同一引擎类型下各进程累加，再取最忙的引擎类型（与任务管理器的口径接近）
            var perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            int itemSize = Marshal.SizeOf<PdhItem>();

            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhItem>(IntPtr.Add(buffer, i * itemSize));
                if (item.Status != 0)
                {
                    continue;
                }

                string name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                string engine = ExtractEngineType(name);

                perEngine[engine] = perEngine.TryGetValue(engine, out double current)
                    ? current + item.Value
                    : item.Value;
            }

            double busiest = 0;
            string busiestName = string.Empty;
            foreach (var pair in perEngine)
            {
                if (pair.Value > busiest)
                {
                    busiest = pair.Value;
                    busiestName = pair.Key;
                }
            }

            return (Math.Clamp(busiest, 0, 100), busiestName);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ExtractEngineType(string instanceName)
    {
        const string marker = "engtype_";
        int index = instanceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 && index + marker.Length < instanceName.Length
            ? instanceName[(index + marker.Length)..]
            : "其他";
    }

    // ---------------- 网络 ----------------

    private (double Down, double Up, double LinkBits) ReadNetwork()
    {
        long totalRx = 0;
        long totalTx = 0;
        double linkBits = 0;

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                var stats = adapter.GetIPStatistics();
                string key = adapter.Id;

                if (_prevRx.TryGetValue(key, out long prevRx))
                {
                    totalRx += Math.Max(0, stats.BytesReceived - prevRx);
                    totalTx += Math.Max(0, stats.BytesSent - _prevTx[key]);
                }

                _prevRx[key] = stats.BytesReceived;
                _prevTx[key] = stats.BytesSent;

                if (adapter.Speed > linkBits)
                {
                    linkBits = adapter.Speed;
                }
            }
            catch
            {
                // 单个网卡读不到就跳过
            }
        }

        double now = _networkClock.Elapsed.TotalSeconds;
        double elapsed = now - _prevNetworkSeconds;
        _prevNetworkSeconds = now;

        if (elapsed <= 0.05)
        {
            return (0, 0, linkBits);
        }

        return (totalRx / elapsed, totalTx / elapsed, linkBits);
    }

    public void Dispose()
    {
        if (_gpuReady && _query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            _gpuCounter = IntPtr.Zero;
            _gpuReady = false;
        }
    }
}
