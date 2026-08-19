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
        ContentIndexingOptions? options = null,
        Func<IServiceProvider, IImageDescriber>? imageDescriberFactory = null,
        Func<IServiceProvider, bool>? enabledWhen = null)
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

            // The store + embedder honour the SAME resolve-time gate as the observer below. They used to
            // be registered unconditionally, which made the gate a half-measure: every consumer that
            // resolves IChunkedContentVectorStore (search_chunks, the Document blocks view, the settings
            // tab's explorer) ran the CONCRETE factory on an unconfigured deployment and got that
            // factory's missing-dependency exception — memex's `search_chunks` failing with
            // "The requested service 'IEmbeddingProvider' has not been registered" (#1642) instead of
            // the documented {count:0, message} envelope. Inert stand-ins keep resolution total, and
            // ContentIndexAvailability maps them back to the null every consumer already branches on.
            services.AddSingleton<IChunkedContentVectorStore>(sp =>
                enabledWhen is null || enabledWhen(sp)
                    ? storeFactory(sp)
                    : new InertChunkedContentVectorStore(InactiveReason));
            services.AddSingleton<IChunkEmbedder>(sp =>
                enabledWhen is null || enabledWhen(sp)
                    ? embedderFactory(sp)
                    : new InertChunkEmbedder(InactiveReason));
            if (summarizerFactory is not null)
                services.AddSingleton(summarizerFactory);
            if (imageDescriberFactory is not null)
                services.AddSingleton(imageDescriberFactory);

            services.AddSingleton(sp => new ContentIndexingService(
                sp.GetRequiredService<ITextExtractor>(),
                sp.GetRequiredService<IChunkEmbedder>(),
                sp.GetRequiredService<IChunkedContentVectorStore>(),
                options,
                sp.GetService<ILogger<ContentIndexingService>>(),
                // Summarizer + sink are OPTIONAL inputs to the service: both present ⇒ the per-file
                // Document branch runs; either absent ⇒ chunk-embed-store only.
                sp.GetService<ISummarizer>(),
                sp.GetService<IDocumentSink>(),
                // Optional vision describer: when wired, image files are captioned (searchable text +
                // Document summary) instead of extracting to empty.
                sp.GetService<IImageDescriber>()));

            // The upload→Activity reactor. Registered as its concrete type so a host/GUI can resolve it
            // to call ReindexAll(...), AND forwarded to the IContentUploadObserver seam so the same
            // single instance is the upload reactor. A plain AddSingleton (not TryAddEnumerable) because
            // the latter cannot dedupe a forwarding factory by implementation type; this extension is
            // called once per host, so idempotency isn't needed.
            //
            // enabledWhen is the RESOLVE-TIME activation gate (a boot-loaded module has no
            // IConfiguration at install time, so the "is this deployment configured for indexing?"
            // decision cannot be made at registration). Evaluated once, at first resolution:
            // disabled ⇒ the upload seam gets a no-op observer (uploads proceed, nothing indexes —
            // the documented inert-when-unconfigured behavior), while resolving the CONCRETE
            // observer (the GUI's reindex entry point) fails with an actionable message instead of
            // a bare missing-dependency error from deep inside the store factory.
            services.AddSingleton<ContentIndexingObserver>(sp =>
                enabledWhen is null || enabledWhen(sp)
                    ? new ContentIndexingObserver(
                        sp.GetRequiredService<IMessageHub>(),
                        sp.GetRequiredService<ContentIndexingService>(),
                        sp.GetService<ILogger<ContentIndexingObserver>>())
                    : throw new InvalidOperationException(InactiveReason));
            services.AddSingleton<IContentUploadObserver>(sp =>
                enabledWhen is null || enabledWhen(sp)
                    ? sp.GetRequiredService<ContentIndexingObserver>()
                    : InertContentUploadObserver.Instance);

            return services;
        });

        return builder;
    }

    /// <summary>
    /// Why the pipeline is inert on an unconfigured deployment — one sentence, naming what to configure.
    /// It is the message of the reindex entry point's refusal AND the <c>Reason</c> the inert store /
    /// embedder carry into the <c>search_chunks</c> "indexing is off" envelope, so an operator reads the
    /// same actionable line wherever the capability is missing.
    /// </summary>
    public const string InactiveReason =
        "Content indexing is not active on this deployment: the pipeline's activation condition is " +
        "false (typically no mesh database connection string, or no embedding provider — " +
        "Embedding:Endpoint plus Embedding:ApiKey for the cloud provider). Configure both to index content.";

    /// <summary>
    /// The disabled-pipeline stand-in on the upload seam: uploads proceed, nothing indexes.
    /// </summary>
    private sealed class InertContentUploadObserver : IContentUploadObserver
    {
        public static readonly InertContentUploadObserver Instance = new();
        public void OnUploaded(string collectionPath, string filePath) { }
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
