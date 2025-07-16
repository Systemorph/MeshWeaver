using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;
using MeshWeaver.Messaging;
using Xunit.Abstractions;

namespace MeshWeaver.Fixture;

/// <summary>
/// File-based logger for debugging message flow and identifying hanging issues
/// </summary>
public class DebugFileLogger : ILogger
{
    private static readonly string LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
    private static readonly object FileLock = new();
    private static readonly string TestInstanceId = Guid.NewGuid().ToString("N")[..8];
    private static readonly string SharedLogFileName = Path.Combine(LogDirectory, $"meshweaver-{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}_{TestInstanceId}.log");
    private readonly string _categoryName;

    static DebugFileLogger()
    {
        Directory.CreateDirectory(LogDirectory);
    }

    public DebugFileLogger(string categoryName)
    {
        _categoryName = categoryName;
    }

    public IDisposable BeginScope<TState>(TState state)
        where TState:notnull => new NoOpDisposable();

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
                        //var m = prop.Value is ExecutionRequest er ? er with { Action = null! } : prop.Value;
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
                    File.AppendAllText(SharedLogFileName, logEntry + Environment.NewLine);
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
    private bool _disposed = false;

    public ILogger CreateLogger(string categoryName)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DebugFileLoggerProvider));
            
        return _loggers.GetOrAdd(categoryName, name => new DebugFileLogger(name));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
            
        _disposed = true;
        
        // Dispose all loggers
        foreach (var logger in _loggers.Values)
        {
            try
            {
                if (logger is IDisposable disposableLogger)
                    disposableLogger.Dispose();
            }
            catch
            {
                // Ignore disposal exceptions to prevent test failures
            }
        }
        
        _loggers.Clear();
    }
}
