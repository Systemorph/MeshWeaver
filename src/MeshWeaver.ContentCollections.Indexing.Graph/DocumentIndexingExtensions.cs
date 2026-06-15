using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.ContentCollections.Indexing.Graph;

/// <summary>
/// Wires the framework <c>Document</c> NodeType into a mesh: registers the <c>Document</c> NodeType,
/// the <see cref="MeshDocumentSink"/> (writes indexed-file summaries as <c>Document</c> mesh nodes),
/// and — when a chat client is supplied — the <see cref="ChatClientSummarizer"/>.
///
/// <para>The indexing core (<c>ContentIndexingService</c>) writes a <c>Document</c> only when BOTH an
/// <see cref="ISummarizer"/> and an <see cref="IDocumentSink"/> are resolved; this method registers
/// the sink unconditionally and the summarizer when a chat client factory is given, so the document
/// branch lights up exactly when both are wired.</para>
/// </summary>
public static class DocumentIndexingExtensions
{
    /// <summary>
    /// Registers the <c>Document</c> NodeType + <see cref="MeshDocumentSink"/> as mesh-scoped
    /// singletons (their lifetime IS the mesh). Does NOT register a summarizer — use this overload
    /// when the host registers its own <see cref="ISummarizer"/>, or when only the chunk-embed branch
    /// is wanted.
    /// </summary>
    public static TBuilder AddDocumentIndexing<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {
        builder.AddDocumentType();
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IDocumentSink>(sp =>
                new MeshDocumentSink(sp.GetRequiredService<IMessageHub>()));
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Registers the <c>Document</c> NodeType, <see cref="MeshDocumentSink"/>, AND a
    /// <see cref="ChatClientSummarizer"/> backed by the host-supplied <see cref="IChatClient"/>.
    /// The <paramref name="chatClientFactory"/> resolves the model client from the mesh
    /// <see cref="IServiceProvider"/> (this codebase has no globally-registered <see cref="IChatClient"/>
    /// — the host owns model selection), and the summarizer routes its single completion call through
    /// the <see cref="IoPoolNames.Http"/> pool.
    /// </summary>
    public static TBuilder AddDocumentIndexing<TBuilder>(
        this TBuilder builder,
        Func<IServiceProvider, IChatClient> chatClientFactory)
        where TBuilder : MeshBuilder
    {
        ArgumentNullException.ThrowIfNull(chatClientFactory);

        builder.AddDocumentIndexing();
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ISummarizer>(sp =>
                new ChatClientSummarizer(
                    chatClientFactory(sp),
                    sp.GetRequiredService<IoPoolRegistry>()));
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Registers the <see cref="ContentChunkAutocompleteProvider"/> so indexed chunk content is
    /// searchable from <c>@</c>-autocomplete, each hit resolving to its <c>Document</c> node. Requires
    /// an <see cref="IChunkedContentVectorStore"/> and an <see cref="IChunkEmbedder"/> already in DI
    /// (the host wires the concrete store/embedder — e.g. the Postgres/pgvector adapter); registered via
    /// <c>TryAddEnumerable</c>, the same pattern every other <see cref="IAutocompleteProvider"/> uses.
    ///
    /// <para>The collection scope is derived from the autocomplete <c>contextPath</c> (and its ancestor
    /// prefixes) — a query typed inside a namespace searches that namespace's indexed collections and
    /// the partitions above it. Over-supplied candidate paths are harmless: the store returns nothing
    /// for a collection it holds no chunks for.</para>
    /// </summary>
    public static TBuilder AddContentSearch<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services =>
        {
            // A FACTORY-based IAutocompleteProvider cannot go through TryAddEnumerable: that API dedupes
            // by implementation type, and a factory descriptor's implementation type IS the service type
            // (IAutocompleteProvider) — which .NET rejects as "indistinguishable from other services
            // registered for IAutocompleteProvider". Since AddContentSearch is called exactly once per
            // host, a plain AddScoped into the IAutocompleteProvider enumerable is correct (GetServices
            // returns it alongside the concrete-typed providers).
            services.AddScoped<IAutocompleteProvider>(sp =>
                new ContentChunkAutocompleteProvider(
                    sp.GetRequiredService<IChunkedContentVectorStore>(),
                    sp.GetRequiredService<IChunkEmbedder>(),
                    CollectionScopeFromContext));
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Default collection-scope resolver: the context path itself plus every ancestor prefix, so a
    /// reference typed in <c>part/Space/Sub</c> searches collections keyed at <c>part/Space/Sub</c>,
    /// <c>part/Space</c>, and <c>part</c>. Returns an empty scope when there is no context (a bare,
    /// context-free query has no collection to anchor on).
    /// </summary>
    internal static IReadOnlyCollection<string> CollectionScopeFromContext(string? contextPath)
    {
        if (string.IsNullOrWhiteSpace(contextPath))
            return [];

        var scope = new List<string>();
        var path = contextPath.Trim().Trim('/');
        while (path.Length > 0)
        {
            scope.Add(path);
            var lastSlash = path.LastIndexOf('/');
            if (lastSlash <= 0)
                break;
            path = path[..lastSlash];
        }
        return scope;
    }
}
