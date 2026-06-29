using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContentCollections.Indexing.Graph;

/// <summary>
/// One-call wiring for the full content-indexing pipeline (STEP 5): the post-upload observer + the
/// indexing Activity + the indexing core (service + extractor + store + embedder + sink + summarizer).
///
/// <para>A host opts in with a single <c>AddContentIndexingPipeline</c> call, supplying
/// the concrete vector store + embedder (e.g. the Postgres/pgvector adapter via
/// <c>AddPostgreSqlContentIndex</c>) and — optionally — a chat client for the AI summarizer. The
/// <c>Document</c> NodeType + <see cref="MeshDocumentSink"/> come from <see cref="DocumentIndexingExtensions.AddDocumentIndexing{TBuilder}(TBuilder)"/>.</para>
///
/// <para>Once registered, an upload through the standard content-upload path (<c>MeshOperations.Upload</c>
/// → <c>ContentCollection.SaveFileAsync</c>) raises <see cref="IContentUploadObserver"/>; the registered
/// <see cref="ContentIndexingObserver"/> fires an Activity that reads the file bytes via the FileSystem
/// I/O-pool and runs <see cref="ContentIndexingService.IndexFile"/> — never inline on the upload handler.</para>
/// </summary>
public static class ContentIndexingPipelineExtensions
{
    /// <summary>
    /// Registers the indexing core + the upload→Activity observer. The <paramref name="storeFactory"/>
    /// and <paramref name="embedderFactory"/> supply the concrete vector store + embedder (host-owned —
    /// e.g. pgvector); <paramref name="summarizerFactory"/> is optional (null ⇒ chunk-embed-store only,
    /// no Document summary). The <c>Document</c> NodeType + sink are wired via
    /// <see cref="DocumentIndexingExtensions.AddDocumentIndexing{TBuilder}(TBuilder)"/>.
    /// </summary>
    public static TBuilder AddContentIndexingPipeline<TBuilder>(
        this TBuilder builder,
        Func<IServiceProvider, IChunkedContentVectorStore> storeFactory,
        Func<IServiceProvider, IChunkEmbedder> embedderFactory,
        Func<IServiceProvider, ISummarizer>? summarizerFactory = null,
        ContentIndexingOptions? options = null)
        where TBuilder : MeshBuilder
    {
        ArgumentNullException.ThrowIfNull(storeFactory);
        ArgumentNullException.ThrowIfNull(embedderFactory);

        // Document NodeType + MeshDocumentSink (the per-file Document branch). The sink is registered
        // unconditionally; the summarizer below decides whether the document branch lights up.
        builder.AddDocumentIndexing();

        builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<ITextExtractor>(sp =>
                new TextExtractor(
                    sp.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem),
                    sp.GetService<ILogger<TextExtractor>>()));

            services.AddSingleton(storeFactory);
            services.AddSingleton(embedderFactory);
            if (summarizerFactory is not null)
                services.AddSingleton(summarizerFactory);

            services.AddSingleton(sp => new ContentIndexingService(
                sp.GetRequiredService<ITextExtractor>(),
                sp.GetRequiredService<IChunkEmbedder>(),
                sp.GetRequiredService<IChunkedContentVectorStore>(),
                options,
                sp.GetService<ILogger<ContentIndexingService>>(),
                // Summarizer + sink are OPTIONAL inputs to the service: both present ⇒ the per-file
                // Document branch runs; either absent ⇒ chunk-embed-store only.
                sp.GetService<ISummarizer>(),
                sp.GetService<IDocumentSink>()));

            // The upload→Activity reactor. Registered as its concrete type so a host/GUI can resolve it
            // to call ReindexAll(...), AND forwarded to the IContentUploadObserver seam so the same
            // single instance is the upload reactor. A plain AddSingleton (not TryAddEnumerable) because
            // the latter cannot dedupe a forwarding factory by implementation type; this extension is
            // called once per host, so idempotency isn't needed.
            services.AddSingleton<ContentIndexingObserver>(sp => new ContentIndexingObserver(
                sp.GetRequiredService<IMessageHub>(),
                sp.GetRequiredService<ContentIndexingService>(),
                sp.GetService<ILogger<ContentIndexingObserver>>()));
            services.AddSingleton<IContentUploadObserver>(sp => sp.GetRequiredService<ContentIndexingObserver>());

            return services;
        });

        return builder;
    }

    /// <summary>
    /// Convenience overload: wire the pipeline with an AI summarizer backed by a host-supplied
    /// <see cref="IChatClient"/> (routed through the <see cref="IoPoolNames.Http"/> pool by
    /// <see cref="ChatClientSummarizer"/>).
    /// </summary>
    public static TBuilder AddContentIndexingPipeline<TBuilder>(
        this TBuilder builder,
        Func<IServiceProvider, IChunkedContentVectorStore> storeFactory,
        Func<IServiceProvider, IChunkEmbedder> embedderFactory,
        Func<IServiceProvider, IChatClient> chatClientFactory,
        ContentIndexingOptions? options = null)
        where TBuilder : MeshBuilder
    {
        ArgumentNullException.ThrowIfNull(chatClientFactory);
        return builder.AddContentIndexingPipeline(
            storeFactory,
            embedderFactory,
            sp => new ChatClientSummarizer(chatClientFactory(sp), sp.GetRequiredService<IoPoolRegistry>()),
            options);
    }
}
