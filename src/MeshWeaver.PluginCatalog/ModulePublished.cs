using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Graph.Configuration;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The fact a registry broadcasts to its consumers the moment a module bundle is published: "this
/// package's module now serves at this version, built against this framework identity" (#3650,
/// rule R3 of <c>Doc/Architecture/ModuleAdoptionPolicy</c>: <i>as soon as a new module version
/// ships, we start using it</i>).
///
/// <para><b>Where it goes.</b> The same inbox every other event reaches an installation through —
/// the generic webhook inbox (<see cref="WebhookInbox"/>, <c>POST /api/hooks/{target}</c>) — at the
/// one node the consumer's reconciler already OWNS: its reconcile ledger
/// (<see cref="RegistryUpdateReconciler.LedgerPath"/>). So the target exists on every installation
/// that has a registry configured, nothing new has to be seeded, and the delivery lands as a durable
/// <see cref="WebhookEvent"/> under <see cref="InboxTarget"/><c>/_Inbox/</c> that the reconciler
/// drains on its own thread — a delivery that arrives while the process is down is consumed at the
/// next boot, never lost. The registry finds its consumers by the <c>HomeUrl</c> each recorded
/// when it registered (<c>PluginCatalog:HomeUrl</c>).</para>
///
/// <para>🚨 <b>A WAKE-UP, never the truth.</b> The consumer reads nothing off this record but
/// WHICH package to look at: the reconcile that follows reads the registry's authenticated bundle
/// index itself and runs the same <see cref="ModuleUpdateDecision"/> the boot runs, against the
/// same activation record. A stale, duplicated or forged delivery therefore costs one authenticated
/// index read for one installed package and changes nothing else — which is what lets the delivery
/// be unsigned by default (a consumer that allowlists the target WITH a <c>SecretConfigKey</c> is
/// answered with a matching HMAC when the registry configures <see cref="BroadcastSecretConfigKey"/>),
/// and what makes the safety net (<see cref="PluginCatalogOptions.ReconcileSafetyNetInterval"/>)
/// the floor rather than a second source of truth. Same rule as the platform's <c>Release</c> facts:
/// the event wakes, the read decides.</para>
/// </summary>
public sealed record ModulePublished
{
    /// <summary>The <see cref="Event"/> value a delivery carries — the discriminator the inbox
    /// drain matches on before it reads anything else.</summary>
    public const string EventName = "module-published";

    /// <summary>The consumer-side inbox target: the reconcile ledger node the reconciler owns.
    /// An installation allowlists it as <c>WebhookInbox:Targets:N = Plugins/_RegistryReconcileLedger</c>.</summary>
    public const string InboxTarget = RegistryUpdateReconciler.LedgerPath;

    /// <summary>The route the registry POSTs to on each consumer's <c>HomeUrl</c>.</summary>
    public const string InboxRoute = "/api/hooks/" + RegistryUpdateReconciler.LedgerPath;

    /// <summary>
    /// The registry-side configuration key holding the shared secret the broadcast is SIGNED with
    /// (GitHub-style <c>X-Hub-Signature-256</c> over the raw body) when it is set. Optional: a
    /// consumer that declares no <c>SecretConfigKey</c> on the target accepts the unsigned
    /// delivery, which is safe for the reason the type doc gives. When a consumer DOES declare one
    /// it must name the same value, or every delivery is refused with 401 — visibly, on the
    /// registry's log, never silently dropped (#3312).
    /// </summary>
    public const string BroadcastSecretConfigKey = "Plugins:Registry:BroadcastSecret";

    /// <summary>The serializer both halves use (Web camelCase), so the body a consumer stores is
    /// the body the registry signed.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Always <see cref="EventName"/>.</summary>
    public string Event { get; init; } = EventName;

    /// <summary>The registry's own base URL, as its consumers configured it — what the consumer
    /// matches against <see cref="PluginRegistryReference.Url"/> to pick which of its registries
    /// to reconcile against. Unknown registries are dropped.</summary>
    public string Registry { get; init; } = "";

    /// <summary>The package id whose module was published.</summary>
    public string Package { get; init; } = "";

    /// <summary>The module's entry-assembly name — informational; the consumer's own install record
    /// says which module the package declares.</summary>
    public string? Module { get; init; }

    /// <summary>The published version — informational; the reconcile reads the index.</summary>
    public string? Version { get; init; }

    /// <summary>The framework identity the bytes were built against — informational.</summary>
    public string? FrameworkMvid { get; init; }

    /// <summary>When the registry accepted the publish.</summary>
    public DateTimeOffset PublishedAt { get; init; }

    /// <summary>The wire body of one broadcast.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Parses an inbox delivery's body; null when it is not a module-published record (a different
    /// event on a shared target, or noise). Never throws — an unreadable body is not this event.
    /// </summary>
    public static ModulePublished? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<ModulePublished>(body, Json);
            return parsed is { Event: EventName, Package.Length: > 0 } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
