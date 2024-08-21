using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Hosting;

public class CsvConsoleFormatter : ConsoleFormatter, IDisposable
{
    private readonly IDisposable optionsReloadToken;
    private CsvConsoleFormatterOptions formatterOptions;

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

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider scopeProvider, TextWriter textWriter)
    {
        var logBuilder = new StringBuilder();

        if (formatterOptions.IncludeTimestamp)
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

    public void Dispose()
    {
        optionsReloadToken?.Dispose();
    }
}

public class CsvConsoleFormatterOptions
{
    public bool IncludeTimestamp { get; set; } = true;
    public string TimestampFormat { get; set; } = "hh:mm:ss:fff";
}
