using System.ComponentModel;
using System.Text.Json.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Observability;

/// <summary>
/// Where an incident is in its lifecycle. The watcher only ever creates incidents in
/// <see cref="New"/>; every later transition is written by the portal's own watchers, so the
/// state is authoritative in the mesh and survives a watcher restart.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LogIncidentStatus>))]
public enum LogIncidentStatus
{
    /// <summary>Just ingested — not yet handed to the triage agent.</summary>
    [Description("New")]
    [Translation("de", "Neu")]
    New,

    /// <summary>A triage thread is running; the agent is drafting the ticket.</summary>
    [Description("Triaging")]
    [Translation("de", "Wird analysiert")]
    Triaging,

    /// <summary>The draft is ready and the issue is being opened on GitHub.</summary>
    [Description("Filing")]
    [Translation("de", "Wird gemeldet")]
    Filing,

    /// <summary>An issue exists for this fingerprint (the incident carries its URL).</summary>
    [Description("Filed")]
    [Translation("de", "Gemeldet")]
    Filed,

    /// <summary>Triage or filing failed; the incident records why.</summary>
    [Description("Failed")]
    [Translation("de", "Fehlgeschlagen")]
    Failed,

    /// <summary>Deliberately not ticketed (known noise). Repeats are counted but never filed.</summary>
    [Description("Suppressed")]
    [Translation("de", "Unterdrückt")]
    Suppressed,
}
