using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Verifies that <see cref="MarkdownExportMenuProvider"/> contributes "Export to PDF" and "Export to DOCX"
/// items to the Node menu (<c>$Menu:Node</c>) when the focused node is of type "Markdown".
/// Regression guard for the menu refactor: items must land in the Node context, not the legacy default <c>$Menu</c>.
/// </summary>
public class MarkdownExportMenuTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Read-only menu fetch tests against statically-seeded TestOrg/TestMarkdown
    // — no node mutation, no permission changes. Safe to share SP.
    protected override bool ShareMeshAcrossTests => true;

    private const string MarkdownNodePath = "TestOrg/TestMarkdown";
    private const string DeckNodePath = "TestOrg/TestDeck";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(
                new MeshNode("TestOrg") { Name = "Test Organization" },
                new MeshNode("TestMarkdown", "TestOrg")
                {
                    Name = "Test Markdown",
                    NodeType = MarkdownNodeType.NodeType
                },
                new MeshNode("TestDeck", "TestOrg")
                {
                    Name = "Test Deck",
                    NodeType = DeckNodeType.NodeType,
                    Content = new DeckContent { Title = "Test Deck" }
                }
            )
            .AddMarkdownExport()
            .ConfigureDefaultNodeHub(c => c.AddDefaultLayoutAreas());

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition));

    /// <summary>
    /// Every entry in the menu, parents AND their descendants, depth-first. The export entries live
    /// under an "Export" parent now, so a test that only looked at the top level would see the group
    /// and none of its contents.
    /// </summary>
    private static IEnumerable<NodeMenuItemDefinition> Flatten(IEnumerable<NodeMenuItemDefinition> items)
    {
        foreach (var item in items)
        {
            yield return item;
            if (item.Children is { Count: > 0 } children)
                foreach (var descendant in Flatten(children))
                    yield return descendant;
        }
    }

    private async Task<IReadOnlyList<NodeMenuItemDefinition>> FetchNodeMenuItems(
        IMessageHub client, Address nodeAddress)
    {
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, reference);

        // The menu renders incrementally: providers that emit synchronously
        // (e.g. "Request Approval") land in an early menu snapshot, while
        // MarkdownExportMenuProvider gates its items on the own-node stream and
        // the viewer's effective permissions (StartWith(null) + CombineLatest),
        // so its export items appear only in a LATER emission once the node
        // loads and Read resolves. Match on the snapshot that actually carries
        // the export items rather than grabbing the first non-null (partial) one.
        var menuControl = await stream
            .GetControlStream(MenuControl.GetMenuArea(NodeMenuItemsExtensions.NodeMenuContext))
            .Should().Within(10.Seconds()).Match(
                x => x is MenuControl m
                     && Flatten(m.Items).Any(i => i.Label == MarkdownExportMenuProvider.PdfLabel));

        return menuControl.Should().BeOfType<MenuControl>().Which.Items;
    }

    [Fact(Timeout = 30000)]
    public async Task MarkdownNode_NodeMenu_ContainsPdfAndDocxExportItems()
    {
        var client = GetClient();
        var nodeAddress = new Address(MarkdownNodePath);

        var items = await FetchNodeMenuItems(client, nodeAddress);
        var all = Flatten(items).ToArray();

        Output.WriteLine($"Node menu items for Markdown node: {items.Count} top-level, {all.Length} total");
        foreach (var item in all)
            Output.WriteLine($"  {item.Label} (Area={item.Area}, Order={item.Order}, Children={item.Children?.Count ?? 0})");

        all.Select(i => i.Label).Should().Contain(MarkdownExportMenuProvider.PdfLabel,
            "MarkdownExportMenuProvider should contribute 'Export to PDF' for nodes with NodeType=Markdown");
        all.Select(i => i.Label).Should().Contain(MarkdownExportMenuProvider.DocxLabel,
            "MarkdownExportMenuProvider should contribute 'Export to DOCX' for nodes with NodeType=Markdown");

        var pdfItem = all.First(i => i.Label == MarkdownExportMenuProvider.PdfLabel);
        pdfItem.Area.Should().Be(ExportDocumentLayoutArea.PdfArea,
            "PDF item must navigate to the PDF export layout area");

        var docxItem = all.First(i => i.Label == MarkdownExportMenuProvider.DocxLabel);
        docxItem.Area.Should().Be(ExportDocumentLayoutArea.DocxArea,
            "DOCX item must navigate to the DOCX export layout area");
    }

    /// <summary>
    /// The export/share entries are ONE collapsible "Export" parent, not three flat rows — the whole
    /// point of the grouping. The parent must be a pure group (so no renderer lets it be activated),
    /// and its children must come out in <c>Order</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MarkdownNode_ExportEntries_AreGroupedUnderOneParent()
    {
        var client = GetClient();
        var items = await FetchNodeMenuItems(client, new Address(MarkdownNodePath));

        // The three actions are NOT at the top level any more…
        items.Select(i => i.Label).Should().NotContain(MarkdownExportMenuProvider.PdfLabel,
            "the export entries moved into the 'Export' group — a flat PDF row means the grouping regressed");

        // …they are the group's children.
        var group = items.Should().ContainSingle(i => i.Label == MarkdownExportMenuProvider.ExportGroupLabel)
            .Which;

        group.Area.Should().Be(MarkdownExportMenuProvider.ExportGroupArea,
            "a grouping parent carries a _group:{name} area so no client can navigate to it — and so the "
            + "MenuPresentation catalog can still address THIS group by a stable key");
        group.IsGroup.Should().BeTrue();
        group.IsSubmenuParent.Should().BeTrue();
        group.Icon.Should().Be(MarkdownExportMenuProvider.ExportGroupIcon);
        group.LabelKey.Should().Be("menu.exportGroup", "the parent label must translate like every other entry");
        group.TooltipKey.Should().Be("menu.exportGroup.tooltip",
            "once the label is one word the tooltip is the only remaining explanation");

        group.Children!.Select(c => c.Label).Should().Equal(
            [MarkdownExportMenuProvider.PdfLabel, SendDocumentLayoutArea.SendLabel, MarkdownExportMenuProvider.DocxLabel],
            "children are sorted by Order (27 PDF / 28 Email / 29 DOCX) — the block's reading order "
            + "survives the move into the group");

        group.Children!.Should().BeInAscendingOrder(c => c.Order);
    }

    /// <summary>
    /// A Deck offers PDF + email but no DOCX — and still gets the SAME parent, so the two node types
    /// present their export actions in the same place.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task DeckNode_ExportEntries_UseTheSameGroup()
    {
        var client = GetClient();
        var items = await FetchNodeMenuItems(client, new Address(DeckNodePath));

        var group = items.Should().ContainSingle(i => i.Label == MarkdownExportMenuProvider.ExportGroupLabel).Which;
        group.Area.Should().Be(MarkdownExportMenuProvider.ExportGroupArea);
        group.Children!.Select(c => c.Label).Should().Equal(
            [MarkdownExportMenuProvider.PdfLabel, SendDocumentLayoutArea.SendLabel]);
    }

    [Fact(Timeout = 30000)]
    public async Task DeckNode_NodeMenu_ContainsPdfExportItem_NotDocx()
    {
        var client = GetClient();
        var nodeAddress = new Address(DeckNodePath);

        var items = await FetchNodeMenuItems(client, nodeAddress);
        var all = Flatten(items).ToArray();

        Output.WriteLine($"Node menu items for Deck node: {items.Count} top-level, {all.Length} total");
        foreach (var item in all)
            Output.WriteLine($"  {item.Label} (Area={item.Area}, Order={item.Order})");

        all.Select(i => i.Label).Should().Contain(MarkdownExportMenuProvider.PdfLabel,
            "DeckExportMenuProvider should contribute 'Export to PDF' for nodes with NodeType=Deck");
        // A deck carries no markdown body of its own, so DOCX export (which renders the node's own
        // content) is deliberately NOT offered.
        all.Select(i => i.Label).Should().NotContain(MarkdownExportMenuProvider.DocxLabel,
            "a Deck exposes PDF export only — DOCX would render the deck's (empty) own body");

        var pdfItem = all.First(i => i.Label == MarkdownExportMenuProvider.PdfLabel);
        pdfItem.Area.Should().Be(ExportDocumentLayoutArea.PdfArea,
            "PDF item must navigate to the PDF export layout area");
    }

    /// <summary>
    /// The export group reads <b>PDF, Email, DOCX</b> — bare format/action names, in that sequence,
    /// each with a tooltip.
    ///
    /// <para>Order is asserted as a SEQUENCE rather than as three <c>Order</c> values, because the
    /// number is an implementation detail and the sequence is the thing the user asked for; a
    /// renumbering that preserves the reading order should not fail, and one that scrambles it
    /// must.</para>
    ///
    /// <para>The tooltip assertion is the other half of the shape: once a label is shortened to
    /// "PDF", the tooltip is the ONLY place left that says what the entry does, so an entry that
    /// loses it becomes unexplainable rather than merely terse.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ExportGroup_ReadsPdfEmailDocx_InThatOrder_EachWithATooltip()
    {
        var client = GetClient();
        var items = await FetchNodeMenuItems(client, new Address(MarkdownNodePath));

        // Flatten: the three now live inside the 📦 Export sub-menu rather than at top level. What
        // this test pins is unchanged by that — the reading order and the tooltips — so it looks
        // through the tree rather than caring which level they sit on.
        var group = Flatten(items)
            .Where(i => i.Area is ExportDocumentLayoutArea.PdfArea
                        or ExportDocumentLayoutArea.DocxArea
                        or SendDocumentLayoutArea.SendArea)
            .OrderBy(i => i.Order)
            .ToArray();

        foreach (var item in group)
            Output.WriteLine($"  {item.Order}: {item.Icon} {item.Label} — \"{item.Tooltip}\"");

        group.Select(i => i.Label).Should().Equal(
            MarkdownExportMenuProvider.PdfLabel,      // "PDF"
            SendDocumentLayoutArea.SendLabel,         // "Email"
            MarkdownExportMenuProvider.DocxLabel);    // "DOCX"

        group.Where(i => string.IsNullOrWhiteSpace(i.Tooltip)).Select(i => i.Label)
            .Should().BeEmpty(
                "a bare format name is only usable when the tooltip carries the explanation");
    }

    /// <summary>
    /// Every node-menu entry carries an icon, and it is an EMOJI.
    ///
    /// <para>Asserted as an invariant over the whole menu rather than item by item, because the
    /// defect it guards is precisely an entry that joins the menu without one: the export/share
    /// block shipped icon-less and read as a foreign group wedged between two iconed ones. A
    /// per-item assertion would have to be remembered for each new entry; this one cannot be
    /// forgotten, since any icon-less addition fails it.</para>
    ///
    /// <para>The emoji check is not cosmetic. The renderer treats a non-emoji value as an image
    /// URL (<c>&lt;img src="…"&gt;</c>), so a Fluent icon NAME — the natural wrong guess, and what
    /// two dead <c>Icon:</c> values elsewhere in the codebase still contain — renders as a broken
    /// image rather than failing loudly.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task EveryNodeMenuItem_HasAnEmojiIcon()
    {
        var client = GetClient();
        var items = await FetchNodeMenuItems(client, new Address(MarkdownNodePath));

        // Dividers are the one legitimately icon-less, label-less kind.
        // Descends into Children: nested entries are just as visible to the user as top-level ones,
        // and before sub-menus rendered at all this invariant silently skipped every one of them.
        var actionable = Flatten(items).Where(i => i.Area != NodeMenuItemDefinition.SeparatorArea).ToArray();

        // Non-vacuity: the export/share entries this test exists for are actually in the slice.
        actionable.Select(i => i.Label).Should().Contain(
            new[]
            {
                MarkdownExportMenuProvider.PdfLabel,
                MarkdownExportMenuProvider.DocxLabel,
                SendDocumentLayoutArea.SendLabel
            },
            "otherwise the invariant below would pass without ever seeing the group it guards");

        foreach (var item in actionable)
            Output.WriteLine($"  {item.Icon ?? "(none)"} {item.Label} (Order={item.Order})");

        actionable.Where(i => string.IsNullOrEmpty(i.Icon)).Select(i => i.Label)
            .Should().BeEmpty("every node-menu entry is icon + label — none may render bare");

        actionable.Where(i => !MeshNodeImageHelper.IsEmoji(i.Icon)).Select(i => $"{i.Label}={i.Icon}")
            .Should().BeEmpty(
                "icons here must be emoji; a Fluent icon name would render as a broken <img src=\"Name\">");
    }
}
