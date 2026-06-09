using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

namespace MeshWeaver.AI;

/// <summary>
/// The built-in model catalog as a static-repo import source. <see cref="BuiltInLanguageModelProvider"/>
/// emits the read-only <c>_Policy</c> under the <c>Model</c> partition and the
/// <c>ModelProvider</c>/<c>LanguageModel</c> content under the <c>_Provider</c> partition (all
/// derived from <c>IConfiguration</c>). This source materializes ALL of them into their partitions
/// so the catalog is served from the DB on the distributed/PG path. The nodes span two partitions;
/// the importer reads &amp; prunes every partition the source's nodes touch. See
/// <c>Doc/Architecture/StaticRepoImport.md</c>.
/// </summary>
public sealed class ModelStaticRepoSource(BuiltInLanguageModelProvider provider) : IStaticRepoSource
{
    /// <inheritdoc />
    // Logical source id / primary partition — the activity lock lives at "Model/_Activity/import-*".
    // (The source's nodes also include "_Provider" content; the importer is multi-partition aware.)
    public string Partition => LanguageModelNodeType.RootNamespace; // "Model"

    /// <inheritdoc />
    // The catalog is config-derived → fingerprint on content, so a changed catalog re-imports.
    public bool Versioned => false;

    /// <inheritdoc />
    // All catalog nodes go to the partition — provider/model content AND the read-only _Policy
    // (the _Policy is also prune-protected by the governance rule).
    public IReadOnlyList<MeshNode> EnumerateSourceNodes() =>
        provider.GetStaticNodes().ToArray();

    /// <inheritdoc />
    public MeshNode? PartitionRoot => new(LanguageModelNodeType.RootNamespace)
    {
        Name = "Models",
        NodeType = "Space",
        State = MeshNodeState.Active,
        Content = new MarkdownContent
        {
            Content = """
                # Models

                The language models available to this deployment. Browse the catalog to see each
                model's provider and capabilities.
                """
        }
    };
}
