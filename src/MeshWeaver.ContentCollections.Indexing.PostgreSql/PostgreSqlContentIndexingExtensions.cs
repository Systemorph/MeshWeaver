using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContentCollections.Indexing.PostgreSql;

/// <summary>
/// Registers the separate-Postgres (pgvector) content vector store + the
/// <see cref="IEmbeddingProvider"/>→<see cref="IChunkEmbedder"/> adapter on a mesh / node hub.
/// </summary>
public static class PostgreSqlContentIndexingExtensions
{
    /// <summary>
    /// Wires the pgvector-backed <see cref="IChunkedContentVectorStore"/> (against
    /// <paramref name="vectorConnectionString"/> — its own DB/server, independent of the mesh's
    /// primary storage Postgres) and the <see cref="EmbeddingProviderChunkEmbedder"/> as
    /// mesh-scoped singletons. Both die with the mesh (their lifetime IS the hub's
    /// <see cref="IServiceProvider"/>), resolving <see cref="IoPoolRegistry"/> and the framework's
    /// <see cref="IEmbeddingProvider"/> from DI.
    ///
    /// <para>The store's <c>vector({dim})</c> column width comes from the registered
    /// <see cref="IEmbeddingProvider.Dimensions"/> so the schema matches the embedder.</para>
    /// </summary>
    public static MessageHubConfiguration AddPostgreSqlContentIndex(
        this MessageHubConfiguration configuration, string vectorConnectionString)
    {
        if (string.IsNullOrWhiteSpace(vectorConnectionString))
            throw new ArgumentException("Vector connection string is required.", nameof(vectorConnectionString));

        return configuration.WithServices(services =>
        {
            services.AddSingleton<IChunkedContentVectorStore>(sp =>
            {
                var embeddingProvider = sp.GetRequiredService<IEmbeddingProvider>();
                return new PostgreSqlChunkedContentVectorStore(
                    vectorConnectionString,
                    sp.GetService<IoPoolRegistry>(),
                    embeddingProvider.Dimensions);
            });

            services.AddSingleton<IChunkEmbedder>(sp =>
                new EmbeddingProviderChunkEmbedder(
                    sp.GetRequiredService<IEmbeddingProvider>(),
                    sp.GetService<IoPoolRegistry>()));

            return services;
        });
    }
}
