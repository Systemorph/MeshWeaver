using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Abstractions;

namespace OpenSmc.Fixture;

public static class LoggingBuilderExtensions
{
    /// <summary>
    /// Adds a console logger named 'Console' to the factory.
    /// </summary>
    /// <param name="builder">The <see cref="ILoggingBuilder"/> to use.</param>
    /// <param name="outputHelperAccessor">outputHelperAccessor to register. If not specified, a type singleton is registered</param>
    public static ILoggingBuilder AddXUnitLogger(this ILoggingBuilder builder, TestOutputHelperAccessor outputHelperAccessor = null)
    {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, XUnitLoggerProvider>());
        builder.SetMinimumLevel(LogLevel.Debug);
        if (outputHelperAccessor == null)
            builder.Services.AddSingleton<TestOutputHelperAccessor>();
        else
            builder.Services.AddSingleton(outputHelperAccessor);
        return builder;
    }
}
public class TestOutputHelperAccessor
{
    public ITestOutputHelper OutputHelper { get; set; }
}

[ProviderAlias("XUnitLogger")]
public class XUnitLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly TestOutputHelperAccessor testOutputHelperAccessor;
    private readonly ConcurrentDictionary<string, XUnitLogger> loggers = new();
    private IExternalScopeProvider scopeProvider;

    public XUnitLoggerProvider(TestOutputHelperAccessor testOutputHelperAccessor)
    {
        this.testOutputHelperAccessor = testOutputHelperAccessor;
    }

    // ReSharper disable once ParameterHidesMember
    void ISupportExternalScope.SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        this.scopeProvider = scopeProvider;
    }

    void IDisposable.Dispose()
    {
    }

    public ILogger CreateLogger(string categoryName)
    {
        return loggers.GetOrAdd(categoryName, _ => new(categoryName, testOutputHelperAccessor, scopeProvider));
    }
}

public class XUnitLogger : ILogger
{
    private readonly TestOutputHelperAccessor testOutputHelperAccessor;
    private readonly string categoryName;
    private readonly IExternalScopeProvider scopeProvider;

    public XUnitLogger(string categoryName, TestOutputHelperAccessor testOutputHelperAccessor, IExternalScopeProvider scopeProvider)
    {
        this.testOutputHelperAccessor = testOutputHelperAccessor;
        this.categoryName = categoryName;
        this.scopeProvider = scopeProvider;
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public IDisposable BeginScope<TState>(TState state) => scopeProvider.Push(state);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        if (testOutputHelperAccessor.OutputHelper == null)
            return;
        if (!IsEnabled(logLevel))
            return;
        if (formatter == null)
            throw new ArgumentNullException(nameof(formatter));

        var sb = new StringBuilder();
        sb.Append(GetLogLevelString(logLevel))
          .Append($" {DateTime.UtcNow:hh:mm:ss.fff tt}")
          .Append(" [").Append(categoryName).AppendLine("] ")
          .Append(formatter(state, exception));

        if (exception != null)
        {
            sb.Append('\n').Append(exception);
        }

        // Append scopes
        scopeProvider.ForEachScope((scope, s) =>
        {
            s.Append("\n => ");
            s.Append(scope);
        }, sb);

        testOutputHelperAccessor.OutputHelper.WriteLine(sb.ToString());
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
