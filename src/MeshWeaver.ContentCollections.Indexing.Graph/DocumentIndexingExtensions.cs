using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

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
}
