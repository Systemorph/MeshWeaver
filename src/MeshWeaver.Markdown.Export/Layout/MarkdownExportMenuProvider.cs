using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;

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

    public string Context => NodeMenuItemsExtensions.NodeMenuContext;

    public async IAsyncEnumerable<NodeMenuItemDefinition> GetItemsAsync(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodeStream = host.Workspace.GetStream<MeshNode>()
                ?.Select(n => n ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        // Bridges the live MeshNode + permission streams into the IAsyncEnumerable
        // contract: first snapshot wins via early yield break (the menu pipeline is
        // one-shot per render today). TODO: emit live menu updates when the menu
        // pipeline becomes fully reactive.
        await foreach (var nodes in nodeStream.ToAsyncEnumerableSequence())
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            if (node is null || node.NodeType != "Markdown")
                yield break;

            await foreach (var perms in PermissionHelper.GetEffectivePermissions(host.Hub, hubPath)
                .ToAsyncEnumerableSequence())
            {
                if (!perms.HasFlag(Permission.Read))
                    yield break;

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
                yield break;
            }
            yield break;
        }
    }
}
