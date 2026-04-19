using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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
    private const string MarkdownNodePath = "TestOrg/TestMarkdown";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(
                new MeshNode("TestOrg") { Name = "Test Organization" },
                new MeshNode("TestMarkdown", "TestOrg")
                {
                    Name = "Test Markdown",
                    NodeType = MarkdownNodeType.NodeType
                }
            )
            .AddMarkdownExport()
            .ConfigureDefaultNodeHub(c => c.AddDefaultLayoutAreas());

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition));

    private async Task<IReadOnlyList<NodeMenuItemDefinition>> FetchNodeMenuItemsAsync(
        IMessageHub client, Address nodeAddress)
    {
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, reference);

        var menuControl = await stream
            .GetControlStream(MenuControl.GetMenuArea(NodeMenuItemsExtensions.NodeMenuContext))
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        return menuControl.Should().BeOfType<MenuControl>().Which.Items;
    }

    [Fact(Timeout = 30000)]
    public async Task MarkdownNode_NodeMenu_ContainsPdfAndDocxExportItems()
    {
        var client = GetClient();
        var nodeAddress = new Address(MarkdownNodePath);

        var items = await FetchNodeMenuItemsAsync(client, nodeAddress);

        Output.WriteLine($"Node menu items for Markdown node: {items.Count}");
        foreach (var item in items)
            Output.WriteLine($"  {item.Label} (Area={item.Area}, Order={item.Order})");

        items.Select(i => i.Label).Should().Contain(MarkdownExportMenuProvider.PdfLabel,
            "MarkdownExportMenuProvider should contribute 'Export to PDF' for nodes with NodeType=Markdown");
        items.Select(i => i.Label).Should().Contain(MarkdownExportMenuProvider.DocxLabel,
            "MarkdownExportMenuProvider should contribute 'Export to DOCX' for nodes with NodeType=Markdown");

        var pdfItem = items.First(i => i.Label == MarkdownExportMenuProvider.PdfLabel);
        pdfItem.Area.Should().Be(ExportDocumentLayoutArea.PdfArea,
            "PDF item must navigate to the PDF export layout area");

        var docxItem = items.First(i => i.Label == MarkdownExportMenuProvider.DocxLabel);
        docxItem.Area.Should().Be(ExportDocumentLayoutArea.DocxArea,
            "DOCX item must navigate to the DOCX export layout area");
    }
}
