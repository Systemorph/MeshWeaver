using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Fixture;

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
        // Don't set a global minimum level - let configuration handle it
        if (outputHelperAccessor == null)
            builder.Services.AddSingleton<TestOutputHelperAccessor>();
        else
            builder.Services.AddSingleton(outputHelperAccessor);
        return builder;
    }
}

public class TestOutputHelperAccessor
{
    public ITestOutputHelper? OutputHelper { get; set; }
}

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

    public ILogger CreateLogger(string categoryName)
    {
        return loggers.GetOrAdd(
            categoryName,
            _ => new(categoryName, testOutputHelperAccessor, scopeProvider, filterOptions)
        );
    }
}

public class XUnitLogger(
    string categoryName,
    TestOutputHelperAccessor testOutputHelperAccessor,
    IExternalScopeProvider scopeProvider,
    IOptionsMonitor<LoggerFilterOptions> filterOptions)
    : ILogger
{
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

    public IDisposable BeginScope<TState>(TState state) where TState: notnull => scopeProvider.Push(state);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        if (testOutputHelperAccessor.OutputHelper == null)
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
            testOutputHelperAccessor.OutputHelper.WriteLine(sb.ToString());
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
