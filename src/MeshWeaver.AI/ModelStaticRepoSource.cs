using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

namespace MeshWeaver.AI;

/// <summary>
/// The built-in model catalog as a static-repo import source for the <c>Provider</c> partition — the
/// same nodes <see cref="BuiltInLanguageModelProvider"/> serves in-memory, materialized into the DB
/// partition on boot so the provider/model catalog is served from the database on the distributed/PG
/// path (Orleans routing does NOT consult the in-memory adapter; without this import the model picker
/// is invisible to <c>namespace:Provider</c> queries). Single-partition like
/// <see cref="AgentStaticRepoSource"/> / <see cref="HarnessStaticRepoSource"/>: every node it emits —
/// the <c>ModelProvider</c> providers at <c>Provider/{name}</c>, their <c>LanguageModel</c> children at
/// <c>Provider/{name}/{modelId}</c>, and the read-only <c>Provider/_Policy</c> — lives under
/// <c>Provider</c>. The built-in provider is only a sync SOURCE; once imported, the DB is the catalog
/// of record.
/// </summary>
public sealed class ModelStaticRepoSource(BuiltInLanguageModelProvider provider) : IStaticRepoSource
{
    /// <inheritdoc />
    // Single partition: the whole catalog (providers + models + the PublicRead _Policy) lives under
    // "Provider". The activity lock lives at "Provider/_Activity/import-*".
    public string Partition => ModelProviderNodeType.RootNamespace; // "Provider"

    /// <inheritdoc />
    // The catalog is config-derived → fingerprint on content, so a changed catalog re-imports.
    public bool Versioned => false;

    /// <inheritdoc />
    // ALL catalog nodes go to the partition — provider/model content AND the read-only _Policy
    // (PartitionAccessPolicy). On the SYNCED path the in-memory provider that served the policy is
    // gated off, so the policy MUST be imported or the partition has no read policy → its nodes are
    // unreadable (the Harness wedge — OrleansHarnessPartitionPublicReadTest). Every node the provider
    // emits is already under the "Provider" partition, so no cross-partition filtering is needed.
    public IReadOnlyList<MeshNode> EnumerateSourceNodes() =>
        provider.GetStaticNodes().ToArray();

    /// <inheritdoc />
    public MeshNode? PartitionRoot => new(ModelProviderNodeType.RootNamespace)
    {
        Name = "Providers",
        Icon = "/static/NodeTypeIcons/database.svg",
        NodeType = "Space",
        State = MeshNodeState.Active,
        Content = new MarkdownContent
        {
            Content = """
                # Providers

                The AI model providers and language models available to this deployment. Browse the
                catalog to see each provider's models and capabilities; platform admins manage the
                shared providers (endpoints, keys, enabled models) here.
                """
        }
    };
}
