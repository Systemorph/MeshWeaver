using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Render-level smoke tests for the layout areas touched by the Overview redesign,
/// the Delete progress UI, and the NodeType Configuration rework. These guard the
/// wiring so "the layout just renders blank" regressions are caught in CI instead
/// of through manual browser verification.
/// </summary>
public class OverviewHeaderRenderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Sanity check: the Overview area renders for a plain Markdown node. The refactored
    /// <see cref="MeshNodeLayoutAreas.BuildHeader"/> adds an action-button row and a meta
    /// row; if either throws, the remote stream never produces a value and this test
    /// times out at 20 s instead of hanging forever.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Overview_Renders_ForMarkdownNode()
    {
        var nodePath = $"{TestPartition}/overview-smoke";
        await NodeFactory.CreateNode(
            new MeshNode("overview-smoke", TestPartition) { Name = "Overview Smoke", NodeType = "Markdown" });

        var client = GetClient(c => c.AddData(data => data));
        var address = new Address(nodePath);
        await client.AwaitResponse(new PingRequest(), o => o.WithTarget(address));

        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea));

        var value = await stream.Timeout(20.Seconds()).FirstAsync();
        value.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "Overview must emit a rendered UI control, not time out");
    }

    /// <summary>
    /// The Delete area renders a confirmation page with the progress banner wired to a
    /// local data stream. Before this test existed, a missing <c>using</c> directive on
    /// <c>Address</c> / <c>IMessageDelivery</c> could silently break the page.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Delete_Renders_ForExistingNode()
    {
        var nodePath = $"{TestPartition}/delete-smoke";
        await NodeFactory.CreateNode(
            new MeshNode("delete-smoke", TestPartition) { Name = "Delete Smoke", NodeType = "Markdown" });

        var client = GetClient(c => c.AddData(data => data));
        var address = new Address(nodePath);
        await client.AwaitResponse(new PingRequest(), o => o.WithTarget(address));

        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, new LayoutAreaReference(MeshNodeLayoutAreas.DeleteArea));

        var value = await stream.Timeout(20.Seconds()).FirstAsync();
        value.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "Delete area must render the confirmation page");
    }

    /// <summary>
    /// Newly created nodes must have <see cref="MeshNode.CreatedDate"/> and
    /// <see cref="MeshNode.LastModified"/> stamped at creation time. The Overview meta
    /// row only shows them when they are non-default, and the user specifically asked
    /// for "the create process should hand out" a created timestamp.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CreateNode_StampsCreatedAndLastModified()
    {
        var nodePath = $"{TestPartition}/stamp-check";
        await NodeFactory.CreateNode(
            new MeshNode("stamp-check", TestPartition) { Name = "Stamp Check", NodeType = "Markdown" });

        var node = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath}")
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        node.Should().NotBeNull();
        node!.CreatedDate.Should().NotBe(default, "Created timestamp must be stamped at creation time");
        node.LastModified.Should().NotBe(default, "LastModified must be stamped at creation time");
    }
}
