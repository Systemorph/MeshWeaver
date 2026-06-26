using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Hosting;

/// <summary>
/// A <see cref="ConsoleFormatter"/> that renders each log entry as a single comma-separated line
/// (optional timestamp, level, category, event id, message), so console output can be ingested as CSV.
/// </summary>
public class CsvConsoleFormatter : ConsoleFormatter, IDisposable
{
    private readonly IDisposable? optionsReloadToken;
    private CsvConsoleFormatterOptions? formatterOptions;

    /// <summary>
    /// Creates the formatter and subscribes to live option changes.
    /// </summary>
    /// <param name="options">Monitor supplying the current <see cref="CsvConsoleFormatterOptions"/> and change notifications.</param>
    public CsvConsoleFormatter(IOptionsMonitor<CsvConsoleFormatterOptions> options)
        : base(nameof(CsvConsoleFormatter))
    {
        optionsReloadToken = options.OnChange(ReloadLoggerOptions);
        formatterOptions = options.CurrentValue;
    }
    private void ReloadLoggerOptions(CsvConsoleFormatterOptions options)
    {
        formatterOptions = options;
    }

    /// <inheritdoc />
    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var logBuilder = new StringBuilder();

        if (formatterOptions!.IncludeTimestamp)
        {
            var timestamp = DateTimeOffset.Now.ToString(formatterOptions.TimestampFormat);
            logBuilder.Append(timestamp).Append(", ");
        }

        logBuilder.Append(logEntry.LogLevel).Append(", ");
        logBuilder.Append(logEntry.Category).Append(", ");
        logBuilder.Append(logEntry.EventId.Id).Append(", ");
        logBuilder.Append(logEntry.Formatter(logEntry.State, logEntry.Exception));

        textWriter.WriteLine(logBuilder.ToString());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        optionsReloadToken?.Dispose();
    }
}

/// <summary>
/// Options controlling how <see cref="CsvConsoleFormatter"/> renders each log line.
/// </summary>
public class CsvConsoleFormatterOptions
{
    /// <summary>Whether to prepend a timestamp column to each line.</summary>
    public bool IncludeTimestamp { get; set; } = true;
    /// <summary>The <see cref="DateTimeOffset"/> format string used for the timestamp column.</summary>
    public string TimestampFormat { get; set; } = "hh:mm:ss:fff"!;
}
