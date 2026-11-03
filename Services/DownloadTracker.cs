using System.Collections.Generic;
using System.Threading;

namespace WinUIDemo.Services;

/// <summary>
/// 跟踪当前正在进行的下载，供「退出前提示」使用。
/// 每个下载把自己的 CancellationTokenSource 注册进来，结束再注销。
/// </summary>
public static class DownloadTracker
{
    private static readonly object Gate = new();
    private static readonly HashSet<CancellationTokenSource> Active = new();

    public static int ActiveCount
    {
        get
        {
            lock (Gate)
            {
                return Active.Count;
            }
        }
    }

    public static void Register(CancellationTokenSource cts)
    {
        lock (Gate)
        {
            Active.Add(cts);
        }
    }

    public static void Unregister(CancellationTokenSource cts)
    {
        lock (Gate)
        {
            Active.Remove(cts);
        }
    }

    /// <summary>取消所有正在进行的下载（退出时用）。</summary>
    public static void CancelAll()
    {
        CancellationTokenSource[] copy;

        lock (Gate)
        {
            copy = new CancellationTokenSource[Active.Count];
            Active.CopyTo(copy);
        }

        foreach (CancellationTokenSource cts in copy)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // 已释放 / 已取消都无所谓
            }
        }
    }
}
