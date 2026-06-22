using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Sqlite;

/// <summary>
/// Registration for the SQLite persistence backend — the local-first counterpart to
/// <c>AddPartitionedFileSystemPersistence</c> / <c>AddPartitionedPostgreSqlPersistence</c>.
/// Registers the adapter + a durable <see cref="IPartitionStorageProvider"/> and wires the shared
/// core (PersistenceService + the generic query provider), so queries
/// (<c>path:</c>/<c>nodeType:</c>/<c>namespace:</c>/<c>scope:</c>/free-text) work through
/// <c>StorageAdapterMeshQueryProvider</c> with no SQLite-specific SQL generator.
/// </summary>
public static class SqliteExtensions
{
    /// <param name="connectionString">A Microsoft.Data.Sqlite connection string, e.g.
    /// <c>Data Source=memex.db</c> (a file) or <c>Data Source=:memory:</c> (tests).</param>
    public static IServiceCollection AddPartitionedSqlitePersistence(
        this IServiceCollection services, string connectionString)
    {
        var adapter = new SqliteStorageAdapter(connectionString);
        services.AddSingleton(adapter);
        services.AddSingleton<IPartitionStorageProvider>(new SqlitePartitionStorageProvider(adapter));
        return services.AddPartitionedCoreAndWrapperServices();
    }

    public static TBuilder AddPartitionedSqlitePersistence<TBuilder>(
        this TBuilder builder, string connectionString)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services => services.AddPartitionedSqlitePersistence(connectionString));
        return builder.RegisterMeshQueryCoreOnMeshHub();
    }
}
