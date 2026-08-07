using System.ComponentModel;
using System.Text.Json.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Observability;

/// <summary>
/// The severity a red log line was emitted at. Mirrors the two .NET console prefixes the log
/// watcher treats as "red": <c>fail:</c> (Error) and <c>crit:</c> (Critical).
///
/// <para>Serialized by NAME, explicitly: this type crosses an untyped HTTP boundary (the watcher
/// POSTs it to the portal), and pinning the wire form here means the contract cannot be broken by
/// someone changing the host's global JSON options.</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LogSeverity>))]
public enum LogSeverity
{
    /// <summary>An <c>Error</c>-level line (console prefix <c>fail:</c>).</summary>
    [Description("Error")]
    [Translation("de", "Fehler")]
    Error,

    /// <summary>A <c>Critical</c>-level line (console prefix <c>crit:</c>).</summary>
    [Description("Critical")]
    [Translation("de", "Kritisch")]
    Critical,
}

/// <summary>One red log line kept as evidence on an incident (bounded — the ingest service trims).</summary>
/// <param name="Timestamp">When the line was emitted.</param>
/// <param name="Pod">The pod the line came from.</param>
/// <param name="Line">The verbatim log line (already truncated by the watcher).</param>
public record LogSample(DateTimeOffset Timestamp, string? Pod, string Line);
