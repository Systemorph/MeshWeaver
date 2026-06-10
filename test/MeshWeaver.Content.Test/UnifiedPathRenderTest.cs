using System;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Render tests for the Unified Path self-reference areas the docs use: <c>@@data/</c> must render
/// the current node's content as a JSON structure (the <c>$Data</c> area), and <c>@@schema/</c> must
/// render the node's MeshNode + content-type schema (the <c>$Schema</c> area). These exercise the
/// actual layout-area render (reactive — <see cref="MeshNodeLayoutAreas.Data"/>/<c>Schema</c> return
/// <c>IObservable&lt;UiControl&gt;</c>), not just the parser. Complements the parser tests in
/// <see cref="MarkdownNodeIntegrationTest"/>.
/// </summary>
public class UnifiedPathRenderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Layout-area streams require the layout client on the client hub.
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    // A node with distinctive content so the rendered JSON is unambiguous. Created under System
    // because a top-level partition root (User) requires it; rendered as the (admin) test user.
    private Address SeedNodeWithContent(string partition)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (access.ImpersonateAsSystem())
        {
            NodeFactory.CreateNode(
                new MeshNode(partition) { NodeType = "User", Name = partition }).Should().Emit();
            NodeFactory.CreateNode(
                new MeshNode("Page", partition)
                {
                    NodeType = "Markdown",
                    Name = "Render Page",
                    Content = new MarkdownContent { Content = "# Render\n\nUCR_RENDER_MARKER_42" }
                }).Should().Emit();
        }
        return new Address($"{partition}/Page");
    }

    [Fact]
    public void DataArea_RendersCurrentNodeContentAsJson()
    {
        var address = SeedNodeWithContent("UcrDataRenderUser");

        var workspace = GetClient().GetWorkspace();
        // The area emits a "Building layout..." placeholder first, then the rendered
        // content — Match() waits for the frame that actually contains the node's data.
        var value = workspace
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                address, new LayoutAreaReference(MeshNodeLayoutAreas.DataArea))
            .Should().Within(40.Seconds())
            .Match(v => v.Value.GetRawText().Contains("UCR_RENDER_MARKER_42"),
                "@@data/ renders the current node's content as a JSON structure");

        var rendered = value.Value.GetRawText();
        Output.WriteLine(rendered.Length > 800 ? rendered[..800] : rendered);
        rendered.Should().Contain("json", "the content is rendered as a JSON code structure");
    }

    [Fact]
    public void SchemaArea_RendersCurrentNodeSchema()
    {
        var address = SeedNodeWithContent("UcrSchemaRenderUser");

        var workspace = GetClient().GetWorkspace();
        // Wait past the "Building layout..." placeholder for the rendered schema frame.
        var value = workspace
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                address, new LayoutAreaReference(MeshNodeLayoutAreas.SchemaArea))
            .Should().Within(40.Seconds())
            .Match(v => v.Value.GetRawText().Contains("MeshNode") && v.Value.GetRawText().Contains("MarkdownContent"),
                "@@schema/ renders the node's MeshNode + content-type schema");

        var rendered = value.Value.GetRawText();
        Output.WriteLine(rendered.Length > 800 ? rendered[..800] : rendered);
        rendered.Should().Contain("Schema", "the schema area renders a schema");
    }
}
