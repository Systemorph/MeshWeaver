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
/// DI-registered <see cref="INodeMenuProvider"/> that contributes "Export to PDF" and
/// "Export to DOCX" items to the Node menu when the focused node is of type
/// <c>Markdown</c>. Registered via <c>TryAddEnumerable</c> so each hub sees exactly one
/// instance even when its configuration lambda runs multiple times — same pattern as
/// <c>IAutocompleteProvider</c>.
/// </summary>
public class MarkdownExportMenuProvider : INodeMenuProvider
{
    /// <summary>Menu item label for the PDF export.</summary>
    public const string PdfLabel = "Export to PDF";
    /// <summary>Menu item label for the DOCX export.</summary>
    public const string DocxLabel = "Export to DOCX";

    /// <summary>The menu context this provider contributes to — the Node menu.</summary>
    public string Context => NodeMenuItemsExtensions.NodeMenuContext;

    /// <summary>
    /// Reactive: combines the live own-node stream with the viewer's effective permissions and
    /// re-projects the export items on every change. Emits an empty slice when the node isn't a
    /// Markdown node or the viewer lacks Read — and re-emits (showing the items) once a runtime
    /// grant propagates, without a reload.
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
                if (node is null || node.NodeType != "Markdown" || !perms.HasFlag(Permission.Read))
                    return (IReadOnlyCollection<NodeMenuItemDefinition>)[];

                return
                [
                    new NodeMenuItemDefinition(
                        Label: PdfLabel,
                        Area: ExportDocumentLayoutArea.PdfArea,
                        RequiredPermission: Permission.Read,
                        Order: 27,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ExportDocumentLayoutArea.PdfArea)),
                    new NodeMenuItemDefinition(
                        Label: DocxLabel,
                        Area: ExportDocumentLayoutArea.DocxArea,
                        RequiredPermission: Permission.Read,
                        Order: 28,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ExportDocumentLayoutArea.DocxArea)),
                    new NodeMenuItemDefinition(
                        Label: SendDocumentLayoutArea.SendLabel,
                        Area: SendDocumentLayoutArea.SendArea,
                        RequiredPermission: Permission.Read,
                        Order: 29,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, SendDocumentLayoutArea.SendArea)),
                ];
            });
    }
}
