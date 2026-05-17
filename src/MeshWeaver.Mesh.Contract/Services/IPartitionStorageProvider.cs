using System.Collections.Immutable;
using System.Reactive.Linq;

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
/// <para><b>Why <see cref="Matches"/> and <see cref="ResolveDefinition"/> are
/// observable.</b> Partition existence changes at runtime (organization
/// creation, user onboarding, partition drop). Returning <c>IObservable&lt;bool&gt;</c>
/// lets backends back the predicate with a <see cref="System.Reactive.Subjects.ReplaySubject{T}"/>
/// populated from <c>IMeshQueryCore.ObserveQuery</c> (Postgres watches
/// <c>Admin/Partition/*</c>; in-memory providers emit a constant). Callers
/// (PersistenceService, PathRoutingAdapter) compose with <c>SelectMany</c>;
/// no blocking <c>.Wait()</c> anywhere — the routing path is observable
/// end-to-end.</para>
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
    /// Emits <c>true</c> while this provider owns <paramref name="fullPath"/>'s
    /// partition. Live — re-emits when the underlying partition catalog
    /// (e.g. <c>Admin/Partition/*</c> or schema existence) changes.
    /// <para>The <b>full path</b> is passed (not just the first segment) so
    /// providers can branch on multi-segment prefixes — e.g. one provider
    /// routes <c>Admin/Partition/*</c> to Postgres while another routes
    /// <c>Admin/Settings/*</c> to embedded resources.</para>
    /// <para>Implementations that don't change over time (Embedded, Static,
    /// the in-memory catch-all) return <c>Observable.Return(predicate)</c>.
    /// Implementations driven by a partition catalog
    /// (Postgres wildcard) back the observable with a
    /// <see cref="System.Reactive.Subjects.ReplaySubject{T}"/> fed by an
    /// <c>ObserveQuery</c> subscription.</para>
    /// </summary>
    /// <param name="fullPath">Full node path. Implementations are responsible
    /// for case-insensitive comparison if they need it.</param>
    IObservable<bool> Matches(string fullPath);

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
    /// when <c>Matches(p)</c> emits true, this emits the definition;
    /// otherwise null.
    /// <para>The router uses <see cref="MeshWeaver.Mesh.PartitionDefinition.Schema"/>
    /// (or <see cref="MeshWeaver.Mesh.PartitionDefinition.Namespace"/>)
    /// and <see cref="MeshWeaver.Mesh.PartitionDefinition.ResolveTable"/>
    /// to derive the <c>(schema, table)</c> hub key.</para>
    /// <para>Default implementation emits the single
    /// <see cref="PartitionDefinition"/> property — appropriate for
    /// single-namespace static providers (Embedded, StaticNode). Backends
    /// that track many partitions (Postgres wildcard) override this with
    /// a per-first-segment <see cref="System.Reactive.Subjects.ReplaySubject{T}"/>.</para>
    /// </summary>
    IObservable<PartitionDefinition?> ResolveDefinition(string fullPath) =>
        Observable.Return(PartitionDefinition);

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
    /// Iteration priority within the routing table — higher = evaluated
    /// first. Used by <c>PersistenceService.Resolve</c> to disambiguate
    /// when more than one wildcard provider would claim a path.
    /// <para>Convention:</para>
    /// <list type="bullet">
    ///   <item><b>100</b> — schema-aware wildcard (e.g.
    ///     <c>PostgreSqlPartitionStorageProvider</c>). <c>Matches</c> only
    ///     emits true for partitions that actually exist in the backend,
    ///     so it can sit ahead of catch-all wildcards without stealing
    ///     paths that don't belong to it.</item>
    ///   <item><b>0</b> (default) — catch-all wildcard (InMemory,
    ///     FileSystem). <c>Matches</c> always emits true for non-empty
    ///     first segments. Must sit after schema-aware wildcards so
    ///     a Postgres-backed namespace doesn't accidentally route to
    ///     an empty in-memory adapter.</item>
    /// </list>
    /// Providers with a fixed <see cref="PartitionDefinition.Namespace"/>
    /// (Embedded, Static) are evaluated <i>before</i> any wildcard
    /// regardless of <see cref="Priority"/>; the priority only orders
    /// providers within the wildcard band.
    /// </summary>
    int Priority => 0;
}
