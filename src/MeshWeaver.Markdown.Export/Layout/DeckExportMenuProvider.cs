using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown.Export.Layout;

/// <summary>
/// DI-registered <see cref="INodeMenuProvider"/> that contributes "Export to PDF" to the Node
/// menu when the focused node is a <c>Deck</c>. The deck's PDF export renders one page per slide
/// (in the deck's manifest / query order) via the same <c>Templates/Export/Pdf</c> template — the
/// template branches on <c>NodeType == "Deck"</c>. Only PDF is offered: a deck carries no markdown
/// body of its own, so DOCX (which exports the node's own content) would be empty.
/// Registered via <c>TryAddEnumerable</c> so each hub sees exactly one instance — same pattern as
/// <see cref="MarkdownExportMenuProvider"/>.
/// </summary>
public class DeckExportMenuProvider : INodeMenuProvider
{
    /// <summary>The menu context this provider contributes to — the Node menu.</summary>
    public string Context => NodeMenuItemsExtensions.NodeMenuContext;

    /// <summary>
    /// Reactive: combines the live own-node stream with the viewer's effective permissions and
    /// re-projects the export item on every change. Emits an empty slice when the node isn't a
    /// Deck or the viewer lacks Read — and re-emits (showing the item) once a runtime grant
    /// propagates, without a reload.
    /// </summary>
    public IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> GetItems(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();

        // Own MeshNode via the canonical reducer. StartWith(null) so CombineLatest fires before
        // the node loads; Catch degrades to "no node" on hubs without a MeshDataSource.
        var nodeStream = host.Workspace.GetMeshNodeStream()
            .Select(n => (MeshNode?)n)
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .StartWith((MeshNode?)null);

        return nodeStream.CombineLatest(
            host.Hub.GetEffectivePermissions(hubPath),
            (node, perms) =>
            {
                if (node is null || node.NodeType != DeckNodeType.NodeType || !perms.HasFlag(Permission.Read))
                    return (IReadOnlyCollection<NodeMenuItemDefinition>)[];

                return
                [
                    new NodeMenuItemDefinition(
                        Label: MarkdownExportMenuProvider.PdfLabel,
                        Area: ExportDocumentLayoutArea.PdfArea,
                        RequiredPermission: Permission.Read,
                        Order: 27,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ExportDocumentLayoutArea.PdfArea)),
                ];
            });
    }
}
