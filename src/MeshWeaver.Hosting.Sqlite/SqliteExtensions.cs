using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Sqlite;

/// <summary>
/// Registration for the SQLite persistence backend — the local-first counterpart to
/// <c>AddPartitionedFileSystemPersistence</c> / <c>AddPartitionedPostgreSqlPersistence</c>.
/// Registers the adapter + a durable <see cref="IPartitionStorageProvider"/> and wires the shared
/// core (PersistenceService + the generic query provider), so queries
/// (<c>path:</c>/<c>nodeType:</c>/<c>namespace:</c>/<c>scope:</c>/free-text) work through
/// <c>StorageAdapterMeshQueryProvider</c>. When an <see cref="ITextEmbedder"/> is also registered
/// (see <see cref="AddSqliteOllamaEmbeddings"/>), writes embed each node and a
/// <see cref="SqliteVectorMeshQuery"/> is fanned in so free-text search + autocomplete rank by
/// vector similarity.
/// </summary>
public static class SqliteExtensions
{
    /// <param name="services">The service collection to register the SQLite persistence backend into.</param>
    /// <param name="connectionString">A Microsoft.Data.Sqlite connection string, e.g.
    /// <c>Data Source=memex.db</c> (a file) or <c>Data Source=:memory:</c> (tests).</param>
    public static IServiceCollection AddPartitionedSqlitePersistence(
        this IServiceCollection services, string connectionString)
    {
        // Factory registration so the adapter picks up an ITextEmbedder (+ logger) from DI when one
        // is wired — embeddings on, vector search lights up; otherwise it stores NULL embeddings and
        // the vector provider stays inert (lexical search unchanged).
        services.AddSingleton<SqliteStorageAdapter>(sp => new SqliteStorageAdapter(
            connectionString,
            sp.GetService<IIoPool>(),
            sp.GetService<ITextEmbedder>(),
            sp.GetService<ILogger<SqliteStorageAdapter>>()));
        services.AddSingleton<IPartitionStorageProvider>(sp =>
            new SqlitePartitionStorageProvider(sp.GetRequiredService<SqliteStorageAdapter>()));
        // Durable event-log store (SQLite) — overrides the in-memory default from AddMeshEventLog(),
        // so the app-level outbox survives restarts on the embedded / offline (MAUI) backend. Uses
        // the same DI-provided IIoPool as the storage adapter (SQLite is single-writer + serialised).
        services.Replace(ServiceDescriptor.Singleton<MeshWeaver.Hosting.IEventLogStore>(sp =>
            new SqliteEventLogStore(connectionString, sp.GetService<IIoPool>())));
        services.AddPartitionedCoreAndWrapperServices();
        // Vector-ranked search + autocomplete, fanned in alongside the pedestrian provider.
        services.AddSingleton<IMeshQueryProvider>(sp => new SqliteVectorMeshQuery(
            sp.GetRequiredService<SqliteStorageAdapter>(),
            sp.GetService<ITextEmbedder>(),
            sp.GetService<IoPoolRegistry>()));
        return services;
    }

    /// <summary>
    /// Registers the SQLite persistence backend on a <see cref="MeshBuilder"/> and wires the mesh
    /// query core onto the mesh hub.
    /// </summary>
    /// <typeparam name="TBuilder">The concrete <see cref="MeshBuilder"/> type, returned for chaining.</typeparam>
    /// <param name="builder">The mesh builder to configure.</param>
    /// <param name="connectionString">A Microsoft.Data.Sqlite connection string, e.g.
    /// <c>Data Source=memex.db</c> (a file) or <c>Data Source=:memory:</c> (tests).</param>
    /// <returns>The same <paramref name="builder"/> for fluent chaining.</returns>
    public static TBuilder AddPartitionedSqlitePersistence<TBuilder>(
        this TBuilder builder, string connectionString)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services => services.AddPartitionedSqlitePersistence(connectionString));
        return builder.RegisterMeshQueryCoreOnMeshHub();
    }

    /// <summary>
    /// Registers a local <b>Ollama</b>-backed <see cref="ITextEmbedder"/> (OpenAI-compatible
    /// <c>/v1/embeddings</c>) so SQLite writes embed nodes and search/autocomplete rank by vector
    /// similarity. <b>No-op when <paramref name="endpoint"/> is empty</b> — the safe default for a
    /// device with no reachable model server (embeddings stay off; lexical search still works).
    /// Call BEFORE / alongside <see cref="AddPartitionedSqlitePersistence(IServiceCollection,string)"/>.
    /// </summary>
    /// <param name="services">The service collection to register the embedder into.</param>
    /// <param name="endpoint">OpenAI-compatible base, e.g. <c>http://localhost:11434/v1</c>.</param>
    /// <param name="model">Embedding model, e.g. <c>bge-m3</c> (1024d) or <c>nomic-embed-text</c> (768d).</param>
    /// <param name="dimensions">Vector dimension override; inferred from <paramref name="model"/> when <c>null</c>.</param>
    /// <param name="timeout">Embedding request timeout; defaults to the embedder's own default when <c>null</c>.</param>
    public static IServiceCollection AddSqliteOllamaEmbeddings(
        this IServiceCollection services, string? endpoint, string model = "bge-m3",
        int? dimensions = null, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return services;
        services.AddSingleton<ITextEmbedder>(
            new OllamaTextEmbedder(endpoint!, model, dimensions, timeout: timeout));
        return services;
    }
}
