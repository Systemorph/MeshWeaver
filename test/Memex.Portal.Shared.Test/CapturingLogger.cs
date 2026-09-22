using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that keeps every formatted line, for tests whose
/// SUBJECT is a log line — the stage a validation failure is logged under, the rows a supersede
/// names. Thread-safe: the services under test log from Rx continuations, never from the test's
/// thread.
/// </summary>
/// <typeparam name="TCategory">The category the logger is injected as.</typeparam>
internal sealed class CapturingLogger<TCategory> : ILogger<TCategory>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Text)> entries = new();

    /// <summary>Every line logged so far, in arrival order.</summary>
    public IReadOnlyList<(LogLevel Level, string Text)> Entries => entries.ToArray();

    /// <summary>The text of every line at <paramref name="level"/>.</summary>
    public IReadOnlyList<string> Lines(LogLevel level) =>
        entries.Where(e => e.Level == level).Select(e => e.Text).ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        entries.Enqueue((logLevel, formatter(state, exception)));
}
