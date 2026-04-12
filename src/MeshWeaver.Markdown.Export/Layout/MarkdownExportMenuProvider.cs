using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown.Export.Layout;

/// <summary>
/// Menu provider that contributes "Export to PDF" and "Export to DOCX" menu items
/// to the node action menu. Yields items only when the current node is of type <c>Markdown</c>.
/// </summary>
public static class MarkdownExportMenuProvider
{
    /// <summary>Menu item label/area for the PDF export.</summary>
    public const string PdfLabel = "Export to PDF";
    /// <summary>Menu item label/area for the DOCX export.</summary>
    public const string DocxLabel = "Export to DOCX";

    /// <summary>The provider delegate registered via <c>AddNodeMenuItems</c>.</summary>
    public static async IAsyncEnumerable<NodeMenuItemDefinition> Provide(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodes = await (host.Workspace.GetStream<MeshNode>()
                ?.Select(n => n ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>()))
            .FirstAsync();
        var node = nodes.FirstOrDefault(n => n.Path == hubPath);

        if (node is null || node.NodeType != "Markdown") yield break;

        var perms = await PermissionHelper.GetEffectivePermissionsAsync(host.Hub, hubPath);
        if (!perms.HasFlag(Permission.Read)) yield break;

        yield return new NodeMenuItemDefinition(
            Label: PdfLabel,
            Area: ExportDocumentLayoutArea.PdfArea,
            RequiredPermission: Permission.Read,
            Order: 27,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ExportDocumentLayoutArea.PdfArea));

        yield return new NodeMenuItemDefinition(
            Label: DocxLabel,
            Area: ExportDocumentLayoutArea.DocxArea,
            RequiredPermission: Permission.Read,
            Order: 28,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ExportDocumentLayoutArea.DocxArea));
    }
}
