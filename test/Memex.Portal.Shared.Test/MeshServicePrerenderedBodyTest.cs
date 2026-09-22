using System.Text.Json;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The signed-in prerender read must honor current source just like the crawler-facing body.
/// Exercise the real query and service: a stale mirror must not win over edited or cleared text.
/// </summary>
public class MeshServicePrerenderedBodyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddMeshNodes(
            new MeshNode("Prerender") { NodeType = "Space" },
            Page("Markdown", new { content = "Current **markdown**", prerenderedHtml = "Stale content" }),
            Page("Cover", new { body = "Current **cover**", prerenderedHtml = "Stale content" }),
            Page("Cleared", new { content = "", prerenderedHtml = "Stale content" }));

    private static MeshNode Page(string id, object content) => new(id, "Prerender")
    {
        NodeType = id == "Cover" ? "Space" : "Markdown",
        Content = JsonSerializer.SerializeToElement(content),
        PreRenderedHtml = "Stale mirror",
    };

    [Theory]
    [InlineData("Markdown", "<p>Current <strong>markdown</strong></p>\n")]
    [InlineData("Cover", "<p>Current <strong>cover</strong></p>\n")]
    [InlineData("Cleared", "")]
    public async Task PrerenderReadsCurrentSource(string id, string expected)
    {
        var html = await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .GetPreRenderedHtml($"Prerender/{id}").Should().Emit();

        Assert.Equal(expected, html);
    }
}
