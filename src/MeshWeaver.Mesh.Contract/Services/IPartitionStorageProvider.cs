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
/// each provider's <see cref="Adapter"/> answers <see cref="IStorageAdapter.Write"/>
/// with <c>null</c> when the path isn't theirs, and the
/// <see cref="MeshWeaver.Hosting.Persistence.PersistenceService"/> walks the
/// writable list until one accepts. There is no <c>Matches</c> predicate.
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
    /// from the write-attempt chain in
    /// <see cref="MeshWeaver.Hosting.Persistence.PersistenceService.Write"/>.
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
