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

    /// <summary>The registry told this installation a module was published
    /// (<see cref="ModulePublished"/>, delivered to the reconciler's inbox) and the module lane
    /// ran for that one package (#3650).</summary>
    public const string ViaBroadcast = "broadcast";

    /// <summary>The safety-net reconcile (<see cref="PluginCatalogOptions.ReconcileSafetyNetInterval"/>)
    /// ran — the bound on how long a lost broadcast can hide (#3650).</summary>
    public const string ViaSafetyNet = "safety-net";

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

    /// <summary><see cref="ViaBoot"/>, <see cref="ViaFeedRead"/>, <see cref="ViaBroadcast"/> or
    /// <see cref="ViaSafetyNet"/>.</summary>
    public string? LastReconciledVia { get; init; }

    /// <summary>
    /// 🚨 The installed packages that declare a compiled module and which THIS registry did not
    /// OFFER at <see cref="Ref"/> — so the module funnel never considers them and their bytes can
    /// never advance from here (Systemorph/MeshWeaver.Plugins#1584). See <see cref="ModuleDelivery"/>
    /// for what this does and does not claim.
    ///
    /// <para>🚨 <b><c>null</c> is NOT an empty list.</b> Null means this process has no successful
    /// feed read from this registry to answer the question from; an EMPTY list means the registry
    /// answered in full and offers every installed module package. Reading the first as the second
    /// is a gate that never ran painted the colour of one that passed — the same distinction
    /// <see cref="Pending"/> exists to draw, one question further along.</para>
    /// </summary>
    public ImmutableList<UndeliveredModule>? UndeliveredModules { get; init; }
}
