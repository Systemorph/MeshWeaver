using System.Collections.Immutable;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Well-known partition-context identifiers. Identical vocabulary to
/// the existing <c>context:</c> query qualifier
/// (see <c>Doc/DataMesh/QuerySyntax.md</c>) so partition-level and
/// node-level participation share one model. A partition opts into a
/// context by including the name in
/// <see cref="IPartitionStorageProvider.Contexts"/>; consumers
/// running with <c>context:&lt;X&gt;</c> skip every partition whose
/// context set excludes <c>X</c> — no global fan-out, no per-node
/// post-filter for partitions the operation already skipped.
/// </summary>
public static class PartitionContexts
{
    /// <summary>Free-text search across nodes (global search bar).</summary>
    public const string Search = "search";

    /// <summary>NodeTypes / node listings in create menus.</summary>
    public const string Create = "create";

    /// <summary>Path / name autocomplete in chat / mention pickers.</summary>
    public const string Autocomplete = "autocomplete";

    /// <summary>Browseable in tree views, partition listings.</summary>
    public const string Browse = "browse";
}

/// <summary>
/// One backend in the partition routing table. Routing is implicit —
/// each provider's <see cref="Adapter"/> answers
/// <see cref="IStorageAdapter"/>.Write
/// with <c>null</c> when the path isn't theirs, and the routing layer walks
/// the writable list until one accepts. There is no <c>Matches</c> predicate.
///
/// <para><b>Read vs. write split.</b> <see cref="IsReadOnly"/> filters the
/// writable list. Read-only providers (<c>EmbeddedResource</c>,
/// <c>StaticNode</c>) still answer reads through
/// <see cref="MeshWeaver.Mesh.Services.IMeshQueryProvider"/> fan-out;
/// writable providers (InMemory, FileSystem, Postgres, Cosmos, AzureBlob)
/// answer both reads and writes.</para>
///
/// <para><b>What providers must NOT do.</b> Resolve
/// <c>IMessageHub</c> or <c>IMeshQueryCore</c> at construction. Providers
/// are constructed during persistence init, before the singleton
/// <c>IMessageHub</c> factory runs. Re-entering that factory caused the
/// stack overflow that motivated this redesign. Lazy resolution (e.g. on
/// <see cref="System.Reactive.Subjects.ReplaySubject{T}"/> first-subscribe)
/// is fine — by then the hub is up.</para>
///
/// <para>This contract lives in <see cref="MeshWeaver.Mesh.Services"/>
/// (not <c>MeshWeaver.Hosting.Persistence</c>) so node-type registration
/// chains in <c>MeshWeaver.AI</c> / <c>MeshWeaver.Graph</c> can register
/// a partition provider directly without taking a transitive dep on
/// <c>MeshWeaver.Hosting</c>.</para>
/// </summary>
public interface IPartitionStorageProvider
{
    /// <summary>
    /// Stable name used in diagnostics / partition listings.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// <c>true</c> = read-only seed (EmbeddedResource, StaticNode). Excluded
    /// from the write-attempt chain.
    /// <c>false</c> = writable backend (InMemory, FileSystem, Postgres,
    /// Cosmos, AzureBlob); included in the chain.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Storage adapter for this provider. The adapter's
    /// <see cref="IStorageAdapter.Write"/> returns <c>IObservable&lt;MeshNode?&gt;</c>
    /// — <c>null</c> means "not my path, try the next provider." Reads can
    /// also short-circuit (return null/empty) when the adapter knows it
    /// doesn't own the path.
    /// </summary>
    IStorageAdapter Adapter { get; }

    /// <summary>
    /// Optional partition definition emitted to the routing layer so
    /// <see cref="PartitionDefinition"/> consumers (Global Settings,
    /// Schema view) can list this partition. Wildcard providers
    /// typically return null because the partition list is data-driven
    /// (one entry per discovered first-segment).
    /// </summary>
    PartitionDefinition? PartitionDefinition => null;

    /// <summary>
    /// Claim precedence among providers of the same specificity band:
    /// <c>PersistenceService</c> walks specific (fixed-namespace) providers
    /// before wildcards, and within each band higher <see cref="Priority"/>
    /// claims first (ties keep registration order). DURABLE backends
    /// (Postgres, FileSystem, Cosmos, AzureBlob) return <c>100</c> so they
    /// always beat the in-memory wildcard catch-all (default <c>0</c>) that
    /// <c>AddOrleansMeshServices</c> registers as a baseline — without this,
    /// registration order decided, and a host that wired its durable backend
    /// AFTER the Orleans defaults silently persisted every node into RAM
    /// (the 2026-06-11 atioz create-loss: acked, searchable nowhere, gone on
    /// restart).
    /// </summary>
    int Priority => 0;

    /// <summary>
    /// Builds an <see cref="IStorageAdapter"/> scoped to a specific
    /// <c>(def, table)</c> pair. Called by the partition-storage-hub layer
    /// when it spawns a per-table hub for <c>(def.Schema, table)</c>.
    /// <para>For Postgres / Cosmos, each <c>(schema, table)</c> gets a
    /// fresh adapter with its own bounded connection (e.g.
    /// <c>NpgsqlDataSource(MaxPoolSize=1)</c> with <c>SearchPath</c>
    /// set to <paramref name="def"/>.Schema). For static / read-only
    /// providers the table dimension is degenerate and the provider
    /// may return the same shared <see cref="Adapter"/> for every
    /// table.</para>
    /// </summary>
    IStorageAdapter CreateAdapterForTable(PartitionDefinition def, string table) => Adapter;

    /// <summary>
    /// Eagerly provisions whatever backing store a top-level partition needs
    /// (e.g. the Postgres schema + tables) BEFORE any read- or write-shaped touch,
    /// so a brand-new partition root never hits a "relation does not exist" race.
    /// Idempotent. Emits exactly once and completes.
    ///
    /// <para><b>Reactive surface — no <c>await</c>.</b> Any actual I/O (the Postgres
    /// <c>CREATE SCHEMA</c> round-trip) stays at the IO boundary inside the
    /// implementation (<c>Observable.FromAsync(work, Scheduler.Default)</c>, bounded
    /// by Npgsql's pool); consumers compose this into the create-validation chain with
    /// <c>.Concat()</c>/<c>.Subscribe(...)</c>. This is the ONLY trigger for partition
    /// creation — see <c>OwnsPartitionProvisioningValidator</c>.</para>
    ///
    /// <para>Default is a no-op: providers whose storage needs no per-partition
    /// provisioning (InMemory, FileSystem, EmbeddedResource, StaticNode) emit immediately;
    /// the Postgres provider routes to <c>public.ensure_partition_schema</c>.</para>
    /// </summary>
    IObservable<System.Reactive.Unit> EnsurePartitionProvisioned(string @namespace)
        => System.Reactive.Linq.Observable.Return(System.Reactive.Unit.Default);

    /// <summary>
    /// Read-only existence probe — the counterpart to
    /// <see cref="EnsurePartitionProvisioned"/> that NEVER creates anything.
    /// Emits exactly one value and completes: <c>true</c> when this provider knows the
    /// partition's backing store exists, <c>false</c> when it knows it definitively does
    /// NOT, and <c>null</c> when it cannot tell (e.g. InMemory has no per-partition store,
    /// or a transient probe failure). Reactive surface — any actual I/O (the Postgres
    /// schema probe) stays at the IO boundary inside the implementation; consumers compose
    /// it with the rest of the validation chain, no <c>await</c>.
    ///
    /// <para>Used by the write guard that enforces <b>"no partition, no write"</b>:
    /// a normal user write whose top-level partition does not already exist is
    /// rejected rather than silently provisioning a new space (implicit space
    /// creation is forbidden — spaces are created explicitly via the <c>Space</c>
    /// node type). Because a wrong <c>false</c> would block legitimate writes, the
    /// guard rejects ONLY on a definitive <c>false</c> and treats <c>null</c> as
    /// "allow"; providers must therefore emit <c>null</c> — never a guessed
    /// <c>false</c> — whenever they are unsure.</para>
    ///
    /// <para>Default emits <c>null</c>: providers with no per-partition backing store
    /// (InMemory, FileSystem, EmbeddedResource, StaticNode) can't answer and defer
    /// to others; the Postgres provider answers from its schema-existence cache.</para>
    /// </summary>
    IObservable<bool?> PartitionExists(string @namespace)
        => System.Reactive.Linq.Observable.Return<bool?>(null);

    /// <summary>
    /// Tears down whatever backing store this provider holds for a top-level partition —
    /// the inverse of <see cref="EnsurePartitionProvisioned"/>. The Postgres provider drops
    /// the partition's schema (all tables, satellites included) and evicts its provisioning
    /// caches so a later re-create of the same partition provisions from scratch. Idempotent:
    /// deleting a partition whose backing store does not exist emits and completes normally.
    /// Emits exactly once and completes; a genuine failure propagates through OnError.
    ///
    /// <para><b>Reactive surface — no <c>await</c>.</b> Any actual I/O (the Postgres
    /// <c>DROP SCHEMA … CASCADE</c> round-trip) stays at the IO boundary inside the
    /// implementation, bounded by the provider's <c>IIoPool</c>. This is driven by the
    /// partition-owning delete flow (deleting a <c>Space</c>/<c>User</c> root) — the ONE
    /// trigger for partition removal, mirroring how <c>OwnsPartitionProvisioningValidator</c>
    /// is the one trigger for creation.</para>
    ///
    /// <para>Default is a no-op: providers whose storage has no per-partition backing store
    /// (InMemory, EmbeddedResource, StaticNode) emit immediately — the recursive node delete
    /// has already removed their rows.</para>
    /// </summary>
    IObservable<System.Reactive.Unit> DeletePartition(string @namespace)
        => System.Reactive.Linq.Observable.Return(System.Reactive.Unit.Default);

    /// <summary>
    /// Contexts this partition opts into. Consumers iterating
    /// partitions for a given context (search, autocomplete, browse)
    /// skip every partition that doesn't include the context. The
    /// default is "every read context" — partitions that don't want
    /// to be searched/autocompleted explicitly remove the membership.
    /// </summary>
    ImmutableHashSet<string> Contexts =>
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            PartitionContexts.Search,
            PartitionContexts.Create,
            PartitionContexts.Autocomplete,
            PartitionContexts.Browse);
}
