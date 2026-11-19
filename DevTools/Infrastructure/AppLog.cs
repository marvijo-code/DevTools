using System.Diagnostics;
using System.Security;
using System.Text;
using System.Threading.Channels;

namespace DevTools.Infrastructure;

public static class AppLog
{
    private const int BatchSize = 64;
    private static readonly DateTime SessionStartUtc = DateTime.UtcNow;
    private static readonly Lazy<string> SessionFileName = new(static () =>
        $"session-{SessionStartUtc.ToLocalTime():yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
    private static readonly string LocalLogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    private static readonly TaskCompletionSource<string?> LogDirectoryReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly Channel<LogEntry> Pending = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(1024)
    {
        SingleReader = true
    });
    private static string? _currentLogDirectory;
    private static long _droppedEntries;

    static AppLog()
    {
        Write("INFO", $"Log started. BaseDirectory={AppContext.BaseDirectory}");
        var writer = Task.Run(WritePendingAsync);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Pending.Writer.TryComplete();
            writer.Wait(TimeSpan.FromSeconds(2));
        };
    }

    public static string LogDirectory => Volatile.Read(ref _currentLogDirectory) ?? LocalLogDirectory;

    public static string CurrentLogPath => Path.Combine(LogDirectory, SessionFileName.Value);

    internal static Task<string?> GetLogDirectoryAsync() => LogDirectoryReady.Task;

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            Write("ERROR", message);
            return;
        }

        Write("ERROR", $"{message}{Environment.NewLine}{exception}");
    }

    private static void Write(string level, string message)
    {
        var entry = new LogEntry(DateTime.UtcNow, level, message);
        if (!Pending.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _droppedEntries);
        }

        Debug.Write(FormatLine(entry));
    }

    private static string FormatLine(LogEntry entry) =>
        $"{entry.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} [{entry.Level}] [pid:{Environment.ProcessId}] {entry.Message}{Environment.NewLine}";

    private static async Task WritePendingAsync()
    {
        try
        {
            var directory = LocalLogDirectory;
            var target = TryOpenLog(directory);
            if (target is null && Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } localAppData)
            {
                directory = Path.Combine(localAppData, "DevTools", "logs");
                target = TryOpenLog(directory);
            }

            if (target is null)
            {
                return;
            }

            using var log = target;
            Volatile.Write(ref _currentLogDirectory, directory);
            LogDirectoryReady.TrySetResult(directory);
            while (await Pending.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                for (var count = 0; count < BatchSize && Pending.Reader.TryRead(out var entry); count++)
                {
                    log.Write(FormatLine(entry));
                }

                var dropped = Interlocked.Exchange(ref _droppedEntries, 0);
                if (dropped > 0)
                {
                    log.Write(FormatLine(new LogEntry(DateTime.UtcNow, "WARN", $"Skipped {dropped:N0} entries because the log queue was full.")));
                }

                await log.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Debug.WriteLine($"DevTools could not write the session log: {ex.Message}");
        }
        finally
        {
            Pending.Writer.TryComplete();
            LogDirectoryReady.TrySetResult(null);
        }
    }

    private static StreamWriter? TryOpenLog(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, SessionFileName.Value);
            return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete,
                4096, FileOptions.Asynchronous), Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Debug.WriteLine($"DevTools could not open the log directory {directory}: {ex.Message}");
            return null;
        }
    }

    private readonly record struct LogEntry(DateTime TimestampUtc, string Level, string Message);
}
