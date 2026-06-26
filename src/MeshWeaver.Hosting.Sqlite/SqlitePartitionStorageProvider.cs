using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Sqlite;

/// <summary>
/// Routes the mesh's partitioned persistence to a single SQLite store. Mirrors
/// <c>FileSystemPartitionStorageProvider</c>: a durable backend (<see cref="Priority"/> 100, so it
/// beats the in-memory wildcard catch-all), one shared adapter for every partition.
///
/// <para>SQLite has no schemas, so every partition lives in one file keyed by path — there is no
/// per-partition provisioning (the schema is created on adapter construction) and
/// <c>CreateAdapterForTable</c> / <c>EnsurePartitionProvisioned</c> / <c>PartitionExists</c> use the
/// interface defaults, exactly as the FileSystem provider does.</para>
/// </summary>
public sealed class SqlitePartitionStorageProvider(SqliteStorageAdapter adapter) : IPartitionStorageProvider
{
    /// <inheritdoc />
    public string Name => "Sqlite";
    /// <inheritdoc />
    public bool IsReadOnly => false;
    /// <inheritdoc />
    public int Priority => 100;
    /// <inheritdoc />
    public IStorageAdapter Adapter { get; } = adapter;
}
