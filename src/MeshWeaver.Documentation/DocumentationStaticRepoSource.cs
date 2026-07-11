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
        Icon = DocIcon,
        State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = WelcomeMarkdown }
    };

    /// <summary>
    /// The Doc space icon: a mesh of nodes above an open book — the docs ARE nodes in the mesh.
    /// Inline SVG (renderable wherever a node icon appears; never a bare Fluent icon name).
    /// </summary>
    public const string DocIcon =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'>"
        + "<rect width='48' height='48' rx='10' fill='#1a2744'/>"
        + "<path d='M10 33c4-2 9-2 13 0 4-2 9-2 13 0V17c-4-2-9-2-13 0-4-2-9-2-13 0z' fill='none' stroke='#9fb6dd' stroke-width='2' stroke-linejoin='round'/>"
        + "<path d='M23 17v16' stroke='#9fb6dd' stroke-width='2'/>"
        + "<path d='M15 12 24 7l9 5' fill='none' stroke='#5c8fd6' stroke-width='1.6'/>"
        + "<circle cx='15' cy='12' r='3' fill='#7cc0ff'/><circle cx='24' cy='7' r='3' fill='#fff'/><circle cx='33' cy='12' r='3' fill='#7cc0ff'/>"
        + "</svg>";

    /// <summary>
    /// The Doc partition landing page — a cover that teases the documentation: a hero, one card
    /// per doc section, the most-read pages, and the chat invitation. Raw HTML blocks pass
    /// through Markdig; styles use the theme variables so dark and light mode both work.
    /// </summary>
    public const string WelcomeMarkdown = """
        <div style="border-radius:18px;padding:36px 40px;margin:4px 0 22px;
                    background:linear-gradient(135deg,#1a2744 0%,#24406e 55%,#2e5a9e 100%);color:#fff;">
          <div style="font-size:13px;letter-spacing:.14em;text-transform:uppercase;opacity:.75;">MeshWeaver</div>
          <h1 style="margin:6px 0 10px;font-size:34px;line-height:1.15;color:#fff;">Documentation</h1>
          <p style="margin:0;max-width:640px;font-size:16px;line-height:1.55;opacity:.92;">
            Everything in MeshWeaver is a <strong>node in a mesh</strong> — data, views, agents, even
            these docs. Learn how hubs pass messages, how layout areas render reactively, and how a
            NodeType compiles live from source.
          </p>
          <p style="margin:18px 0 0;">
            <a href="/Doc/Architecture" style="display:inline-block;background:#fff;color:#1a2744;font-weight:600;
               padding:9px 18px;border-radius:8px;text-decoration:none;">Start with Architecture →</a>
            <a href="/Doc/DataMesh/CreatingNodeTypes" style="display:inline-block;margin-left:10px;color:#fff;font-weight:600;
               padding:9px 14px;border:1px solid rgba(255,255,255,.5);border-radius:8px;text-decoration:none;">Build a NodeType</a>
          </p>
        </div>

        <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:14px;margin:0 0 24px;">
          <a href="/Doc/Architecture" style="text-decoration:none;color:inherit;background:var(--neutral-layer-2);
             border:1px solid var(--neutral-stroke-rest);border-radius:14px;padding:18px 20px;display:block;">
            <div style="font-size:24px;">🏛️</div>
            <div style="font-weight:700;margin:6px 0 4px;">Architecture</div>
            <div style="font-size:13px;color:var(--neutral-foreground-hint);line-height:1.5;">
              The actor-model message hub, partitions &amp; routing, access control, persistence, deployment.</div>
          </a>
          <a href="/Doc/DataMesh" style="text-decoration:none;color:inherit;background:var(--neutral-layer-2);
             border:1px solid var(--neutral-stroke-rest);border-radius:14px;padding:18px 20px;display:block;">
            <div style="font-size:24px;">🕸️</div>
            <div style="font-weight:700;margin:6px 0 4px;">Data Mesh</div>
            <div style="font-size:13px;color:var(--neutral-foreground-hint);line-height:1.5;">
              Nodes, node types, the unified path, query syntax, CRUD — the data model behind everything.</div>
          </a>
          <a href="/Doc/GUI" style="text-decoration:none;color:inherit;background:var(--neutral-layer-2);
             border:1px solid var(--neutral-stroke-rest);border-radius:14px;padding:18px 20px;display:block;">
            <div style="font-size:24px;">🎛️</div>
            <div style="font-weight:700;margin:6px 0 4px;">GUI</div>
            <div style="font-size:13px;color:var(--neutral-foreground-hint);line-height:1.5;">
              Layout areas, controls, data binding — reactive views streamed from node state.</div>
          </a>
          <a href="/Doc/AI" style="text-decoration:none;color:inherit;background:var(--neutral-layer-2);
             border:1px solid var(--neutral-stroke-rest);border-radius:14px;padding:18px 20px;display:block;">
            <div style="font-size:24px;">🤖</div>
            <div style="font-weight:700;margin:6px 0 4px;">AI</div>
            <div style="font-size:13px;color:var(--neutral-foreground-hint);line-height:1.5;">
              Agents, threads, skills and MCP — chat with any node, automate with the mesh tools.</div>
          </a>
        </div>

        ## Most-read pages

        - **[Plugins](@/Doc/Architecture/Plugins)** — node-native modules the mesh compiles live; no rebuild, no NuGet.
        - **[NodeType Compilation & Releases](@/Doc/Architecture/NodeTypeCompilation)** — what triggers a compile, how releases activate.
        - **[Data Binding](@/Doc/GUI/DataBinding)** — the one right way to bind, edit and persist node data in views.
        - **[Asynchronous Calls](@/Doc/Architecture/AsynchronousCalls)** — why it's `IObservable<T>` end to end, and where I/O belongs.
        - **[Creating Node Types](@/Doc/DataMesh/CreatingNodeTypes)** — from content record to layout areas, step by step.

        ## Or just ask

        Not sure where to look? Ask in the chat box below — it starts a new thread, and the
        assistant searches these docs and answers with sources.
        """;
}
