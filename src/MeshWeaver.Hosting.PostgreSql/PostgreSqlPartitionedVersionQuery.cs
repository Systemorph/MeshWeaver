using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Partition-aware <see cref="IVersionQuery"/> for the partitioned PostgreSQL backend.
/// Routes each read to a per-partition <see cref="PostgreSqlVersionQuery"/> over that
/// partition's <b>schema-qualified</b> <c>mesh_node_history</c> table — the schema and
/// data source come from <see cref="PostgreSqlPartitionStorageProvider.GetSchemaAdapter"/>,
/// the same schema-bound adapter the router reads <c>mesh_nodes</c> through. That is the
/// layout <c>public.ensure_partition_schema</c> (<c>GetVersionedPartitionDdl</c>) provisions:
/// history lives in the partition's own schema, populated by the per-schema
/// <c>mesh_node_copy_to_history</c> trigger on every insert/update.
///
/// <para>Without this, <c>AddPartitionedPostgreSqlPersistence</c> falls through to the
/// <c>NoOpVersionQuery</c> that <c>AddPartitionedCoreAndWrapperServices</c> registers, and
/// the portal's "Versions" panel shows <i>"No version history available."</i> for every
/// node even though the trigger has faithfully recorded every version.</para>
///
/// <para>Writes are a no-op: the trigger already snapshots history, so the
/// <c>VersionWritingStorageAdapter</c> decorator must not double-write.</para>
/// </summary>
public sealed class PostgreSqlPartitionedVersionQuery : IVersionQuery
{
    private readonly PostgreSqlPartitionStorageProvider _provider;

    // One PostgreSqlVersionQuery per schema — the schema-bound adapter is itself cached by the
    // routing adapter, so keying on schema reuses a single reader per partition. Instance field
    // (never static): its lifetime is this singleton's.
    private readonly ConcurrentDictionary<string, PostgreSqlVersionQuery> _bySchema =
        new(StringComparer.Ordinal);

    /// <summary>Creates the partition-aware version reader over a partitioned PG storage provider.</summary>
    /// <param name="provider">The provider that maps a path's first segment to its schema-bound adapter.</param>
    public PostgreSqlPartitionedVersionQuery(PostgreSqlPartitionStorageProvider provider)
    {
        _provider = provider;
    }

    private PostgreSqlVersionQuery? For(string path)
    {
        var adapter = _provider.GetSchemaAdapter(path);
        if (adapter?.SchemaName is not { Length: > 0 } schema)
            return null;
        return _bySchema.GetOrAdd(
            schema,
            s => new PostgreSqlVersionQuery(adapter.DataSource, s, _provider.ReadPool));
    }

    /// <summary>
    /// Swallows Postgres <c>42P01</c> (undefined_table) to the supplied empty result — a
    /// partition with no history table yet shows no versions rather than throwing — while
    /// letting every other Postgres error propagate. Mirrors the read adapter's own
    /// missing-schema tolerance.
    /// </summary>
    private static IObservable<T> TolerateMissingHistory<T>(IObservable<T> source, IObservable<T> empty)
        => source.Catch<T, PostgresException>(ex =>
            ex.SqlState == PostgresErrorCodes.UndefinedTable ? empty : Observable.Throw<T>(ex));

    /// <inheritdoc />
    public IObservable<MeshNodeVersion> GetVersions(string path)
        => For(path) is { } q
            ? TolerateMissingHistory(q.GetVersions(path), Observable.Empty<MeshNodeVersion>())
            : Observable.Empty<MeshNodeVersion>();

    /// <inheritdoc />
    public IObservable<MeshNode?> GetVersion(string path, long version, JsonSerializerOptions options)
        => For(path) is { } q
            ? TolerateMissingHistory(q.GetVersion(path, version, options), Observable.Return<MeshNode?>(null))
            : Observable.Return<MeshNode?>(null);

    /// <inheritdoc />
    public IObservable<MeshNode?> GetVersionBefore(string path, long beforeVersion, JsonSerializerOptions options)
        => For(path) is { } q
            ? TolerateMissingHistory(q.GetVersionBefore(path, beforeVersion, options), Observable.Return<MeshNode?>(null))
            : Observable.Return<MeshNode?>(null);

    /// <inheritdoc />
    /// <remarks>No-op — the per-schema <c>mesh_node_copy_to_history</c> trigger owns the write side.</remarks>
    public IObservable<MeshNode> WriteVersion(MeshNode node, JsonSerializerOptions options)
        => Observable.Return(node);
}
