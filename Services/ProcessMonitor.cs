using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace WinUIDemo.Services;

/// <summary>单个进程的占用情况。</summary>
public sealed record ProcessUsage(
    int Pid,
    string Name,
    string? ExecutablePath,
    double CpuPercent,
    double MemoryMb);

/// <summary>
/// 进程占用采样：用 TotalProcessorTime 的增量除以采样间隔得到每个进程的 CPU 百分比。
/// 第一次采样只建立基线，CPU 为 0。
/// </summary>
public sealed class ProcessMonitor
{
    private readonly Dictionary<int, (TimeSpan CpuTime, DateTime SampledAt)> _previous = new();

    public IReadOnlyList<ProcessUsage> Sample(int count)
    {
        var now = DateTime.UtcNow;
        var collected = new List<(Process Process, ProcessUsage Usage)>();
        var seen = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                TimeSpan cpuTime = process.TotalProcessorTime;
                double cpuPercent = 0;

                if (_previous.TryGetValue(process.Id, out var previous))
                {
                    double elapsed = (now - previous.SampledAt).TotalSeconds;
                    if (elapsed > 0.05)
                    {
                        cpuPercent = (cpuTime - previous.CpuTime).TotalSeconds
                                     / elapsed
                                     / Environment.ProcessorCount
                                     * 100;
                    }
                }

                _previous[process.Id] = (cpuTime, now);
                seen.Add(process.Id);

                collected.Add((process, new ProcessUsage(
                    process.Id,
                    process.ProcessName,
                    null,
                    Math.Clamp(cpuPercent, 0, Environment.ProcessorCount * 100d),
                    process.WorkingSet64 / 1048576d)));
            }
            catch
            {
                // 受保护进程拿不到 CPU 时间，直接跳过
                process.Dispose();
            }
        }

        // 只给排在前面的进程解析可执行文件路径（MainModule 较慢）
        var top = collected
            .OrderByDescending(entry => entry.Usage.CpuPercent)
            .ThenByDescending(entry => entry.Usage.MemoryMb)
            .Take(count)
            .Select(entry => entry.Usage with { ExecutablePath = TryGetPath(entry.Process) })
            .ToList();

        foreach (var entry in collected)
        {
            entry.Process.Dispose();
        }

        foreach (int stale in _previous.Keys.Where(pid => !seen.Contains(pid)).ToList())
        {
            _previous.Remove(stale);
        }

        return top;
    }

    private static string? TryGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
