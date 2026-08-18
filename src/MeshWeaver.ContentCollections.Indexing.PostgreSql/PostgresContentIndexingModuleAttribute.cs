using MeshWeaver.ContentCollections.Indexing.Graph;
using MeshWeaver.Hosting.Embeddings;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

[assembly: MeshWeaver.ContentCollections.Indexing.PostgreSql.PostgresContentIndexingModule]

namespace MeshWeaver.ContentCollections.Indexing.PostgreSql;

/// <summary>
/// Module registration for the pgvector content-indexing pipeline. Listing this DLL under
/// <c>Modules:Assemblies</c> wires the full upload→extract→chunk→embed→store pipeline, the
/// per-file <c>Document</c> nodes, and the <c>@document</c> chunk autocomplete — the vector store
/// living in each partition's OWN schema of the mesh database.
///
/// <para><b>Activation is decided at RESOLVE time, not install time</b>: a module has no
/// <c>IConfiguration</c> when its attribute folds in, so the pipeline registers with the
/// <c>enabledWhen</c> gate — active only when the mesh database connection resolves AND an
/// <see cref="IEmbeddingProvider"/> is registered (which <c>AddEmbeddings</c> does exactly when
/// <c>Embedding:Endpoint</c>, plus <c>Embedding:ApiKey</c> for the cloud provider, is configured).
/// Unconfigured deployments (e.g. the FileSystem monolith) stay inert THROUGHOUT: uploads proceed
/// unindexed, content autocomplete settles empty, and the chunk store/embedder resolve inert
/// stand-ins so <c>search_chunks</c> answers "indexing is off" — never a missing-store error.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class PostgresContentIndexingModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Func<MeshBuilder, MeshBuilder>> BuilderConfigurations =>
        [builder => builder.AddPostgreSqlContentIndexing()];

    /// <summary>
    /// The Content Indexing settings tab on every per-node hub — riding the module so the tab
    /// appears exactly when the deployment lists the pipeline (its layout area self-gates on the
    /// node shape; an unconfigured-but-listed deployment shows it and the reindex entry point
    /// fails with the actionable configuration message).
    /// </summary>
    public override IEnumerable<Func<MessageHubConfiguration, MessageHubConfiguration>> DefaultNodeHubConfigurations =>
        [config => config.AddContentIndexSettingsTab()];
}
