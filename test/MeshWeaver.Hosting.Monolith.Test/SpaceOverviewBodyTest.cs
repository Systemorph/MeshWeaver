#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Regression for the recurring "Space body / logo won't show" bug.
/// <para><see cref="SpaceLayoutAreas.Overview"/> used to read the Space via
/// <c>host.Workspace.GetStream&lt;Space&gt;()</c> — but <c>WithContentType&lt;Space&gt;</c>
/// registers <c>Space</c> only in the TypeRegistry, NOT as a workspace TypeSource, so
/// <c>Workspace.GetStream&lt;Space&gt;()</c> always returned null (it bails when
/// <c>DataContext.GetTypeSource(T)</c> is null). The space variable was therefore always
/// null → <c>space.Body</c> / <c>space.Logo</c> were never read → every Space rendered the
/// default welcome placeholder. The fix reads the Space off <c>MeshNode.Content</c>
/// (<c>node.ContentAs&lt;Space&gt;()</c>). This pins it: a Space whose <c>content.Body</c> is
/// set must render that body, not the welcome template.</para>
/// </summary>
public class SpaceOverviewBodyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string BodyMarker = "SPACE_BODY_MARKER_42";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddSpaceType();

    [Fact(Timeout = 60000)]
    public async Task SpaceOverview_RendersContentBody_NotWelcomeTemplate()
    {
        var spaceId = $"BodySpace{Guid.NewGuid():N}"[..18];
        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = "Body Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space { Body = $"# Overview\n\n{BodyMarker}" }
        };
        await NodeFactory.CreateNode(spaceNode).Should().Emit();

        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var reference = new LayoutAreaReference("Overview");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(spaceId), reference);

        // Overview composes a Stack: [header, body, …].
        var root = await stream.GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(c => c is StackControl);
        var stack = (StackControl)root!;
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(2, "Overview composes header + body");

        // Resolve every top-level child area and collect any markdown text. The body is a
        // MarkdownControl rendered from content.Body; the header resolves to a Stack (skipped).
        var markdownTexts = new List<string>();
        foreach (var area in stack.Areas)
        {
            var name = area.Area?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            var child = await stream.GetControlStream(name).Should().Within(10.Seconds()).Match(c => c != null);
            if (child is MarkdownControl md)
                markdownTexts.Add(md.Markdown?.ToString() ?? md.Html?.ToString() ?? "");
        }

        Assert.True(markdownTexts.Any(t => t.Contains(BodyMarker)),
            "the Space's content.Body must render — it lives on MeshNode.Content, not a Space stream");
        Assert.False(markdownTexts.Any(t => t.Contains("your space's home page")),
            "the welcome placeholder must NOT render when content.Body is set");

        await NodeFactory.DeleteNode(spaceId).Should().Emit();
    }
}
