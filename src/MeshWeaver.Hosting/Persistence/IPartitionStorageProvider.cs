using System.Collections.Immutable;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

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
/// inside <c>RoutingPersistenceServiceCore.InitializeAsync</c> to pick
/// between static / writable / etc. That branched on string equality
/// and special-cased <c>"static"</c>; adding a new backend forced
/// editing the routing core. Sequential rules let new backends plug
/// in by registering a provider with their match predicate — the
/// routing core stays generic.</para>
///
/// <para><b>What providers must NOT do.</b> Resolve
/// <c>IMessageHub</c> or <c>IMeshQueryCore</c>. Providers are
/// constructed during persistence init, before / during the singleton
/// <c>IMessageHub</c> factory runs. Re-entering that factory caused
/// the stack-overflow that motivated this redesign.</para>
/// </summary>
public interface IPartitionStorageProvider
{
    /// <summary>
    /// Stable name used in diagnostics / partition listings.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// True if the given path's first segment belongs to this
    /// partition. Implementations match exact namespace, prefix list,
    /// or wildcard <c>*</c> for "anything else".
    /// </summary>
    /// <param name="firstSegment">First path segment of the node
    /// path, lowercased by the caller for case-insensitive match.</param>
    bool Matches(string firstSegment);

    /// <summary>
    /// Storage adapter backing every partition that resolves to this
    /// rule. The adapter may be shared across partition keys when the
    /// rule is a wildcard / prefix; in that case the routing layer
    /// uses one <see cref="InMemoryPersistenceService"/> per first-
    /// segment with the same shared adapter (mirrors how the legacy
    /// FileSystemPartitionedStoreFactory works).
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
