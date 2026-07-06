using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Documentation;

/// <summary>
/// The embedded MeshWeaver documentation as a <see cref="IStaticRepoSource"/> — the import source
/// for the <c>Doc</c> partition. The same nodes <c>DocumentationBackfill</c> indexed for search are
/// here materialized (content + prerender) into the partition by the static-repo import on boot, so
/// docs are served from the DB rather than the in-memory embedded adapter. See
/// <c>Doc/Architecture/StaticRepoImport.md</c>.
/// </summary>
public sealed class DocumentationStaticRepoSource(IServiceProvider serviceProvider) : IStaticRepoSource
{
    /// <inheritdoc />
    public string Partition => DocumentationNodeProvider.RootNamespace; // "Doc"

    /// <inheritdoc />
    // Embedded docs ship Versioned=false → fingerprint on content, so an edited .md re-imports.
    public bool Versioned => false;

    /// <inheritdoc />
    // 🚨 MUST pass the hub's JsonSerializerOptions: doc nodes defined as .json (e.g. Cession.json,
    // a NodeType node) use camelCase keys + polymorphic `$type` content. Deserializing them with
    // default options (case-sensitive, no $type) yields an all-null MeshNode → bare (no NodeType,
    // no Content) → the CreateNode pipeline rejects it ("bare nodes are not allowed") and the whole
    // Doc import aborts. The hub options carry the camelCase + $type config the parser needs.
    // Resolve the hub lazily (at import time, post-construction) to avoid the cyclic-DI overflow
    // DocumentationNodeProvider documents.
    public IReadOnlyList<MeshNode> EnumerateSourceNodes() =>
        DocumentationNodeProvider.LoadIndexableNodes(
            serviceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions);

    /// <inheritdoc />
    // The Unified Path page embeds @@content/logo.svg + @@content/sample.md. Those assets ship in
    // the embedded DocContent collection (Content/DataMesh/UnifiedPath/*); after the node upsert the
    // import copies that folder's direct-child files into the node's runtime "content" collection so
    // the embeds render on a fresh deploy.
    public IReadOnlyList<StaticContentImport> EnumerateContentImports() =>
    [
        new StaticContentImport(
            NodePath: $"{Partition}/DataMesh/UnifiedPath",
            SourceCollection: "DocContent",
            SourcePath: "DataMesh/UnifiedPath",
            TargetCollection: MeshWeaver.ContentCollections.ContentCollectionsExtensions.DefaultCollectionName,
            TargetPath: ""),
    ];

    /// <inheritdoc />
    // The /Doc landing page — a proper Space root with a curated welcome inviting the reader to
    // explore the docs or just start a chat. NodeType is the "Space" string (no portal-assembly
    // dependency); the importer prerenders the MarkdownContent and the Space Overview renders it.
    public MeshNode? PartitionRoot => new(Partition)
    {
        Name = "MeshWeaver Documentation",
        NodeType = "Space",
        Icon = "/static/NodeTypeIcons/document.svg",
        State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = WelcomeMarkdown }
    };

    /// <summary>
    /// The Doc partition landing page. Two clear invitations: browse the documentation, or just
    /// chat. The Space Overview renders a chat input below this body that creates a new thread.
    /// </summary>
    public const string WelcomeMarkdown = """
        # MeshWeaver Documentation

        Welcome to the MeshWeaver platform documentation.

        ## Explore

        Browse the guides from the menu — start with the architecture overview, the data mesh,
        the GUI, or AI integration:

        - **[Architecture](@/Doc/Architecture)** — the actor-model message hub, access control,
          persistence, and deployment.
        - **[Data Mesh](@/Doc/DataMesh)** — node types, query syntax, paths, and CRUD.
        - **[GUI](@/Doc/GUI)** — layout areas, controls, data binding, and reactive views.
        - **[AI](@/Doc/AI)** — agents, MCP, and the MeshPlugin tools.

        ## Or just chat

        Not sure where to look? Ask a question in the chat box below — it starts a new thread, and
        the assistant will search the docs and answer directly.
        """;
}
