using System.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #6036 — the framework's markdown page columns ask for <see cref="HorizontalAlignment.Stretch"/>
/// EXPLICITLY. A markdown body has no width of its own; under a vertical stack's default start
/// alignment a table wider than the column made the body the table's max-content width (3,211 px
/// inside a 980 px column on a CRM offer page) and the pane clipped it with no scrollbar. Stretch is
/// an opt-in (an unset stack stays start — <c>StackCrossAxisAlignmentTest</c> in Layout.Test), so
/// these two columns are the sites that must carry it, and each is pinned here as rendered: through
/// the layout stream, the way the client receives it.
/// </summary>
public class MarkdownColumnStretchTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string MarkdownOverview = nameof(MarkdownOverview);
    private const string NodeDetails = nameof(NodeDetails);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(MarkdownOverview, MarkdownOverviewView)
                .WithView(NodeDetails, (host, _) => host.BuildDetailsTemplate(canEdit: false)));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private static UiControl MarkdownOverviewView(LayoutAreaHost host, RenderingContext ctx) =>
        MarkdownOverviewLayoutArea.BuildOverview(
            host,
            new MeshNode("Offer", "test/wide")
            {
                Name = "Offer",
                NodeType = MeshWeaver.Graph.Configuration.MarkdownNodeType.NodeType,
                Content = new MarkdownContent { Content = "| deliverable | description |\n|---|---|\n| a | b |" },
            },
            canComment: false, canEdit: false, hideHeader: true);

    private async Task<UiControl> RenderAsync(string area)
    {
        var reference = new LayoutAreaReference(area);
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);
        return (await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x != null,
                cancellationToken: TestContext.Current.CancellationToken))!;
    }

    private void ShouldStretch(UiControl control, string because)
    {
        var stack = control.Should().BeOfType<StackControl>().Subject;
        var skin = stack.Skins!.OfType<LayoutStackSkin>().Single();
        Output.WriteLine($"horizontalAlignment on the wire: {skin.HorizontalAlignment}");
        GetClient().ConvertSingle<HorizontalAlignment>(skin.HorizontalAlignment, null, HorizontalAlignment.Left)
            .Should().Be(HorizontalAlignment.Stretch, because);
    }

    [HubFact]
    public async Task MarkdownOverviewContainer_StretchesItsChildren()
    {
        ShouldStretch(await RenderAsync(MarkdownOverview),
            "the Markdown node's overview hosts the body directly; start alignment let a wide table clip it (#6036)");
    }

    [HubFact]
    public async Task NodePageOuterColumn_StretchesItsChildren()
    {
        ShouldStretch(await RenderAsync(NodeDetails),
            "the node page's markdown body is a direct child of this column; start alignment let a wide table clip it (#6036)");
    }
}
