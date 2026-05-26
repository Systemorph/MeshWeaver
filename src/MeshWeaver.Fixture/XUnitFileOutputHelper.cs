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

    // Per-test-method log files: opt-in via MESHWEAVER_TEST_FILE_LOGS=1. Disabled
    // by default so CI runners aren't writing thousands of .log files (their
    // upload step then ships the lot as an artifact, blowing through storage
    // quotas). Locally you can flip this on for the hung-test workflow:
    //   set MESHWEAVER_TEST_FILE_LOGS=1
    //   dotnet test … --filter X
    // and the per-method files appear at bin/Debug/net10.0/test-logs/.
    // ITestOutputHelper.WriteLine is unaffected — xUnit console output remains.
    private static readonly bool FileLogsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("MESHWEAVER_TEST_FILE_LOGS"), "1", StringComparison.Ordinal)
        || string.Equals(Environment.GetEnvironmentVariable("MESHWEAVER_TEST_FILE_LOGS"), "true", StringComparison.OrdinalIgnoreCase);
    // Long-lived writer for the active test method. Replaces the previous
    // `File.AppendAllText` per WriteLine — that opened, wrote, and closed
    // the file on every call (with up to 3 retries on lock contention).
    // Showed up at ~2.4% in autocomplete-test CPU profiles. AutoFlush
    // preserves the "see the last line of a hung test" tail-visibility
    // semantics; the StreamWriter is recreated on SetCurrentTestMethod
    // (per-method log file) and disposed on Dispose.
    private StreamWriter? _writer;

    static XUnitFileOutputHelper()
    {
        // Ensure log directory exists only when file logging is enabled.
        if (FileLogsEnabled)
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

        // Opt-out by default: skip file I/O entirely unless MESHWEAVER_TEST_FILE_LOGS
        // is set. xUnit's own ITestOutputHelper still captures everything for
        // console display; we just stop persisting per-method .log files.
        if (!FileLogsEnabled) return;

        // Create simple log file name: TestClass_TestMethod.log
        var newLogPath = Path.Combine(LogDirectory, $"{_baseFileName}_{methodName}.log");

        lock (_fileLock)
        {
            _logFilePath = newLogPath;

            // Dispose the previous writer (if any) and open a fresh one.
            // `append: false` truncates an existing file, so we don't need
            // a separate File.Delete pass. FileShare.Read lets developers
            // tail the log while the test is running.
            try { _writer?.Dispose(); } catch { /* best-effort */ }
            _writer = null;

            try
            {
                var stream = new FileStream(_logFilePath,
                    FileMode.Create, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(stream) { AutoFlush = true };
            }
            catch
            {
                // If we can't open the file, fall back to silent — file
                // logging is best-effort and must never break a test.
                _writer = null;
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
        
        // Write to xUnit output (visible in test runners). Catch ANY exception:
        // xUnit v2 throws InvalidOperationException after test completion, but v3
        // may throw ObjectDisposedException or other types when the test's output
        // helper is invalidated. A throw here propagating into ILogger callers
        // (e.g. MessageHub's dispose chain) would either fault the action block
        // or — worse — re-trigger the log path on the way up, producing the
        // "endless logs" cascade the dispose pipeline can't recover from.
        try
        {
            _xunitOutput.WriteLine(timestampedMessage);
        }
        catch
        {
            // Swallow — file logging below still records the message; xUnit
            // output is best-effort once the test has been reported.
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

        // Don't write if no log file is active (no test method has started yet).
        var writer = _writer;
        if (writer is null) return;

        // Single WriteLine on the held-open StreamWriter — no per-call file
        // open/close, no retry loop, no IOException to chase. AutoFlush keeps
        // the tail visible immediately for hung-test diagnostics.
        try
        {
            lock (_fileLock)
            {
                writer.WriteLine(message);
            }
        }
        catch
        {
            // Best-effort — never break a test on a logger I/O failure.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        WriteToFile($"=== TEST LOG ENDED: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        _disposed = true;

        lock (_fileLock)
        {
            try { _writer?.Dispose(); } catch { /* best-effort */ }
            _writer = null;
        }

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