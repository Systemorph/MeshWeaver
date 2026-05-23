using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
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
            new PostgreSqlStorageAdapter(_provider.BaseDataSource, embeddingProvider: null, def));
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
