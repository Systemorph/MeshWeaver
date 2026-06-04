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
/// first segment to a per-schema <see cref="PostgreSqlStorageAdapter"/>
/// according to the <see cref="PgPartitionCache"/> state.
///
/// <para><b>Try-then-claim contract.</b> Every method returns null/empty
/// when the cache reports <see cref="PartitionState.Absent"/> so the outer
/// chain in <see cref="MeshWeaver.Hosting.Persistence.PersistenceService"/>
/// can fall through to the next writable provider.</para>
///
/// <para><b>Lazy schema creation.</b> A Write to a namespace whose cache
/// state is <see cref="PartitionState.PendingCreate"/> calls
/// <see cref="PostgreSqlPartitionStorageProvider.EnsureSchemaForPartitionSync"/>,
/// which CREATE SCHEMAs + initialises tables on-demand. Reads against a
/// PendingCreate namespace return null/empty (nothing to read until the
/// first write).</para>
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

    private IObservable<PartitionState> ResolveState(string path)
    {
        var seg = PostgreSqlPartitionStorageProvider.GetFirstSegment(path);
        if (string.IsNullOrEmpty(seg))
            return Observable.Return<PartitionState>(new PartitionState.Absent());
        // 🚨 Prod-2026-05-21 regression guard: paths whose first segment is a
        // NodeType name (Thread, AccessAssignment, …) MUST NOT be probed as
        // partition namespaces. Without this, the cache probes for a schema
        // named after the NodeType, gets PendingCreate, and AdapterForWriteState
        // CREATE SCHEMAs it on first write — surfacing as `relation
        // "thread.mesh_nodes" does not exist` and the SatelliteRoutingExhaustive
        // schema-must-not-exist assertion failing for AccessAssignment.
        if (PartitionDefinition.NodeTypeToSuffix.ContainsKey(seg))
            return Observable.Return<PartitionState>(new PartitionState.Absent());
        // 🚨 Global satellite namespaces (`_Access`, `_Activity`, `_Thread`,
        // `_UserActivity`) are managed by DefaultPartitionProvider with explicit
        // schemas (`system_access`, `system_activity`, etc.) — the schema is NOT
        // the lowercased namespace. The cache's probe queries information_schema
        // for `_access` which doesn't exist (real schema is `system_access`), so
        // probe returns PendingCreate. If we let AdapterForWriteState lazy-create
        // from that, we'd build a competing `_access` schema. Instead, demote
        // PendingCreate → Absent for `_`-prefixed segments: writes are accepted
        // only when the static-partition path has populated the cache with the
        // real Exists(def with Schema="system_access").
        if (seg.StartsWith('_'))
        {
            return _provider.PartitionCache.GetOrProbe(seg)
                .Take(1)
                .Select(state => state is PartitionState.Exists
                    ? state
                    : (PartitionState)new PartitionState.Absent());
        }
        return _provider.PartitionCache.GetOrProbe(seg).Take(1);
    }

    /// <summary>
    /// Schema-bound adapter cache: once we've materialised the
    /// PostgreSqlStorageAdapter for a (def.Schema), reuse it. Distinct from
    /// the partition-cache (which holds the state observable per namespace);
    /// this holds the concrete adapter per schema.
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

    private PostgreSqlStorageAdapter? AdapterForReadState(PartitionState state) => state switch
    {
        PartitionState.Exists e => GetOrCreateAdapter(e.Def),
        _ => null,
    };

    private PostgreSqlStorageAdapter? AdapterForWriteState(PartitionState state)
    {
        switch (state)
        {
            case PartitionState.Exists e:
                return GetOrCreateAdapter(e.Def);
            case PartitionState.PendingCreate p:
                var def = new PartitionDefinition
                {
                    Namespace = p.FirstSegment,
                    DataSource = "default",
                    Schema = p.FirstSegment.ToLowerInvariant(),
                    Table = "mesh_nodes",
                    TableMappings = PartitionDefinition.StandardTableMappings,
                    Versioned = true,
                };
                _provider.EnsureSchemaForPartitionSync(def);
                return GetOrCreateAdapter(def);
            default:
                return null;
        }
    }

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => ResolveState(path).SelectMany(state => AdapterForReadState(state) is { } a
            ? a.Read(path, options)
            : Observable.Return<MeshNode?>(null));

    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => ResolveState(node.Path).SelectMany(state => AdapterForWriteState(state) is { } a
            ? a.Write(node, options)
            : Observable.Return<MeshNode?>(null));

    public IObservable<string> Delete(string path)
        => ResolveState(path).SelectMany(state => AdapterForReadState(state) is { } a
            ? a.Delete(path)
            : Observable.Return(path));

    public IObservable<bool> Exists(string path)
        => ResolveState(path).SelectMany(state => AdapterForReadState(state) is { } a
            ? a.Exists(path)
            : Observable.Return(false));

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => ResolveState(fullPath).SelectMany(state => AdapterForReadState(state) is { } a
            ? a.FindBestPrefixMatch(fullPath, options)
            : Observable.Return<(MeshNode?, int)>((null, 0)));

    /// <summary>
    /// Forwards to the per-schema adapter's <see cref="IStorageAdapter.ResolvePath"/>
    /// — PostgreSqlStorageAdapter overrides this with a UNION query across
    /// mesh_nodes + every satellite table named in
    /// <see cref="PartitionDefinition.TableMappings"/>.
    /// </summary>
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => ResolveState(fullPath).SelectMany(state => AdapterForReadState(state) is { } a
            ? a.ResolvePath(fullPath, options)
            : Observable.Return<(MeshNode?, int)>((null, 0)));

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
        => string.IsNullOrEmpty(parentPath)
            ? Observable.Throw<(IEnumerable<string>, IEnumerable<string>)>(
                new NotSupportedException(
                    "Root-level listing is a query concern; use IMeshQueryCore."))
            : ResolveState(parentPath).SelectMany(state => AdapterForReadState(state) is { } a
                ? a.ListChildPaths(parentPath)
                : Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], [])));

    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => ResolveState(nodePath).SelectMany(state => AdapterForReadState(state) is { } a
            ? a.ListPartitionSubPaths(nodePath)
            : Observable.Return(Enumerable.Empty<string>()));

    public IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        => ResolveState(nodePath).SelectMany(state => AdapterForReadState(state) is { } a
            ? a.GetPartitionObjects(nodePath, subPath, options)
            : Observable.Empty<object>());

    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => ResolveState(nodePath).SelectMany(state => AdapterForWriteState(state) is { } a
            ? a.SavePartitionObjects(nodePath, subPath, objects, options)
            : Observable.Return(Unit.Default));

    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => ResolveState(nodePath).SelectMany(state => AdapterForReadState(state) is { } a
            ? a.DeletePartitionObjects(nodePath, subPath)
            : Observable.Return(Unit.Default));

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => ResolveState(nodePath).SelectMany(state => AdapterForReadState(state) is { } a
            ? a.GetPartitionMaxTimestamp(nodePath, subPath)
            : Observable.Return<DateTimeOffset?>(null));
}
