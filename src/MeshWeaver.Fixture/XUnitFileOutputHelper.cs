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
        
        // Store just the test class name for simple naming
        _baseFileName = testName ?? "UnknownTest";
        
        // Don't create log file yet - wait for first test method
        _logFilePath = string.Empty;
    }

    public void SetCurrentTestMethod(string methodName)
    {
        if (_disposed) return;
        
        _currentTestMethod = methodName;
        
        // Create simple log file name: TestClass_TestMethod.log
        var newLogPath = Path.Combine(LogDirectory, $"{_baseFileName}_{methodName}.log");
        
        lock (_fileLock)
        {
            _logFilePath = newLogPath;
            
            // Delete the existing log file if it exists to start fresh
            if (File.Exists(_logFilePath))
            {
                try
                {
                    File.Delete(_logFilePath);
                }
                catch
                {
                    // If we can't delete it, we'll just overwrite it
                }
            }
        }
        
        // Write initial header now that we have a log file
        WriteToFile($"=== TEST LOG STARTED: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        WriteToFile($"Test Method: {_baseFileName}.{methodName}");
        WriteToFile($"Log File: {_logFilePath}");
        WriteToFile(new string('=', 60));
    }

    public void ClearCurrentTestMethod()
    {
        if (_disposed) return;
        _currentTestMethod = null;
    }

    public bool IsInTestMethod()
    {
        return !string.IsNullOrEmpty(_currentTestMethod);
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
        
        // Don't write to file if no log file has been created yet (no test method active)
        if (string.IsNullOrEmpty(_logFilePath)) return;

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

            // Get all logging configuration
            var loggingSection = configuration.GetSection("Logging:LogLevel");
            if (!loggingSection.Exists())
                return LogLevel.Information;

            // Find the most specific rule that matches the category name
            // Rules are checked in order of specificity (longest match first)
            LogLevel? specificLevel = null;
            var longestMatch = -1;

            foreach (var child in loggingSection.GetChildren())
            {
                var ruleName = child.Key;
                var ruleValue = child.Value;
                
                if (string.IsNullOrEmpty(ruleValue) || !Enum.TryParse<LogLevel>(ruleValue, out var ruleLogLevel))
                    continue;

                // Check if this rule matches our category
                if (ruleName == "Default")
                {
                    // Default rule - use it if no more specific rule is found
                    if (longestMatch < 0)
                    {
                        specificLevel = ruleLogLevel;
                        longestMatch = 0;
                    }
                }
                else if (_categoryName.StartsWith(ruleName, StringComparison.OrdinalIgnoreCase))
                {
                    // This rule matches our category, check if it's more specific
                    if (ruleName.Length > longestMatch)
                    {
                        specificLevel = ruleLogLevel;
                        longestMatch = ruleName.Length;
                    }
                }
            }

            return specificLevel ?? LogLevel.Information;
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

        // Only log if we're inside a test method
        if (!outputHelper.IsInTestMethod())
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