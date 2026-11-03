namespace NovaServer;

internal enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

internal readonly record struct LogEntry(DateTime Time, LogLevel Level, string Message);

/// <summary>
/// 运行日志：服务启停、数据同步、发布结果、设置保存都往这里写一条，
/// 界面的「主页」把最近 500 条读出来展示。服务可能在后台线程写，界面自己切回 UI 线程。
/// </summary>
internal static class ServerLog
{
    private const int MaxKeep = 500;

    private static readonly object Gate = new();
    private static readonly List<LogEntry> Buffer = new();

    public static event Action<LogEntry>? EntryAdded;

    public static void Info(string message) => Add(LogLevel.Info, message);

    public static void Success(string message) => Add(LogLevel.Success, message);

    public static void Warning(string message) => Add(LogLevel.Warning, message);

    public static void Error(string message) => Add(LogLevel.Error, message);

    public static LogEntry[] Snapshot()
    {
        lock (Gate)
        {
            return Buffer.ToArray();
        }
    }

    private static void Add(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);

        Action<LogEntry>? handler;

        lock (Gate)
        {
            Buffer.Add(entry);

            if (Buffer.Count > MaxKeep)
            {
                Buffer.RemoveRange(0, Buffer.Count - MaxKeep);
            }

            handler = EntryAdded;
        }

        try
        {
            handler?.Invoke(entry);
        }
        catch
        {
            // 界面关掉 / 没准备好时不要影响服务本身
        }
    }
}
