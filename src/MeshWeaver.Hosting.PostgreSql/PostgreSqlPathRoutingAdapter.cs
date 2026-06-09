using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Path-routing <see cref="IStorageAdapter"/> facade exposed by
/// <see cref="PostgreSqlPartitionStorageProvider.Adapter"/>. Maps the path's
/// first segment to a per-schema <see cref="PostgreSqlStorageAdapter"/>.
///
/// <para><b>Schema = first path segment.</b> A valid partition segment maps to
/// the Postgres schema <c>seg.ToLowerInvariant()</c> — resolved
/// <i>synchronously</i>, with NO <c>information_schema</c> probe and NO async
/// cache lookup. The router only verifies the segment is routable
/// (<see cref="ResolveSchema"/>'s guards). <b>It NEVER creates a schema</b> — schema
/// creation is eager and gated to partition-owning creates
/// (<c>OwnsPartitionProvisioningValidator</c> →
/// <see cref="PostgreSqlPartitionStorageProvider.EnsurePartitionProvisioned"/>). Existence
/// is therefore established by provisioning, and at the router both reads and writes simply
/// route:</para>
///
/// <list type="bullet">
///   <item><b>Writes</b> (Write / SavePartitionObjects / DeletePartitionObjects)
///     route to the per-schema adapter. If the partition was never provisioned the schema
///     does not exist and the write faults with Postgres <c>42P01</c> — the "no partition,
///     no write" refusal. The router does NOT lazily CREATE SCHEMA for an arbitrary path
///     segment (that was the atioz 45-ghost-schema DB-corruption root cause).</item>
///   <item><b>Reads</b> route straight to the per-schema adapter, which
///     <i>tolerates an absent schema</i>: each read catches Postgres
///     <c>42P01</c> (undefined table/schema) and returns the empty result
///     (null / empty / false). No probe is needed to "know" whether a schema
///     exists — the read simply finds nothing.</item>
/// </list>
///
/// <para><b><c>_</c>-prefix global satellites.</b> Namespaces like
/// <c>_Access</c> / <c>_Activity</c> are managed by <c>DefaultPartitionProvider</c>
/// with explicit schemas (<c>system_access</c>, <c>system_activity</c>, …) — the
/// schema is NOT the lowercased namespace. Those resolve via the provider's
/// registered-partition lookup (populated at boot by the static-partition
/// seeding) and are NEVER lazily created.</para>
/// </summary>
internal sealed class PostgreSqlPathRoutingAdapter : IStorageAdapter
{
    private readonly PostgreSqlPartitionStorageProvider _provider;
    private readonly ConcurrentDictionary<string, PostgreSqlStorageAdapter> _adaptersBySchema =
        new(StringComparer.OrdinalIgnoreCase);

    // Merged in-process change feed across every lazily-created per-schema
    // PostgreSqlStorageAdapter. PathRoutingAdapter is the IStorageAdapter the
    // PartitionStorageProvider exposes; the synced-query layer subscribes here
    // to receive notifications from ANY schema this router fans out to.
    // Without this, the default interface Changes = Observable.Empty drops every
    // change event silently (the same bug pattern VersionWritingStorageAdapter
    // had — f28449035).
    private readonly Subject<DataChangeNotification> _changes = new();
    public IObservable<DataChangeNotification> Changes => _changes.AsObservable();

    public PostgreSqlPathRoutingAdapter(PostgreSqlPartitionStorageProvider provider)
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
        var seg = PostgreSqlPartitionStorageProvider.GetFirstSegment(path);
        if (string.IsNullOrEmpty(seg))
            return null;
        // 🚨 Prod-2026-05-21 regression guard: paths whose first segment is a
        // NodeType name (Thread, AccessAssignment, …) MUST NOT be routed as
        // partition namespaces. Without this, the router would map them to a
        // schema named after the NodeType and CREATE SCHEMA it on first write —
        // surfacing as `relation "thread.mesh_nodes" does not exist` and the
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
        // user/space, the first path segment) becomes a Postgres SCHEMA, so it must be a
        // simple identifier. A URL- or query-string-shaped segment
        // (`login?error=auth_failed`, `search?q=agent&hq=scope%3adescendants`) must NEVER
        // be lazily CREATE SCHEMA'd. Prod 2026-06-05: the atioz DB filled with exactly
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
    /// A partition's first path segment becomes a Postgres schema, so it must be a simple
    /// identifier: start with a letter/digit, then only letters/digits/<c>. - _</c>, ≤63
    /// chars (PG identifier limit). Rejects URL/query-string-shaped segments
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
    /// PostgreSqlStorageAdapter for a (def.Schema), reuse it.
    /// </summary>
    private PostgreSqlStorageAdapter GetOrCreateAdapter(PartitionDefinition def)
    {
        var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;
        return _adaptersBySchema.GetOrAdd(schema!, _ =>
        {
            var adapter = new PostgreSqlStorageAdapter(_provider.BaseDataSource, embeddingProvider: null, def, readGate: _provider.ReadGate);
            // Wire the new per-schema adapter's Changes into the routing
            // adapter's merged feed. Once-per-schema cost — the inner adapter
            // is itself cached in _adaptersBySchema.
            adapter.Changes.Subscribe(_changes);
            return adapter;
        });
    }

    /// <summary>
    /// Adapter for a READ. The per-schema adapter tolerates an absent schema
    /// (catches Postgres <c>42P01</c> → empty result), so no existence check
    /// is needed here — we route as long as the segment is a routable partition.
    /// </summary>
    private PostgreSqlStorageAdapter? AdapterForRead(string path)
        => ResolveSchema(path) is { } def ? GetOrCreateAdapter(def) : null;

    /// <summary>
    /// The CACHED per-schema <see cref="PostgreSqlStorageAdapter"/> for a path's first segment
    /// — the SAME instance whose in-process <c>Changes</c> Subject fires on Write (resolves
    /// <c>_</c>-prefix globals like <c>_Access</c>→<c>system_access</c> via the registered
    /// partition). Null when the segment isn't a routable partition. Lets
    /// <see cref="PostgreSqlPartitionedMeshQuery"/> delegate SCOPED queries to a per-schema
    /// <see cref="PostgreSqlMeshQuery"/> that observes the live change feed.
    /// </summary>
    internal PostgreSqlStorageAdapter? GetSchemaAdapter(string path) => AdapterForRead(path);

    /// <summary>
    /// Cold write pipeline. <b>NEVER creates a schema.</b> Provisioning is eager and
    /// gated to partition-owning creates (<c>OwnsPartitionProvisioningValidator</c> →
    /// <see cref="PostgreSqlPartitionStorageProvider.EnsurePartitionProvisioned"/>) — the
    /// router only routes. A write whose partition was never provisioned routes to a
    /// non-existent schema and faults with Postgres <c>42P01</c> (the "no partition, no
    /// write" refusal) rather than lazily conjuring a ghost schema for an arbitrary path
    /// segment (the atioz 45-ghost-schema corruption). Path resolution is pure; the
    /// no-op observable is returned when the segment isn't a routable partition.
    /// </summary>
    private IObservable<T> RouteWrite<T>(
        string path, Func<PostgreSqlStorageAdapter, IObservable<T>> write, T whenNotRouted)
        => Observable.Defer(() =>
            ResolveSchema(path) is { } def
                ? write(GetOrCreateAdapter(def))
                : Observable.Return(whenNotRouted));

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => AdapterForRead(path) is { } a
            ? a.Read(path, options)
            : Observable.Return<MeshNode?>(null);

    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => RouteWrite<MeshNode?>(node.Path, a => a.Write(node, options), null);

    public IObservable<string> Delete(string path)
        => AdapterForRead(path) is { } a
            ? a.Delete(path)
            : Observable.Return(path);

    public IObservable<bool> Exists(string path)
        => AdapterForRead(path) is { } a
            ? a.Exists(path)
            : Observable.Return(false);

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => AdapterForRead(fullPath) is { } a
            ? a.FindBestPrefixMatch(fullPath, options)
            : Observable.Return<(MeshNode?, int)>((null, 0));

    /// <summary>
    /// Forwards to the per-schema adapter's <see cref="IStorageAdapter.ResolvePath"/>
    /// — PostgreSqlStorageAdapter overrides this with a UNION query across
    /// mesh_nodes + every satellite table named in
    /// <see cref="PartitionDefinition.TableMappings"/>.
    /// </summary>
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => AdapterForRead(fullPath) is { } a
            ? a.ResolvePath(fullPath, options)
            : Observable.Return<(MeshNode?, int)>((null, 0));

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
        => string.IsNullOrEmpty(parentPath)
            ? Observable.Throw<(IEnumerable<string>, IEnumerable<string>)>(
                new NotSupportedException(
                    "Root-level listing is a query concern; use IMeshQueryCore."))
            : AdapterForRead(parentPath) is { } a
                ? a.ListChildPaths(parentPath)
                : Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []));

    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => AdapterForRead(nodePath) is { } a
            ? a.ListPartitionSubPaths(nodePath)
            : Observable.Return(Enumerable.Empty<string>());

    public IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        => AdapterForRead(nodePath) is { } a
            ? a.GetPartitionObjects(nodePath, subPath, options)
            : Observable.Empty<object>();

    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => RouteWrite(nodePath, a => a.SavePartitionObjects(nodePath, subPath, objects, options), Unit.Default);

    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        // Delete on the READ path: never CREATE SCHEMA just to delete nothing.
        // The per-schema adapter tolerates an absent schema (42P01 → no-op).
        => AdapterForRead(nodePath) is { } a
            ? a.DeletePartitionObjects(nodePath, subPath)
            : Observable.Return(Unit.Default);

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => AdapterForRead(nodePath) is { } a
            ? a.GetPartitionMaxTimestamp(nodePath, subPath)
            : Observable.Return<DateTimeOffset?>(null);
}
