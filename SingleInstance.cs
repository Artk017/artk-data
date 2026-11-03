using System.IO.Pipes;

namespace WinUIDemo;

/// <summary>
/// 单实例：第一个进程占住互斥量并监听一条命名管道；
/// 之后再点快捷方式，新进程只给已有窗口发个信号把它拉到前面就退出。
/// </summary>
internal static class SingleInstance
{
    private const string Id = "ArtkTools";

    private static Mutex? _mutex;

    /// <summary>抢到第一个实例返回 true；已经有实例在跑就通知它显示窗口并返回 false。</summary>
    public static bool TryTake(Action onActivate)
    {
        _mutex = new Mutex(true, @"Local\" + Id, out bool created);

        if (!created)
        {
            Signal();
            return false;
        }

        var listener = new Thread(() => Listen(onActivate))
        {
            IsBackground = true,
            Name = "Artk Tools 单实例监听",
        };
        listener.Start();

        return true;
    }

    private static void Listen(Action onActivate)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    Id + ".activate", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);

                server.WaitForConnection();
                server.ReadByte();
                onActivate();
            }
            catch
            {
                // 管道没建起来（极少数情况）就歇一下再等
                Thread.Sleep(200);
            }
        }
    }

    private static void Signal()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id + ".activate", PipeDirection.Out);
            client.Connect(400);
            client.WriteByte(1);
        }
        catch
        {
            // 老实例还没准备好 / 已经退出：直接退出就行
        }
    }
}
