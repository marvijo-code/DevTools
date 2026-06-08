using System.Diagnostics;
using System.Text;

namespace DevTools.Infrastructure;

public static class AppLog
{
    private static readonly object SyncRoot = new();
    private static readonly string SessionStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

    static AppLog()
    {
        Directory.CreateDirectory(LogDirectory);
        CurrentLogPath = Path.Combine(LogDirectory, $"session-{SessionStamp}.log");
        Write("INFO", $"Log started. BaseDirectory={AppContext.BaseDirectory}");
    }

    public static string LogDirectory => Path.Combine(AppContext.BaseDirectory, "logs");

    public static string CurrentLogPath { get; }

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
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [")
            .Append(level)
            .Append("] [pid:")
            .Append(Environment.ProcessId)
            .Append("] ")
            .Append(message)
            .AppendLine()
            .ToString();

        lock (SyncRoot)
        {
            File.AppendAllText(CurrentLogPath, line, Encoding.UTF8);
        }

        Debug.Write(line);
    }
}
