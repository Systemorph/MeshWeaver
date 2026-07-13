using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Path-routing <see cref="IStorageAdapter"/> facade exposed by
/// <see cref="SnowflakePartitionStorageProvider.Adapter"/> — the Snowflake port of
/// <c>PostgreSqlPathRoutingAdapter</c>. Maps the path's first segment to a per-schema
/// <see cref="SnowflakeStorageAdapter"/>.
///
/// <para><b>Schema = first path segment.</b> A valid partition segment maps to
/// the Snowflake schema <c>seg.ToLowerInvariant()</c> — resolved
/// <i>synchronously</i>, with NO <c>information_schema</c> probe and NO async
/// cache lookup. The router only verifies the segment is routable
/// (<see cref="ResolveSchema"/>'s guards). <b>It NEVER creates a schema</b> — schema
/// creation is eager and gated to partition-owning creates
/// (<c>OwnsPartitionProvisioningValidator</c> →
/// <see cref="SnowflakePartitionStorageProvider.EnsurePartitionProvisioned"/>). Existence
/// is therefore established by provisioning, and at the router both reads and writes simply
/// route:</para>
///
/// <list type="bullet">
///   <item><b>Writes</b> (Write / SavePartitionObjects / DeletePartitionObjects)
///     route to the per-schema adapter. If the partition was never provisioned the schema
///     does not exist and the write faults with the driver's "does not exist or not
///     authorized" error — the "no partition, no write" refusal. The router does NOT lazily
///     CREATE SCHEMA for an arbitrary path segment (that was the atioz 45-ghost-schema
///     DB-corruption root cause on the PG backend; the same rule holds here).</item>
///   <item><b>Reads</b> route straight to the per-schema adapter, which
///     <i>tolerates an absent schema</i>: each read catches the Snowflake
///     "does not exist or not authorized" object error (the 42P01 twin) and returns
///     the empty result (null / empty / false). No probe is needed to "know" whether
///     a schema exists — the read simply finds nothing.</item>
/// </list>
///
/// <para><b><c>_</c>-prefix global satellites.</b> Namespaces like
/// <c>_Access</c> / <c>_Activity</c> are managed by <c>DefaultPartitionProvider</c>
/// with explicit schemas (<c>system_access</c>, <c>system_activity</c>, …) — the
/// schema is NOT the lowercased namespace. Those resolve via the provider's
/// registered-partition lookup (populated at boot by the static-partition
/// seeding) and are NEVER lazily created.</para>
/// </summary>
internal sealed class SnowflakePathRoutingAdapter : IStorageAdapter
{
    private readonly SnowflakePartitionStorageProvider _provider;

    /// <summary>
    /// One cache entry per schema: the materialised per-schema adapter plus the subscription
    /// that pipes its <see cref="IStorageAdapter.Changes"/> into the router's merged feed —
    /// kept together so <see cref="EvictSchemaAdapter"/> can tear both down when the
    /// partition is deleted. (The PG router caches the bare adapter; Snowflake adds eviction
    /// because <see cref="SnowflakePartitionStorageProvider.DeletePartition"/> drops the
    /// schema and must not leave a stale adapter + change subscription behind.)
    /// </summary>
    private sealed record CachedAdapter(SnowflakeStorageAdapter Adapter, IDisposable ChangesSubscription);

    private readonly ConcurrentDictionary<string, CachedAdapter> _adaptersBySchema =
        new(StringComparer.OrdinalIgnoreCase);

    // Merged in-process change feed across every lazily-created per-schema
    // SnowflakeStorageAdapter. SnowflakePathRoutingAdapter is the IStorageAdapter the
    // PartitionStorageProvider exposes; the synced-query layer subscribes here
    // to receive notifications from ANY schema this router fans out to.
    // Without this, the default interface Changes = Observable.Empty drops every
    // change event silently (the same bug pattern VersionWritingStorageAdapter
    // had — f28449035).
    private readonly Subject<DataChangeNotification> _changes = new();

    /// <inheritdoc/>
    public IObservable<DataChangeNotification> Changes => _changes.AsObservable();

    /// <summary>
    /// The write side of the merged change feed — lets the cross-process change-feed
    /// poller (<see cref="SnowflakeChangeFeedPoller"/>) inject foreign-silo
    /// <see cref="DataChangeNotification"/>s into the SAME feed the in-process per-schema
    /// adapters publish to, so subscribers observe one unified stream regardless of which
    /// silo committed the change. (Snowflake has no LISTEN/NOTIFY; on PG the listener
    /// plays this role.) Mirrors <c>CosmosStorageAdapter.ChangeObserver</c>.
    /// </summary>
    internal IObserver<DataChangeNotification> ChangeObserver => _changes;

    /// <summary>Creates the router over the owning partition storage provider.</summary>
    /// <param name="provider">Supplies the shared connection source, pools, options, capabilities and the registered-partition lookup.</param>
    public SnowflakePathRoutingAdapter(SnowflakePartitionStorageProvider provider)
    {
        _provider = provider;
    }

    /// <summary>
    /// Resolves the routable <see cref="PartitionDefinition"/> for a path's first
    /// segment — synchronously, no DB round-trip. Returns <c>null</c> when the
    /// segment is not a routable partition (the write falls through to the next
    /// provider / the read returns empty).
    /// </summary>
    private PartitionDefinition? ResolveSchema(string path)
    {
        var seg = SnowflakePartitionStorageProvider.GetFirstSegment(path);
        if (string.IsNullOrEmpty(seg))
            return null;
        // 🚨 Prod-2026-05-21 regression guard (PG lineage): paths whose first segment is a
        // NodeType name (Thread, AccessAssignment, …) MUST NOT be routed as
        // partition namespaces. Without this, the router would map them to a
        // schema named after the NodeType and CREATE SCHEMA it on first write —
        // surfacing as `Object 'thread.mesh_nodes' does not exist` and the
        // SatelliteRoutingExhaustive schema-must-not-exist assertion failing for
        // AccessAssignment.
        if (PartitionDefinition.IsSatelliteNodeType(seg))
            return null;
        // 🚨 Global satellite namespaces (`_Access`, `_Activity`, `_Thread`,
        // `_UserActivity`) are managed by DefaultPartitionProvider with explicit
        // schemas (`system_access`, `system_activity`, etc.) — the schema is NOT
        // the lowercased namespace. They are resolved ONLY via the provider's
        // registered-partition lookup (populated at boot by the static-partition
        // seeding); we never lazily create `_access`/`_activity` schemas. If the
        // namespace hasn't been registered yet, it isn't routable → null.
        if (seg.StartsWith('_'))
            return _provider.TryGetRegisteredPartition(seg, out var registered)
                ? registered
                : null;
        // 🚨 Reject segments that are not valid partition names. A partition (= a
        // user/space, the first path segment) becomes a Snowflake SCHEMA, so it must be a
        // simple identifier. A URL- or query-string-shaped segment
        // (`login?error=auth_failed`, `search?q=agent&hq=scope%3adescendants`) must NEVER
        // be lazily CREATE SCHEMA'd. Prod 2026-06-05 (PG): the atioz DB filled with exactly
        // these garbage schemas (request URLs routed as mesh paths) and corrupted itself.
        // → null so no schema is created; the write falls through to the next provider.
        if (!IsValidPartitionSegment(seg))
            return null;
        // Valid partition segment → schema is the lowercased first segment. If a
        // richer PartitionDefinition was registered for it (e.g. a non-default
        // DataSource), reuse that; otherwise synthesise the standard def.
        if (_provider.TryGetRegisteredPartition(seg, out var known))
            return known;
        return new PartitionDefinition
        {
            Namespace = seg,
            DataSource = "default",
            Schema = seg.ToLowerInvariant(),
            Table = "mesh_nodes",
            TableMappings = _provider.SatelliteSegmentMappings(),
            NodeTypeTableMappings = _provider.SatelliteNodeTypeMappings(),
            Versioned = true,
        };
    }

    /// <summary>
    /// A partition's first path segment becomes a Snowflake schema, so it must be a simple
    /// identifier: start with a letter/digit, then only letters/digits/<c>. - _</c>, ≤63
    /// chars (kept identical to the PG rule — same 63-char cap — so a path routable on one
    /// backend is routable on the other). Rejects URL/query-string-shaped segments
    /// (containing <c>? = &amp; % # :</c>, whitespace, …) that the partition router would
    /// otherwise materialise as garbage schemas — the atioz DB-corruption root cause
    /// (2026-06-05). (<c>_</c>-prefixed global-satellite segments are handled before this.)
    /// </summary>
    internal static bool IsValidPartitionSegment(string seg) =>
        seg.Length is > 0 and <= 63
        && char.IsLetterOrDigit(seg[0])
        && seg.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>
    /// Schema-bound adapter cache: once we've materialised the
    /// <see cref="SnowflakeStorageAdapter"/> for a (def.Schema), reuse it. Every adapter
    /// gets the SAME shared <see cref="SnowflakeConnectionSource"/> — Snowflake needs no
    /// per-schema data source; adapters differ only by <see cref="PartitionDefinition.Schema"/>
    /// (every statement is schema-qualified via <see cref="SnowflakeIdentifiers.Qualify"/>).
    /// </summary>
    private SnowflakeStorageAdapter GetOrCreateAdapter(PartitionDefinition def)
    {
        var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;
        return _adaptersBySchema.GetOrAdd(schema!, _ =>
        {
            var adapter = new SnowflakeStorageAdapter(
                _provider.ConnectionSource,
                embeddingProvider: null,
                partitionDefinition: def,
                logger: null,
                readPool: _provider.ReadPool,
                ioPool: _provider.WritePool,
                capabilities: _provider.Capabilities,
                options: _provider.Options);
            // Wire the new per-schema adapter's Changes into the routing
            // adapter's merged feed. Once-per-schema cost — the inner adapter
            // is itself cached in _adaptersBySchema. Accessed through the
            // interface so the default (Observable.Empty) also binds cleanly.
            var subscription = ((IStorageAdapter)adapter).Changes.Subscribe(_changes);
            return new CachedAdapter(adapter, subscription);
        }).Adapter;
    }

    /// <summary>
    /// Evicts (and best-effort disposes) the cached per-schema adapter after its backing
    /// schema was dropped by <see cref="SnowflakePartitionStorageProvider.DeletePartition"/>,
    /// including the subscription that fed its <c>Changes</c> into the merged feed. A later
    /// re-create of the same partition then materialises a fresh adapter instead of reusing
    /// one bound to a dropped schema. No-op when the schema was never routed to.
    /// </summary>
    /// <param name="schema">The (lowercased) schema whose adapter should be evicted.</param>
    internal void EvictSchemaAdapter(string schema)
    {
        if (!_adaptersBySchema.TryRemove(schema, out var cached))
            return;
        cached.ChangesSubscription.Dispose();
        // The shared connection source is DI-owned and outlives any adapter; disposing the
        // adapter (when it is disposable at all) is cache hygiene only.
        (cached.Adapter as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Adapter for a READ. The per-schema adapter tolerates an absent schema
    /// (catches the Snowflake "does not exist or not authorized" object error → empty
    /// result), so no existence check is needed here — we route as long as the segment
    /// is a routable partition.
    /// </summary>
    private SnowflakeStorageAdapter? AdapterForRead(string path)
        => ResolveSchema(path) is { } def ? GetOrCreateAdapter(def) : null;

    /// <summary>
    /// The CACHED per-schema <see cref="SnowflakeStorageAdapter"/> for a path's first segment
    /// — the SAME instance whose in-process <c>Changes</c> Subject fires on Write (resolves
    /// <c>_</c>-prefix globals like <c>_Access</c>→<c>system_access</c> via the registered
    /// partition). Null when the segment isn't a routable partition. Lets the Snowflake
    /// query layer delegate SCOPED queries to a per-schema query provider that observes
    /// the live change feed.
    /// </summary>
    internal SnowflakeStorageAdapter? GetSchemaAdapter(string path) => AdapterForRead(path);

    /// <summary>
    /// Cold write pipeline. <b>NEVER creates a schema.</b> Provisioning is eager and
    /// gated to partition-owning creates (<c>OwnsPartitionProvisioningValidator</c> →
    /// <see cref="SnowflakePartitionStorageProvider.EnsurePartitionProvisioned"/>) — the
    /// router only routes. A write whose partition was never provisioned routes to a
    /// non-existent schema and faults with the driver's "does not exist or not authorized"
    /// error (the "no partition, no write" refusal) rather than lazily conjuring a ghost
    /// schema for an arbitrary path segment (the atioz 45-ghost-schema corruption). Path
    /// resolution is pure; the no-op observable is returned when the segment isn't a
    /// routable partition.
    /// </summary>
    private IObservable<T> RouteWrite<T>(
        string path, Func<SnowflakeStorageAdapter, IObservable<T>> write, T whenNotRouted)
        => Observable.Defer(() =>
            ResolveSchema(path) is { } def
                ? write(GetOrCreateAdapter(def))
                : Observable.Return(whenNotRouted));

    /// <inheritdoc/>
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => AdapterForRead(path) is { } a
            ? a.Read(path, options)
            : Observable.Return<MeshNode?>(null);

    /// <inheritdoc/>
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        // The null emission is the try-then-claim DECLINE signal: PersistenceService
        // walks writable providers in priority order and takes the first non-null;
        // when every provider declines it throws "no writable storage provider
        // accepted the node" (the fail-closed aggregate). Declining here — instead
        // of throwing — lets lower-priority providers claim paths Snowflake doesn't
        // route (invalid partition segments). Still NEVER creates a schema.
        => RouteWrite<MeshNode?>(node.Path, a => a.Write(node, options), null);

    /// <inheritdoc/>
    public IObservable<string> Delete(string path)
        => AdapterForRead(path) is { } a
            ? a.Delete(path)
            : Observable.Return(path);

    /// <inheritdoc/>
    public IObservable<bool> Exists(string path)
        => AdapterForRead(path) is { } a
            ? a.Exists(path)
            : Observable.Return(false);

    /// <inheritdoc/>
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => AdapterForRead(fullPath) is { } a
            ? a.FindBestPrefixMatch(fullPath, options)
            : Observable.Return<(MeshNode?, int)>((null, 0));

    /// <summary>
    /// Forwards to the per-schema adapter's <see cref="IStorageAdapter.ResolvePath"/>
    /// — <see cref="SnowflakeStorageAdapter"/> overrides this with a UNION query across
    /// mesh_nodes + every satellite table named in
    /// <see cref="PartitionDefinition.TableMappings"/>.
    /// </summary>
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => AdapterForRead(fullPath) is { } a
            ? a.ResolvePath(fullPath, options)
            : Observable.Return<(MeshNode?, int)>((null, 0));

    /// <inheritdoc/>
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
        => string.IsNullOrEmpty(parentPath)
            ? Observable.Throw<(IEnumerable<string>, IEnumerable<string>)>(
                new NotSupportedException(
                    "Root-level listing is a query concern; use IMeshQueryCore."))
            : AdapterForRead(parentPath) is { } a
                ? a.ListChildPaths(parentPath)
                : Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []));

    /// <inheritdoc/>
    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => AdapterForRead(nodePath) is { } a
            ? a.ListPartitionSubPaths(nodePath)
            : Observable.Return(Enumerable.Empty<string>());

    /// <inheritdoc/>
    public IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        => AdapterForRead(nodePath) is { } a
            ? a.GetPartitionObjects(nodePath, subPath, options)
            : Observable.Empty<object>();

    /// <inheritdoc/>
    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => RouteWrite(nodePath, a => a.SavePartitionObjects(nodePath, subPath, objects, options), Unit.Default);

    /// <inheritdoc/>
    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        // Delete on the READ path: never CREATE SCHEMA just to delete nothing.
        // The per-schema adapter tolerates an absent schema ("does not exist" → no-op).
        => AdapterForRead(nodePath) is { } a
            ? a.DeletePartitionObjects(nodePath, subPath)
            : Observable.Return(Unit.Default);

    /// <inheritdoc/>
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => AdapterForRead(nodePath) is { } a
            ? a.GetPartitionMaxTimestamp(nodePath, subPath)
            : Observable.Return<DateTimeOffset?>(null);
}
