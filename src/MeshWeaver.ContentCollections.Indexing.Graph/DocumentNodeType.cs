using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;

namespace MeshWeaver.ContentCollections.Indexing.Graph;

/// <summary>
/// The registered <c>Document</c> mesh NodeType — a first-class, browsable node type (like
/// <c>Space</c>, <c>Code</c>, <c>Markdown</c>) whose content is the indexing core's
/// <see cref="Indexing.Document"/> record: one indexed source file's AI summary plus its indexing
/// metadata. <see cref="MeshDocumentSink"/> creates/updates instances of this type at the
/// deterministic path <see cref="DocumentPaths.For"/>.
/// </summary>
public static class DocumentNodeType
{
    /// <summary>The NodeType value used to identify Document nodes.</summary>
    public const string NodeType = "Document";

    /// <summary>
    /// Inline document SVG (uses <c>currentColor</c> so it follows the theme). A sheet of paper
    /// with a folded corner and a few text lines — the universal "document" glyph.
    /// </summary>
    public const string IconSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 16 16\" fill=\"currentColor\">" +
        "<path d=\"M3.5 1A1.5 1.5 0 0 0 2 2.5v11A1.5 1.5 0 0 0 3.5 15h9a1.5 1.5 0 0 0 1.5-1.5V5.621a1.5 1.5 0 0 0-.44-1.06l-3.12-3.122A1.5 1.5 0 0 0 9.378 1H3.5Zm0 1H9v2.5A1.5 1.5 0 0 0 10.5 6H13v7.5a.5.5 0 0 1-.5.5h-9a.5.5 0 0 1-.5-.5v-11a.5.5 0 0 1 .5-.5Zm6.5.207L12.793 5H10.5a.5.5 0 0 1-.5-.5V2.207ZM4.75 8a.75.75 0 0 0 0 1.5h6.5a.75.75 0 0 0 0-1.5h-6.5Zm0 3a.75.75 0 0 0 0 1.5h4a.75.75 0 0 0 0-1.5h-4Z\"/>" +
        "</svg>";

    /// <summary>
    /// Registers the built-in <c>Document</c> MeshNode on the mesh builder. Idempotent at the
    /// <see cref="MeshBuilder"/> level via the standard <c>AddMeshNodes</c> path.
    /// </summary>
    public static TBuilder AddDocumentType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        return builder;
    }

    /// <summary>
    /// Creates the registered <c>Document</c> NodeType MeshNode. Its content type is the indexing
    /// core's <see cref="Indexing.Document"/> record; the per-node hub renders the AI summary +
    /// metadata via <see cref="DocumentLayoutAreas.Overview"/>.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Document",
        Icon = IconSvg,
        // A Document is primary content (an indexed file's summary), not satellite metadata —
        // browsable and addressable like Markdown/Code.
        IsSatelliteType = false,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<Indexing.Document>())
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout
                .WithView(MeshNodeLayoutAreas.OverviewArea, DocumentLayoutAreas.Overview)
                .WithView(DocumentLayoutAreas.BlocksArea, DocumentLayoutAreas.Blocks)
                .WithView(DocumentLayoutAreas.SourceArea, DocumentLayoutAreas.Source))
    };
}
