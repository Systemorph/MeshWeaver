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
/// One rule in the partition routing table. Rules are evaluated in
/// registration order; the first <see cref="Matches"/> hit owns the
/// node-path's partition. Configuration is flat and sequential — it
/// reads exactly like a routing table:
/// <code>
/// mesh.AddEmbeddedResourcePartition("Doc", asm, "MeshWeaver.Documentation.Data");
/// mesh.AddFileSystemPartition("Northwind", "./data/northwind");
/// mesh.AddPostgresPartitionPattern("*");        // catch-all
/// </code>
///
/// <para><b>Why first-match-wins.</b> The earlier model used
/// <see cref="PartitionDefinition.DataSource"/> string discriminators
/// inside the routing core to pick between static / writable / etc.
/// That branched on string equality and special-cased <c>"static"</c>;
/// adding a new backend forced editing the routing core. Sequential
/// rules let new backends plug in by registering a provider with their
/// match predicate — the routing core stays generic.</para>
///
/// <para><b>What providers must NOT do.</b> Resolve
/// <c>IMessageHub</c> or <c>IMeshQueryCore</c>. Providers are
/// constructed during persistence init, before / during the singleton
/// <c>IMessageHub</c> factory runs. Re-entering that factory caused
/// the stack-overflow that motivated this redesign.</para>
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
    /// True if the given node path belongs to this provider. Implementations
    /// match exact namespaces, multi-segment prefixes (e.g.
    /// <c>Admin/Partition/*</c>), or a wildcard for catch-all.
    /// <para>The <b>full path</b> is passed (not just the first segment) so
    /// providers can branch on multi-segment prefixes — e.g. one provider
    /// routes <c>Admin/Partition/*</c> to Postgres while another routes
    /// <c>Admin/Settings/*</c> to embedded resources.</para>
    /// </summary>
    /// <param name="fullPath">Full node path. Implementations are responsible
    /// for case-insensitive comparison if they need it.</param>
    bool Matches(string fullPath);

    /// <summary>
    /// Storage adapter backing every partition that resolves to this
    /// rule. The adapter may be shared across partition keys when the
    /// rule is a wildcard / prefix; in that case the routing layer
    /// uses one <c>AdapterPersistenceService</c> per first-segment
    /// with the same shared adapter (mirrors how the legacy
    /// FileSystemPartitionedStoreFactory works).
    /// <para><b>Deprecated</b> by <see cref="CreateAdapterForTable"/> as
    /// part of the per-(schema,table) hub redesign. Kept on the
    /// interface during migration so existing providers still compile.</para>
    /// </summary>
    IStorageAdapter Adapter { get; }

    /// <summary>
    /// Optional partition definition emitted to the routing layer so
    /// <see cref="PartitionDefinition"/> consumers (Global Settings,
    /// Schema view) can list this partition. Wildcard providers
    /// typically return null because the partition list is
    /// data-driven (one entry per discovered first-segment).
    /// </summary>
    PartitionDefinition? PartitionDefinition => null;

    /// <summary>
    /// Returns the <see cref="PartitionDefinition"/> that owns
    /// <paramref name="fullPath"/>. Paired with <see cref="Matches"/>:
    /// when <c>Matches(p)</c> is true, this returns the definition;
    /// otherwise null.
    /// <para>The router uses <see cref="MeshWeaver.Mesh.PartitionDefinition.Schema"/>
    /// (or <see cref="MeshWeaver.Mesh.PartitionDefinition.Namespace"/>)
    /// and <see cref="MeshWeaver.Mesh.PartitionDefinition.ResolveTable"/>
    /// to derive the <c>(schema, table)</c> hub key.</para>
    /// <para>Default implementation returns the single
    /// <see cref="PartitionDefinition"/> property — appropriate for
    /// single-namespace static providers (Embedded, StaticNode). Backends
    /// that track many partitions (Postgres wildcard) override this with
    /// a dictionary lookup keyed on <c>GetFirstSegment(fullPath)</c>.</para>
    /// </summary>
    PartitionDefinition? ResolveDefinition(string fullPath) => PartitionDefinition;

    /// <summary>
    /// Builds an <see cref="IStorageAdapter"/> scoped to a specific
    /// <c>(def, table)</c> pair. Called by the routing layer when it
    /// needs to spawn a partition-storage hub for <c>(def.Schema, table)</c>.
    /// <para>For Postgres / Cosmos, each <c>(schema, table)</c> gets a
    /// fresh adapter with its own bounded connection (e.g.
    /// <c>NpgsqlDataSource(MaxPoolSize=1)</c> with <c>SearchPath</c>
    /// set to <paramref name="def"/>.Schema). For static / read-only
    /// providers the table dimension is degenerate and the provider
    /// may return the same shared <see cref="Adapter"/> for every
    /// table.</para>
    /// <para>Default implementation returns <see cref="Adapter"/> so
    /// providers that haven't migrated to per-table adapters still work.</para>
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

    /// <summary>
    /// Native <see cref="IMeshQueryProvider"/> for this partition's data.
    /// Cross-partition query fan-out (<c>RoutingMeshQueryProvider</c>)
    /// iterates providers and asks each for its query layer.
    /// <para>Returning <c>null</c> tells the routing layer "no native query
    /// support" — the fan-out will skip this provider rather than wrap
    /// <see cref="Adapter"/> in a generic adapter-based query provider.
    /// Implementations with a real query layer (Postgres push-down, static
    /// node provider) return their concrete provider.</para>
    /// </summary>
    IMeshQueryProvider? QueryProvider => null;

    /// <summary>
    /// Optional <see cref="IVersionQuery"/> for the partition. Returning
    /// <c>null</c> means this partition doesn't track per-node versions.
    /// </summary>
    IVersionQuery? VersionQuery => null;
}
