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
    private static int _instanceCounter = 0;
    private readonly string _categoryName;
    private readonly string _logFileName;

    static DebugFileLogger()
    {
        Directory.CreateDirectory(LogDirectory);
    }

    public DebugFileLogger(string categoryName)
    {
        _categoryName = categoryName;
        var instanceId = Interlocked.Increment(ref _instanceCounter);
        var processId = Environment.ProcessId;
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        _logFileName = Path.Combine(LogDirectory, $"debug_{timestamp}_{processId}_{instanceId}_{categoryName.Replace(".", "_")}.log");
    }

    public IDisposable? BeginScope<TState>(TState state) => new NoOpDisposable();

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
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

        // Use retry logic for file access to handle temporary conflicts
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                lock (FileLock)
                {
                    File.AppendAllText(_logFileName, logEntry + Environment.NewLine);
                }
                break; // Success, exit retry loop
            }
            catch (IOException) when (attempt < 2)
            {
                // File might be locked by another process, wait a bit and retry
                Thread.Sleep(10 + attempt * 10);
            }
            catch (Exception)
            {
                // Other exceptions or final attempt - give up silently to avoid breaking tests
                break;
            }
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
