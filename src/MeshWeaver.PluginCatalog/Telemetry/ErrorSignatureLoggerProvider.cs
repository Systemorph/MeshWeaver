using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The logging-pipeline tap that feeds the <see cref="ErrorSignatureCollector"/>: one more
/// <see cref="ILoggerProvider"/> beside the console/Loki ones, enabled for <c>Error</c> and
/// <c>Critical</c> only (<c>Doc/Architecture/InstanceTelemetry</c>). It reads the record the way the
/// digest contract needs it — the message TEMPLATE off the structured state's <c>{OriginalFormat}</c>,
/// never the formatted text — so a value, a secret or a person's name in a log line never reaches the
/// ledger. A record logged without a template (a pre-formatted string) is normalised first
/// (<see cref="TelemetryRedaction.Normalize"/>): its values become placeholders.
///
/// <para>🚨 A logger sink must never throw into the pipeline and never block it: every fold is
/// wrapped, the collector is lock-free, and the provider's own category is excluded so a fault in
/// the reporter cannot feed itself.</para>
/// </summary>
[ProviderAlias("InstanceErrorSignatures")]
public sealed class ErrorSignatureLoggerProvider(ErrorSignatureCollector collector) : ILoggerProvider
{
    /// <summary>Categories this tap ignores: its own reporter, so a delivery fault never loops.</summary>
    internal const string OwnCategoryPrefix = "MeshWeaver.PluginCatalog.InstanceErrorReporter";

    /// <summary>The structured-state key .NET logging stores the template under.</summary>
    internal const string OriginalFormatKey = "{OriginalFormat}";

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new SignatureLogger(collector, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <summary>The template of a structured log call, or <c>null</c> when the state carries none.</summary>
    internal static string? TemplateOf<TState>(TState state)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> properties)
            return null;
        foreach (var property in properties)
        {
            if (string.Equals(property.Key, OriginalFormatKey, StringComparison.Ordinal)
                && property.Value is string template && template.Length > 0)
                return template;
        }
        return null;
    }

    /// <summary>
    /// The first application frame of an exception's stack as <c>Type.Method</c> — the first frame
    /// under <c>MeshWeaver.</c> or <c>Memex.</c> when there is one, else the throw site. No
    /// arguments, no file, no line: a frame identifies a site, the rest varies and can carry a path.
    /// </summary>
    internal static string? TopFrame(Exception? exception)
    {
        var trace = exception?.StackTrace;
        if (string.IsNullOrWhiteSpace(trace))
            return null;
        string? first = null;
        foreach (var raw in trace.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("at ", StringComparison.Ordinal))
                continue;
            var paren = line.IndexOf('(');
            var frame = (paren > 3 ? line[3..paren] : line[3..]).Trim();
            if (frame.Length == 0)
                continue;
            first ??= frame;
            if (frame.StartsWith("MeshWeaver.", StringComparison.Ordinal) || frame.StartsWith("Memex.", StringComparison.Ordinal))
                return frame;
        }
        return first;
    }

    private sealed class SignatureLogger(ErrorSignatureCollector collector, string category) : ILogger
    {
        private readonly bool isOwn = category.StartsWith(OwnCategoryPrefix, StringComparison.Ordinal);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error && logLevel < LogLevel.None && !isOwn;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            try
            {
                var template = TemplateOf(state) ?? TelemetryRedaction.Normalize(formatter(state, exception));
                if (template.Length == 0)
                    template = exception?.GetType().FullName ?? "(no message)";
                collector.Record(category, eventId.Id, logLevel, template, exception?.GetType().FullName,
                    TopFrame(exception), DateTimeOffset.UtcNow);
            }
            catch (Exception)
            {
                // A telemetry tap is the one place a swallowed exception is correct: throwing here
                // would fault the logging call that is reporting an error, and the only effect of
                // the miss is one uncounted signature. Nothing to tell and nowhere safe to tell it.
            }
        }
    }
}
