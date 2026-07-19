#pragma warning disable CS1591

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the "@@ embeds render without comments" contract. An @@ embed reaches the Markdown
/// Overview area with the <c>?showHeader=false</c> reference parameter; the rendered
/// <see cref="CollaborativeMarkdownControl"/> must then carry <c>HideAnnotations=true</c>
/// (no comment highlights, sidebar, selection-comment button, or page-comment footer), while
/// the node's own full page keeps the collaboration surface. Regression for the
/// "comments everywhere" report on pages composed of several @@ embeds.
/// </summary>
public class MarkdownEmbedAnnotationsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 60000)]
    public async Task EmbedOverview_HidesAnnotations_FullPageKeepsThem()
    {
        var nodeId = $"EmbedMd{Guid.NewGuid():N}"[..16];
        var path = $"{TestPartition}/{nodeId}";
        var node = new MeshNode(nodeId, TestPartition)
        {
            Name = "Embed Markdown",
            NodeType = MarkdownNodeType.NodeType,
            Content = new MarkdownContent { Content = "# Title\n\nBody text." }
        };
        await NodeFactory.CreateNode(node).Should().Emit();

        var workspace = GetClient(c => c.AddData()).GetWorkspace();

        var embedded = await RenderMarkdownBody(workspace, path, "?showHeader=false");
        Assert.True(embedded.HideAnnotations,
            "an @@ embed (showHeader=false) must render its markdown body without any collaboration UI");

        var fullPage = await RenderMarkdownBody(workspace, path, null);
        Assert.False(fullPage.HideAnnotations,
            "the node's own page must keep the collaboration surface");

        await NodeFactory.DeleteNode(path).Should().Emit();
    }

    /// <summary>
    /// Renders the node's Overview area with the given area id (query parameters ride on the id)
    /// and returns the CollaborativeMarkdownControl composing the markdown body — a direct child
    /// of the Overview container stack (see MarkdownOverviewLayoutArea.BuildOverview).
    /// </summary>
    private static async Task<CollaborativeMarkdownControl> RenderMarkdownBody(
        IWorkspace workspace, string path, string? id)
    {
        var reference = new LayoutAreaReference("Overview") { Id = id };
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(path), reference);

        var root = await stream.GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(c => c is StackControl);
        var stack = (StackControl)root!;

        foreach (var area in stack.Areas)
        {
            var name = area.Area?.ToString();
            if (string.IsNullOrEmpty(name))
                continue;
            var child = await stream.GetControlStream(name).Should().Within(10.Seconds()).Match(c => c != null);
            if (child is CollaborativeMarkdownControl markdown)
                return markdown;
        }

        throw new InvalidOperationException(
            $"No CollaborativeMarkdownControl found among the Overview children: {string.Join(", ", stack.Areas.Select(a => a.Area))}");
    }
}
