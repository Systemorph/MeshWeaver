using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace MeshWeaver.Fixture;

/// <summary>
/// File-based logger for debugging message flow and identifying hanging issues
/// </summary>
public class DebugFileLogger : ILogger
{
    private static readonly string LogDirectory = Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? ".", "MeshWeaverDebugLogs");
    private static readonly object FileLock = new();
    private readonly string _categoryName;
    private readonly string _logFileName;

    static DebugFileLogger()
    {
        Directory.CreateDirectory(LogDirectory);
    }

    public DebugFileLogger(string categoryName)
    {
        _categoryName = categoryName;
        _logFileName = Path.Combine(LogDirectory, $"debug_{DateTime.Now:yyyyMMdd_HHmmss}_{categoryName.Replace(".", "_")}.log");
    }

    public IDisposable BeginScope<TState>(TState state) => new NoOpDisposable();

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var message = formatter(state, exception);
        var logEntry = $"[{timestamp}] [{logLevel}] [{_categoryName}] {message}";

        if (exception != null)
            logEntry += $"\nException: {exception}";

        // Add extra details for message-related logs
        if (state is IEnumerable<KeyValuePair<string, object>> properties)
        {
            foreach (var prop in properties)
            {
                if (prop.Key.Contains("Message") || prop.Key.Contains("Delivery") || prop.Key.Contains("Address"))
                {
                    try
                    {
                        var serialized = JsonSerializer.Serialize(prop.Value, new JsonSerializerOptions { WriteIndented = false });
                        logEntry += $"\n  {prop.Key}: {serialized}";
                    }
                    catch
                    {
                        logEntry += $"\n  {prop.Key}: {prop.Value}";
                    }
                }
            }
        }

        lock (FileLock)
        {
            File.AppendAllText(_logFileName, logEntry + Environment.NewLine);
        }
    }

    private class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// Logger provider for debug file logging
/// </summary>
public class DebugFileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, DebugFileLogger> _loggers = new();

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new DebugFileLogger(name));
    }

    public void Dispose()
    {
        _loggers.Clear();
    }
}
