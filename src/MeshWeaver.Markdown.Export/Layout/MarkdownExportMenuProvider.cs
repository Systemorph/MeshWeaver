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

    // Icons are EMOJI, matching every other node-menu entry (✏️ 🔖 ➡️ 📋 🗑️ 📁 🧾 🕘 ♻️ ✉️ 🔄).
    // A Fluent icon NAME must never be used here: the renderer treats a non-emoji value as an image
    // URL and would emit a broken <img src="DocumentPdf">. These three sat icon-less, which is what
    // made the export block read as a foreign group wedged between two iconed ones.

    /// <summary>Icon for the PDF export — a page, the thing the export produces.</summary>
    public const string PdfIcon = "📄";

    /// <summary>Icon for the DOCX export — a written document, distinct from the plain PDF page.</summary>
    public const string DocxIcon = "📝";

    /// <summary>
    /// Icon for the email share — an outbox tray. Deliberately NOT an envelope: ✉️ already belongs
    /// to "Invite people" in this same menu, and two envelopes at 16 px are indistinguishable.
    /// The tray also says the right thing — this entry SENDS the document out.
    /// </summary>
    public const string SendIcon = "📤";

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
                        Icon: PdfIcon,
                        RequiredPermission: Permission.Read,
                        Order: 27,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ExportDocumentLayoutArea.PdfArea))
                        { LabelKey = "menu.exportPdf" },
                    new NodeMenuItemDefinition(
                        Label: DocxLabel,
                        Area: ExportDocumentLayoutArea.DocxArea,
                        Icon: DocxIcon,
                        RequiredPermission: Permission.Read,
                        Order: 28,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ExportDocumentLayoutArea.DocxArea))
                        { LabelKey = "menu.exportDocx" },
                    // Sits with the export entries as the third member of the same block: the two
                    // above produce a FILE, this one delivers the document itself. It is the
                    // former "Send to contacts" grown up — same entry, same slot, now able to put
                    // the rendered document in the email BODY rather than only attaching a PDF.
                    new NodeMenuItemDefinition(
                        Label: SendDocumentLayoutArea.SendLabel,
                        Area: SendDocumentLayoutArea.SendArea,
                        Icon: SendIcon,
                        RequiredPermission: Permission.Read,
                        Order: 29,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, SendDocumentLayoutArea.SendArea))
                        { LabelKey = "menu.sendToContacts" },
                ];
            });
    }
}
