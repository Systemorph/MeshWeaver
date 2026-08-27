#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>One captured <c>ROUTER_TRAFFIC</c> ERROR — the detector's verdict over a real delivery.</summary>
internal sealed record RouterTrafficRecord(string MessageType, string Role, string Sender, string Target);

/// <summary>
/// Captures the <c>ROUTER_TRAFFIC</c> ERROR records <c>MessageHub.ReportRouterTraffic</c> logs, out
/// of the REAL logging pipeline. Asserting on them is the only way to pin the detector's verdict
/// over every delivery a test drove without re-implementing the detector — each captured record is
/// exactly one production ERROR line, and each is a <c>RouterTrafficRule.RoleOf</c> call the hub
/// itself made over a real delivery.
///
/// <para>Register with
/// <c>ConfigureServices(s =&gt; s.AddLogging(l =&gt; l.Services.AddSingleton&lt;ILoggerProvider&gt;(capture)))</c>
/// on the test's <c>MeshBuilder</c>.</para>
/// </summary>
internal sealed class RouterTrafficCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<RouterTrafficRecord> records = new();

    internal RouterTrafficRecord[] Records => records.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(records);

    public void Dispose() { }

    private sealed class CapturingLogger(ConcurrentQueue<RouterTrafficRecord> sink) : ILogger
    {
        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();
            public void Dispose() { }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error || state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                return;
            if (!formatter(state, exception).StartsWith("ROUTER_TRAFFIC:", StringComparison.Ordinal))
                return;

            sink.Enqueue(new RouterTrafficRecord(
                Value(values, "MessageType"),
                Value(values, "Role"),
                Value(values, "Sender"),
                Value(values, "Target")));
        }

        private static string Value(IReadOnlyList<KeyValuePair<string, object?>> values, string key)
        {
            foreach (var pair in values)
                if (pair.Key == key)
                    return pair.Value?.ToString() ?? "";
            return "";
        }
    }
}
