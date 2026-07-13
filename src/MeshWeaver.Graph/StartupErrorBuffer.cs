using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>One Error/Critical log entry captured during the startup window.</summary>
/// <param name="Timestamp">UTC time the entry was logged.</param>
/// <param name="Level">The log level (<see cref="LogLevel.Error"/> or <see cref="LogLevel.Critical"/>).</param>
/// <param name="Category">The logger category that produced the entry.</param>
/// <param name="Message">The formatted message (exception message appended when present).</param>
public sealed record StartupError(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message);

/// <summary>
/// Buffers every <see cref="LogLevel.Error"/>/<see cref="LogLevel.Critical"/> log entry recorded
/// during the <b>startup window</b> — from logger-factory construction until the host reaches
/// <c>ApplicationStarted</c>, when <see cref="StartupErrorNotifier"/> drains it and raises ONE
/// Admin-partition bell notification summarizing the errors. Errors after the window closes are
/// ignored (they belong to normal operations monitoring, not the boot report).
///
/// <para>An instance singleton owned by the host's DI container (never static — see
/// <c>Doc/Architecture/NoStaticState.md</c>); the backing list is guarded by a plain lock, the
/// same shape as <c>ActivityLogLogger</c>. Bounded: only the first <see cref="Capacity"/> entries
/// are kept (a boot that produces hundreds of errors is summarized by its first ones plus a
/// dropped count), so a log storm can never balloon memory.</para>
/// </summary>
public sealed class StartupErrorBuffer
{
    /// <summary>Maximum number of entries kept; later entries only increment <see cref="StartupErrorReport.Dropped"/>.</summary>
    public const int Capacity = 100;

    private readonly object gate = new();
    private ImmutableList<StartupError> errors = [];
    private int dropped;
    private bool open = true;

    /// <summary>True while the startup window is open (entries are still being recorded).</summary>
    public bool IsOpen
    {
        get { lock (gate) return open; }
    }

    /// <summary>
    /// Records one startup error. No-op after <see cref="CloseAndDrain"/>. Never throws — this
    /// sits on the logging path and must not be able to break the code that logs.
    /// </summary>
    public void Record(LogLevel level, string category, string message)
    {
        if (level < LogLevel.Error)
            return;
        lock (gate)
        {
            if (!open)
                return;
            if (errors.Count >= Capacity)
                dropped++;
            else
                errors = errors.Add(new StartupError(DateTimeOffset.UtcNow, level, category, message));
        }
    }

    /// <summary>
    /// Closes the startup window and returns everything recorded. Idempotent: the first call
    /// drains; subsequent calls return an empty report (so a double-fired lifecycle callback
    /// can never produce a second notification).
    /// </summary>
    public StartupErrorReport CloseAndDrain()
    {
        lock (gate)
        {
            if (!open)
                return new StartupErrorReport([], 0);
            open = false;
            var report = new StartupErrorReport(errors, dropped);
            errors = [];
            dropped = 0;
            return report;
        }
    }
}

/// <summary>The drained content of a <see cref="StartupErrorBuffer"/>.</summary>
/// <param name="Errors">The captured entries, oldest first (at most <see cref="StartupErrorBuffer.Capacity"/>).</param>
/// <param name="Dropped">How many further entries arrived after the buffer was full.</param>
public sealed record StartupErrorReport(ImmutableList<StartupError> Errors, int Dropped);

/// <summary>
/// The <see cref="ILoggerProvider"/> that feeds <see cref="StartupErrorBuffer"/>: registered as an
/// additional provider (DI: <c>AddSingleton&lt;ILoggerProvider, StartupErrorBufferLoggerProvider&gt;</c>),
/// so every logger the factory hands out ALSO forwards its Error/Critical entries to the buffer while
/// the startup window is open. Cheap and inert once the window closes (<see cref="ILogger.IsEnabled"/>
/// turns false), and it never throws — a reporter fault must not take down logging.
/// </summary>
public sealed class StartupErrorBufferLoggerProvider(StartupErrorBuffer buffer) : ILoggerProvider
{
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new BufferLogger(buffer, categoryName);

    /// <inheritdoc />
    public void Dispose() { }

    private sealed class BufferLogger(StartupErrorBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error && buffer.IsOpen;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            try
            {
                var message = formatter(state, exception);
                if (exception is not null)
                    message = $"{message} — {exception.GetType().Name}: {exception.Message}";
                buffer.Record(logLevel, category, message);
            }
            catch
            {
                // Never let the startup-error capture itself become a startup error.
            }
        }
    }
}
