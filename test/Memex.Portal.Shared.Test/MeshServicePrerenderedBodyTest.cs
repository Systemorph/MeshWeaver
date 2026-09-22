using System.Reactive.Linq;
using System.Text.Json;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The signed-in prerender read must honor current source just like the crawler-facing body.
/// Exercise the real owner and service: a stale mirror must not win over edited or cleared text.
/// </summary>
public class MeshServicePrerenderedBodyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddMeshNodes(
            new MeshNode("Prerender") { NodeType = "Space" },
            AssignmentNodeFactory.Policy("Prerender", new PartitionAccessPolicy { PublicRead = true }),
            Page("Markdown", new { content = "Current **markdown**", prerenderedHtml = "Stale content" }),
            Page("Cover", new { body = "Current **cover**", prerenderedHtml = "Stale content" }),
            Page("Cleared", new { content = "", prerenderedHtml = "Stale content" }),
            Page("Edited", new { content = "Initial page", prerenderedHtml = "Stale content" }));

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

    [Fact]
    public async Task AnEditAndClear_AreVisibleOnTheNextPrerenderRead()
    {
        const string path = "Prerender/Edited";
        var service = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        Assert.Contains("Initial page", await service.GetPreRenderedHtml(path).Should().Emit());

        await WriteAndObserve("Edited **page**", "Edited title");
        Assert.Equal("<p>Edited <strong>page</strong></p>\n",
            await service.GetPreRenderedHtml(path).Should().Emit());
        var edited = await SeoResolver.Resolve(Mesh, path).Should().Emit();
        Assert.NotNull(edited);
        Assert.Equal("Edited title", edited.Node.Name);
        Assert.Equal("<p>Edited <strong>page</strong></p>\n", edited.Body);

        await WriteAndObserve("", "Cleared title");
        Assert.Equal("", await service.GetPreRenderedHtml(path).Should().Emit());
        var cleared = await SeoResolver.Resolve(Mesh, path).Should().Emit();
        Assert.NotNull(cleared);
        Assert.Equal("Cleared title", cleared.Node.Name);
        Assert.Equal("", cleared.Body);

        async Task WriteAndObserve(string source, string title)
        {
            await Mesh.GetMeshNodeStream(path).Update(node => node with
            {
                Name = title,
                Content = new MarkdownContent { Content = source, PrerenderedHtml = "Stale content" },
                PreRenderedHtml = "Stale mirror",
            }).Should().Emit();
            await Mesh.GetMeshNodeStream(path)
                .Where(node => node.Name == title && MarkdownBody.Of(node, Mesh.JsonSerializerOptions) == source)
                .Should().Emit();
        }
    }
}
