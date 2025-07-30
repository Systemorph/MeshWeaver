using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Text;
using Xunit;

namespace MeshWeaver.Fixture;

/// <summary>
/// Static registry to track active test output helpers for cross-class access
/// </summary>
public static class XUnitFileOutputRegistry
{
    private static readonly ConcurrentDictionary<object, XUnitFileOutputHelper> _activeOutputHelpers = new();
    
    public static void Register(object testInstance, XUnitFileOutputHelper outputHelper)
    {
        _activeOutputHelpers[testInstance] = outputHelper;
    }
    
    public static void Unregister(object testInstance)
    {
        _activeOutputHelpers.TryRemove(testInstance, out _);
    }
    
    public static XUnitFileOutputHelper? GetOutputHelper(object testInstance)
    {
        return _activeOutputHelpers.TryGetValue(testInstance, out var helper) ? helper : null;
    }
    
    public static XUnitFileOutputHelper? GetAnyActiveOutputHelper()
    {
        return _activeOutputHelpers.Values.FirstOrDefault();
    }
}

/// <summary>
/// A test output helper that writes to both xUnit test output and a file
/// Replaces the DebugFileLogger approach with xUnit-based output capturing
/// </summary>
public class XUnitFileOutputHelper : ITestOutputHelper, IDisposable
{
    private readonly ITestOutputHelper _xunitOutput;
    private string _logFilePath;
    private readonly object _fileLock = new();
    private static readonly string LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-logs");
    private bool _disposed = false;
    private readonly string _baseFileName;
    private string? _currentTestMethod;

    static XUnitFileOutputHelper()
    {
        // Ensure log directory exists
        Directory.CreateDirectory(LogDirectory);
    }

    public XUnitFileOutputHelper(ITestOutputHelper xunitOutput, string? testName = null)
    {
        _xunitOutput = xunitOutput;
        
        // Create base file name for this test instance
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var processId = Environment.ProcessId;
        var instanceId = Guid.NewGuid().ToString("N")[..8];
        _baseFileName = testName != null 
            ? $"test_{testName}_{timestamp}_{processId}_{instanceId}" 
            : $"test_{timestamp}_{processId}_{instanceId}";
        
        // Start with class-level log file
        _logFilePath = Path.Combine(LogDirectory, $"{_baseFileName}.log");
        
        // Write initial header
        WriteToFile($"=== TEST LOG STARTED: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        WriteToFile($"Process ID: {processId}");
        WriteToFile($"Test Class: {testName ?? "Unknown"}");
        WriteToFile($"Log File: {_logFilePath}");
        WriteToFile(new string('=', 60));
    }

    public void SetCurrentTestMethod(string methodName)
    {
        if (_disposed) return;
        
        _currentTestMethod = methodName;
        
        // Create new log file with method name
        var newLogPath = Path.Combine(LogDirectory, $"{_baseFileName}_{methodName}.log");
        
        lock (_fileLock)
        {
            // Copy existing content to new file if needed
            if (File.Exists(_logFilePath) && _logFilePath != newLogPath)
            {
                try
                {
                    var existingContent = File.ReadAllText(_logFilePath);
                    File.WriteAllText(newLogPath, existingContent);
                }
                catch
                {
                    // If copy fails, just start fresh
                }
            }
            
            _logFilePath = newLogPath;
        }
        
        WriteToFile($"=== TEST METHOD STARTED: {methodName} at {DateTime.Now:HH:mm:ss.fff} ===");
    }

    public void WriteLine(string message)
    {
        if (_disposed) return;
        
        var timestampedMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        
        // Write to xUnit output (visible in test runners)
        try
        {
            _xunitOutput.WriteLine(timestampedMessage);
        }
        catch (InvalidOperationException)
        {
            // xUnit output may not be available (e.g., after test completion)
            // Continue with file logging
        }
        
        // Write to file
        WriteToFile(timestampedMessage);
    }

    public void WriteLine(string format, params object[] args)
    {
        WriteLine(string.Format(format, args));
    }

    public void Write(string message)
    {
        // Convert Write to WriteLine for consistency with file logging
        WriteLine(message);
    }

    public void Write(string format, params object[] args)
    {
        // Convert Write to WriteLine for consistency with file logging
        WriteLine(format, args);
    }

    public string Output => "XUnitFileOutputHelper";

    private void WriteToFile(string message)
    {
        if (_disposed) return;

        // Use retry logic for file access to handle temporary conflicts
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                lock (_fileLock)
                {
                    File.AppendAllText(_logFilePath, message + Environment.NewLine);
                }
                break; // Success, exit retry loop
            }
            catch (IOException) when (attempt < 2)
            {
                // File might be locked, wait and retry
                Thread.Sleep(10 + attempt * 10);
            }
            catch (Exception)
            {
                // Other exceptions or final attempt - give up silently to avoid breaking tests
                break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        WriteToFile($"=== TEST LOG ENDED: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Logger provider that creates loggers using XUnitFileOutputHelper
/// Replaces DebugFileLoggerProvider with xUnit-integrated approach
/// </summary>
public class XUnitFileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, XUnitFileLogger> _loggers = new();
    private readonly Func<XUnitFileOutputHelper?> _outputHelperFactory;
    private readonly IServiceProvider? _serviceProvider;
    private bool _disposed = false;

    public XUnitFileLoggerProvider(Func<XUnitFileOutputHelper?> outputHelperFactory, IServiceProvider? serviceProvider = null)
    {
        _outputHelperFactory = outputHelperFactory;
        _serviceProvider = serviceProvider;
    }

    public ILogger CreateLogger(string categoryName)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(XUnitFileLoggerProvider));
            
        return _loggers.GetOrAdd(categoryName, name => new XUnitFileLogger(name, _outputHelperFactory, _serviceProvider));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _loggers.Clear();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Logger implementation that writes to XUnitFileOutputHelper
/// Replaces DebugFileLogger with xUnit-integrated approach
/// </summary>
public class XUnitFileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly Func<XUnitFileOutputHelper?> _outputHelperFactory;
    private readonly IServiceProvider? _serviceProvider;
    private readonly Lazy<LogLevel> _minLogLevel;

    public XUnitFileLogger(string categoryName, Func<XUnitFileOutputHelper?> outputHelperFactory, IServiceProvider? serviceProvider = null)
    {
        _categoryName = categoryName;
        _outputHelperFactory = outputHelperFactory;
        _serviceProvider = serviceProvider;
        _minLogLevel = new Lazy<LogLevel>(GetMinLogLevel);
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull 
        => new NoOpDisposable();

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLogLevel.Value;

    private LogLevel GetMinLogLevel()
    {
        if (_serviceProvider == null)
            return LogLevel.Information;

        try
        {
            var configuration = _serviceProvider.GetService<IConfiguration>();
            if (configuration == null)
                return LogLevel.Information;

            // Check for specific category configuration first
            var categoryLevel = configuration[$"Logging:LogLevel:{_categoryName}"];
            if (!string.IsNullOrEmpty(categoryLevel) && Enum.TryParse<LogLevel>(categoryLevel, out var specificLevel))
                return specificLevel;

            // Check for default configuration
            var defaultLevel = configuration["Logging:LogLevel:Default"];
            if (!string.IsNullOrEmpty(defaultLevel) && Enum.TryParse<LogLevel>(defaultLevel, out var defaultLogLevel))
                return defaultLogLevel;

            return LogLevel.Information;
        }
        catch
        {
            return LogLevel.Information;
        }
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var outputHelper = _outputHelperFactory();
        if (outputHelper == null)
            return;

        var message = formatter(state, exception);
        var logEntry = $"[{logLevel}] [{_categoryName}] {message}";

        if (exception != null)
            logEntry += $"\nException: {exception}";

        outputHelper.WriteLine(logEntry);
    }

    private class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}