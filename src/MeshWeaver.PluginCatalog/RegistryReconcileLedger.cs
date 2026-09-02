using System.Collections.Immutable;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What <see cref="RegistryUpdateReconciler"/> knows about each configured registry's reconcile —
/// stored at <see cref="RegistryUpdateReconciler.LedgerPath"/> so that "the boot reconcile did not
/// run" is a durable, admin-readable fact instead of one Error line on one pod (Systemorph/MeshWeaver#2888).
///
/// <para>A SNAPSHOT per process, rewritten from the reconciler's in-memory state on every change:
/// each boot re-attempts every configured registry, so an entry always describes the current
/// process, and a registry removed from configuration drops off on the next boot.</para>
/// </summary>
public record RegistryReconcileLedger
{
    /// <summary>One entry per configured registry, ordered by URL.</summary>
    public ImmutableList<RegistryReconcileEntry> Registries { get; init; } =
        ImmutableList<RegistryReconcileEntry>.Empty;

    /// <summary>When the ledger last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>The reconcile state of ONE registry as this process sees it.</summary>
public record RegistryReconcileEntry
{
    /// <summary>The reconcile ran as part of this process starting.</summary>
    public const string ViaBoot = "boot";

    /// <summary>The reconcile the boot skipped ran later, on the first successful feed read this
    /// installation made for another reason (a catalog open, an install).</summary>
    public const string ViaFeedRead = "feed-read";

    /// <summary>The registry's base URL (the configured value, trailing slash trimmed).</summary>
    public string Url { get; init; } = "";

    /// <summary>The registry's display name, or its URL when it has none.</summary>
    public string Name { get; init; } = "";

    /// <summary>The ref the reconcile reads the feed at.</summary>
    public string Ref { get; init; } = "HEAD";

    /// <summary>
    /// 🚨 The boot reconcile against this registry did NOT run and has not run since: the feed read
    /// exhausted its startup budget. The reconciler drains this on the next successful feed read
    /// any caller makes against the same registry — there is deliberately no timer behind it.
    /// </summary>
    public bool Pending { get; init; }

    /// <summary>When <see cref="Pending"/> was last set.</summary>
    public DateTimeOffset? PendingSince { get; init; }

    /// <summary>How many feed-read attempts the last failed boot spent.</summary>
    public int Attempts { get; init; }

    /// <summary>The last fault's message — the registry's own answer when it gave one.</summary>
    public string? LastFault { get; init; }

    /// <summary>When a reconcile against this registry last completed in this process.</summary>
    public DateTimeOffset? LastReconciledAt { get; init; }

    /// <summary><see cref="ViaBoot"/> or <see cref="ViaFeedRead"/>.</summary>
    public string? LastReconciledVia { get; init; }
}
