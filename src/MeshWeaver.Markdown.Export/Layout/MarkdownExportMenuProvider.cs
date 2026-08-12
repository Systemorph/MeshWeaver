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
    // Labels are bare format/action names — PDF, Email, DOCX — not sentences. The icon carries the
    // glyph, the label carries the format, and the TOOLTIP carries the explanation the label no
    // longer does. That is the AGENTS.md-preferred shape (language-neutral glyph + short label +
    // translated tooltip) and it shrinks the translation surface to almost nothing: "PDF" and
    // "DOCX" are format names and are deliberately NOT translated, in either catalog.

    /// <summary>Menu item label for the PDF export. A format name — never translated.</summary>
    public const string PdfLabel = "PDF";

    /// <summary>Menu item label for the DOCX export. A format name — never translated.</summary>
    public const string DocxLabel = "DOCX";

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

    /// <summary>English label of the parent that holds the export/share entries.</summary>
    public const string ExportGroupLabel = "Export";

    /// <summary>Stable area key of the export group — its catalog key and parent-reference key.</summary>
    public static readonly string ExportGroupArea = NodeMenuItemDefinition.GroupArea("Export");

    /// <summary>
    /// Icon for the export group — a package, the thing the three children between them produce.
    /// Distinct at 16 px from all three of its own children (📄 📤 📝).
    /// </summary>
    public const string ExportGroupIcon = "📦";

    /// <summary>
    /// The export/share entries as ONE collapsible parent.
    ///
    /// <para>These three were the largest contiguous block in a node menu that had grown to ~15 flat
    /// entries, and they share a single sentence — "take this document somewhere else" — which is
    /// what makes a sub-menu the right shape rather than another divider. The parent carries
    /// <see cref="NodeMenuItemDefinition.GroupArea"/>, so no renderer will ever let it be activated;
    /// it exists only to open.</para>
    ///
    /// <para>Children keep their own <c>Order</c> (27 PDF / 28 Email / 29 DOCX) — the aggregator sorts
    /// each sub-menu by <c>Order</c> exactly as it sorts the top level, so the block's established
    /// reading order survives the move into the group.</para>
    ///
    /// <para>The area is <c>_group:Export</c> — a group's own stable key, so the <c>MenuPresentation</c>
    /// catalog can re-word, re-icon, re-order or hide THIS group like any other entry, and another entry
    /// can name it as a <c>Parent</c>.</para>
    /// </summary>
    internal static NodeMenuItemDefinition ExportGroup(IReadOnlyList<NodeMenuItemDefinition> children)
        => new(
            Label: ExportGroupLabel,
            Area: ExportGroupArea,
            Icon: ExportGroupIcon,
            RequiredPermission: Permission.Read,
            Order: 27,
            Children: children)
        { LabelKey = "menu.exportGroup", TooltipKey = "menu.exportGroup.tooltip" };

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

                // Order is PDF, Email, DOCX — the sequence the user named them in, not
                // alphabetical and not file-formats-then-actions. Email sits between the two file
                // exports deliberately: it is the third way of getting this document to someone.
                return
                [
                    ExportGroup(
                    [
                    new NodeMenuItemDefinition(
                        Label: PdfLabel,
                        Area: ExportDocumentLayoutArea.PdfArea,
                        Icon: PdfIcon,
                        RequiredPermission: Permission.Read,
                        Order: 27,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ExportDocumentLayoutArea.PdfArea))
                        { LabelKey = "menu.exportPdf", TooltipKey = "menu.exportPdf.tooltip" },
                    new NodeMenuItemDefinition(
                        Label: SendDocumentLayoutArea.SendLabel,
                        Area: SendDocumentLayoutArea.SendArea,
                        Icon: SendIcon,
                        RequiredPermission: Permission.Read,
                        Order: 28,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, SendDocumentLayoutArea.SendArea))
                        { LabelKey = "menu.sendToContacts", TooltipKey = "menu.sendToContacts.tooltip" },
                    new NodeMenuItemDefinition(
                        Label: DocxLabel,
                        Area: ExportDocumentLayoutArea.DocxArea,
                        Icon: DocxIcon,
                        RequiredPermission: Permission.Read,
                        Order: 29,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ExportDocumentLayoutArea.DocxArea))
                        { LabelKey = "menu.exportDocx", TooltipKey = "menu.exportDocx.tooltip" },
                    ]),
                ];
            });
    }
}
