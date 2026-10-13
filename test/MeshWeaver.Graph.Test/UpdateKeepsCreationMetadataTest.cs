using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A FULL-ENTITY UPDATE THAT OMITS <c>createdDate</c> / <c>createdBy</c> KEEPS THEM.</b>
///
/// <para>The MCP <c>update</c> tool deserialises the node an agent sends and hands it to
/// <see cref="IMeshService.UpdateNode"/>. An agent that leaves out the creation metadata — it is
/// framework-owned, so it usually does — produced a node with <c>CreatedDate = default</c> and
/// <c>CreatedBy = null</c>, and the update pipeline wrote that wholesale: the node's creation
/// time and creator were reset. The pipeline now carries both from the live node when the caller
/// omits them, and keeps a value the caller names.</para>
/// </summary>
public class UpdateKeepsCreationMetadataTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static string NewId() => "ukc" + Guid.NewGuid().ToString("N")[..8];

    private static MeshNode Page(string id, string text) => new(id, TestPartition)
    {
        Name = id,
        NodeType = "Markdown",
        State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = text },
    };

    private Task<MeshNode> WaitFor(string path, Func<MeshNode, bool> predicate, string because)
        => Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null && predicate(n))
            .Should().Within(60.Seconds()).Emit(because, cancellationToken: TestContext.Current.CancellationToken);

    private static string? TextOf(MeshNode node, System.Text.Json.JsonSerializerOptions options)
        => node.ContentAs<MarkdownContent>(options)?.Content;

    private async Task<MeshNode> CreatePage(string id)
    {
        await MeshService.CreateNode(Page(id, "# original")).Take(1)
            .Should().Within(60.Seconds()).Emit("the page must exist first",
                cancellationToken: TestContext.Current.CancellationToken);
        return await WaitFor($"{TestPartition}/{id}", n => n.CreatedDate != default,
            "the created node carries its creation stamp");
    }

    [Fact(Timeout = 180000)]
    public async Task UpdateOmittingCreationMetadata_KeepsTheLiveValues()
    {
        var created = await CreatePage(NewId());
        created.CreatedDate.Should().NotBe(default(DateTimeOffset));

        // The shape the MCP tool hands the pipeline when the agent's JSON omits both keys.
        var update = Page(created.Id, "# edited") with { CreatedDate = default, CreatedBy = null };
        await MeshService.UpdateNode(update).Take(1)
            .Should().Within(60.Seconds()).Emit("the update lands",
                cancellationToken: TestContext.Current.CancellationToken);

        var after = await WaitFor(created.Path, n => TextOf(n, Mesh.JsonSerializerOptions) == "# edited",
            "the edited content is live");
        after.CreatedDate.Should().Be(created.CreatedDate, "an update that omits createdDate keeps it");
        after.CreatedBy.Should().Be(created.CreatedBy, "an update that omits createdBy keeps it");
    }

    /// <summary>
    /// Negative control: the carry-over applies only to OMITTED values — a caller that names a
    /// creation timestamp (an importer preserving source metadata) still writes it.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task UpdateNamingCreationMetadata_WritesTheNamedValue()
    {
        var created = await CreatePage(NewId());
        var named = MeshNode.StorageStable(created.CreatedDate.AddDays(-30));

        await MeshService.UpdateNode(Page(created.Id, "# imported") with { CreatedDate = named }).Take(1)
            .Should().Within(60.Seconds()).Emit("the update lands",
                cancellationToken: TestContext.Current.CancellationToken);

        await WaitFor(created.Path, n => n.CreatedDate == named,
            "a creation timestamp the caller names is written as given");
    }
}
