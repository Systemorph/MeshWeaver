using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Fixture;

/// <summary>
/// Extension methods that register an xUnit-backed logger provider on an <see cref="ILoggingBuilder"/>.
/// </summary>
public static class LoggingBuilderExtensions
{
    /// <summary>
    /// Adds a console logger named 'Console' to the factory.
    /// </summary>
    /// <param name="builder">The <see cref="ILoggingBuilder"/> to use.</param>
    /// <param name="outputHelperAccessor">outputHelperAccessor to register. If not specified, a type singleton is registered</param>
    public static ILoggingBuilder AddXUnitLogger(
        this ILoggingBuilder builder,
        TestOutputHelperAccessor? outputHelperAccessor = default
    )
    {
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider, XUnitLoggerProvider>()
        );
        // Don't set a global minimum level or any category filters here — log levels
        // (including quietening Orleans framework noise) are configuration-driven via
        // appsettings.json (the runtime test config), never hardcoded in the logger.
        if (outputHelperAccessor == null)
            builder.Services.AddSingleton<TestOutputHelperAccessor>();
        else
            builder.Services.AddSingleton(outputHelperAccessor);
        return builder;
    }
}

/// <summary>
/// Holds the current test's <see cref="ITestOutputHelper"/> so loggers resolved from DI
/// can route log lines to the active test's output.
/// </summary>
public class TestOutputHelperAccessor
{
    /// <summary>The output helper for the currently running test, or null when none is active.</summary>
    public ITestOutputHelper? OutputHelper { get; set; }
}

/// <summary>
/// <see cref="ILoggerProvider"/> that creates <see cref="XUnitLogger"/> instances routing
/// output to the current test via a <see cref="TestOutputHelperAccessor"/>.
/// </summary>
/// <param name="testOutputHelperAccessor">Accessor providing the active test's output helper.</param>
/// <param name="filterOptions">Monitor for log-level filter options bound from configuration.</param>
[ProviderAlias("XUnitLogger")]
public class XUnitLoggerProvider(TestOutputHelperAccessor testOutputHelperAccessor, IOptionsMonitor<LoggerFilterOptions> filterOptions)
    : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, XUnitLogger> loggers = new();
    private IExternalScopeProvider scopeProvider = null!;

    // ReSharper disable once ParameterHidesMember
    void ISupportExternalScope.SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        this.scopeProvider = scopeProvider;
    }

    void IDisposable.Dispose() { }

    /// <summary>
    /// Returns a cached <see cref="XUnitLogger"/> for the given category, creating one if needed.
    /// </summary>
    /// <param name="categoryName">The logger category name.</param>
    /// <returns>The logger for the category.</returns>
    public ILogger CreateLogger(string categoryName)
    {
        return loggers.GetOrAdd(
            categoryName,
            _ => new(categoryName, testOutputHelperAccessor, scopeProvider, filterOptions)
        );
    }
}

/// <summary>
/// An <see cref="ILogger"/> that writes formatted log lines to the active test's output,
/// honouring log-level rules bound from configuration.
/// </summary>
/// <param name="categoryName">The logger category name.</param>
/// <param name="testOutputHelperAccessor">Accessor providing the active test's output helper.</param>
/// <param name="scopeProvider">Provider for external logging scopes.</param>
/// <param name="filterOptions">Monitor for log-level filter options bound from configuration.</param>
public class XUnitLogger(
    string categoryName,
    TestOutputHelperAccessor testOutputHelperAccessor,
    IExternalScopeProvider scopeProvider,
    IOptionsMonitor<LoggerFilterOptions> filterOptions)
    : ILogger
{
    /// <summary>
    /// Determines whether logging is enabled for the given level, applying the most specific
    /// configured category rule.
    /// </summary>
    /// <param name="logLevel">The level to test.</param>
    /// <returns><c>true</c> if logging is enabled for <paramref name="logLevel"/>; otherwise <c>false</c>.</returns>
    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel == LogLevel.None)
            return false;

        var options = filterOptions.CurrentValue;
        var effectiveLogLevel = GetEffectiveLogLevel(categoryName, options);
        
        return logLevel >= effectiveLogLevel;
    }

    private static LogLevel GetEffectiveLogLevel(string categoryName, LoggerFilterOptions options)
    {
        // Find the most specific rule that matches
        LogLevel? specificLevel = null;
        var longestMatch = -1;

        foreach (var rule in options.Rules)
        {
            // Skip rules that have a specific provider name that's not our provider
            // but allow rules with no provider name (global rules)
            if (rule.ProviderName != null && rule.ProviderName != "XUnitLogger" && rule.ProviderName != typeof(XUnitLoggerProvider).FullName)
                continue;

            if (rule.CategoryName == null)
            {
                // This is a catch-all rule, use it if no more specific rule is found
                if (longestMatch < 0)
                {
                    specificLevel = rule.LogLevel;
                    longestMatch = 0;
                }
            }
            else if (categoryName.StartsWith(rule.CategoryName, StringComparison.OrdinalIgnoreCase))
            {
                // This rule matches our category, check if it's more specific
                if (rule.CategoryName.Length > longestMatch)
                {
                    specificLevel = rule.LogLevel;
                    longestMatch = rule.CategoryName.Length;
                }
            }
        }

        return specificLevel ?? options.MinLevel;
    }

    /// <summary>
    /// Begins a logical logging scope.
    /// </summary>
    /// <typeparam name="TState">The type of the scope state.</typeparam>
    /// <param name="state">The scope state.</param>
    /// <returns>A disposable that ends the scope when disposed.</returns>
    public IDisposable BeginScope<TState>(TState state) where TState: notnull => scopeProvider.Push(state);

    /// <summary>
    /// Writes a log entry to the active test's output when the level is enabled.
    /// </summary>
    /// <typeparam name="TState">The type of the state object being logged.</typeparam>
    /// <param name="logLevel">The severity level of the entry.</param>
    /// <param name="eventId">The event id of the entry.</param>
    /// <param name="state">The state to be logged.</param>
    /// <param name="exception">An optional exception associated with the entry.</param>
    /// <param name="formatter">Function that produces the log message from <paramref name="state"/> and <paramref name="exception"/>.</param>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        var outputHelper = testOutputHelperAccessor.OutputHelper
            ?? XUnitFileOutputRegistry.GetAnyActiveOutputHelper();
        if (outputHelper == null)
            return;
        if (!IsEnabled(logLevel))
            return;
        if (formatter == null)
            throw new ArgumentNullException(nameof(formatter));

        var sb = new StringBuilder();
        sb.Append(GetLogLevelString(logLevel))
            .Append($",{DateTime.UtcNow:hh:mm:ss.fff tt},")
            .Append(categoryName)
            .Append(",")
            .Append(formatter(state, exception));

        if (exception != null)
        {
            sb.Append('\n').Append(exception);
        }

        // Scopes are not logged in tests - only the inlined message is shown

#pragma warning disable RCS1075 // Avoid empty catch clause that catches System.Exception
        try
        {
            outputHelper.WriteLine(sb.ToString());
        }
        catch (Exception) { }
#pragma warning restore RCS1075 // Avoid empty catch clause that catches System.Exception
    }

    [SuppressMessage("ReSharper", "StringLiteralTypo")]
    private static string GetLogLevelString(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => throw new ArgumentOutOfRangeException(nameof(logLevel))
        };
    }
}
