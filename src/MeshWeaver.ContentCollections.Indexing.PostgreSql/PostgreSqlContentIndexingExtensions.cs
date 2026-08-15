using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.ContentCollections.Indexing.Graph;
using MeshWeaver.Hosting.Embeddings;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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

    /// <summary>
    /// One-call wiring of the pgvector-backed content-indexing pipeline + chunk search against the
    /// MESH database, gated at RESOLVE time on the deployment actually being configured for it —
    /// the boot-pack entry point (<see cref="PostgresContentIndexingModuleAttribute"/>). The image
    /// describer is NOT wired here: the AI package registers the optional <c>IImageDescriber</c>
    /// when present, so this module carries no AI dependency.
    /// </summary>
    public static TBuilder AddPostgreSqlContentIndexing<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
        => builder
            .AddContentIndexingPipeline(
                storeFactory: sp => new PostgreSqlChunkedContentVectorStore(
                    MeshConnectionString(sp)
                        ?? throw new InvalidOperationException(
                            "Content indexing resolved its store without a mesh database connection — " +
                            "the enabledWhen gate should have kept it inert."),
                    sp.GetService<IoPoolRegistry>(),
                    sp.GetRequiredService<IEmbeddingProvider>().Dimensions),
                embedderFactory: sp => new EmbeddingProviderChunkEmbedder(
                    sp.GetRequiredService<IEmbeddingProvider>(),
                    sp.GetService<IoPoolRegistry>()),
                // Extractive summary by default — deployments wanting AI summaries swap the
                // summarizer; images are captioned via the AI package's optional IImageDescriber.
                summarizerFactory: _ => new ExtractiveSummarizer(),
                enabledWhen: IsConfigured)
            .AddContentSearch(IsConfigured);

    /// <summary>
    /// The mesh database connection: the platform's <c>ConnectionStrings:memex</c> convention,
    /// falling back to the registered <see cref="NpgsqlDataSource"/> — the SAME chain
    /// <c>AddPartitionedPostgreSqlPersistence</c> resolves with, so the chunks land in the mesh
    /// database the nodes live in.
    /// </summary>
    private static string? MeshConnectionString(IServiceProvider sp)
        => sp.GetService<IConfiguration>()?.GetConnectionString("memex")
           ?? sp.GetService<NpgsqlDataSource>()?.ConnectionString;

    private static bool IsConfigured(IServiceProvider sp)
    {
        var configuration = sp.GetService<IConfiguration>();
        return !string.IsNullOrWhiteSpace(MeshConnectionString(sp))
            && !string.IsNullOrWhiteSpace(configuration?["Embedding:Endpoint"])
            && !string.IsNullOrWhiteSpace(configuration?["Embedding:ApiKey"])
            && sp.GetService<IEmbeddingProvider>() is not null;
    }
}
