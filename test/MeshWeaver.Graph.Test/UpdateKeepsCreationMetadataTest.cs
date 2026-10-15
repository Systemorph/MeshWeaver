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
/// 🚨 <b>A FULL-ENTITY UPDATE NEVER CHANGES <c>createdDate</c> / <c>createdBy</c>.</b>
///
/// <para>The MCP <c>update</c> tool deserialises the node an agent sends and hands it to
/// <see cref="IMeshService.UpdateNode"/>. An agent that leaves out the creation metadata — it is
/// framework-owned, so it usually does — produced a node with <c>CreatedDate = default</c> and
/// <c>CreatedBy = null</c>, and the update pipeline wrote that wholesale: the node's creation
/// time and creator were reset. The pipeline now carries both from the live node whatever the
/// caller sends — omitted or named — because Postgres holds both columns immutable.</para>
///
/// <para>Unconditionally (#6745): a live node WITHOUT a stamp — imported without one — keeps
/// having none. Its row already exists, so a stamp an update supplied would never be stored.</para>
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
    /// A caller that NAMES different creation metadata does not change it either: Postgres holds
    /// <c>created_date</c> / <c>created_by</c> immutable, so accepting the named value would make the
    /// live node advertise a stamp the durable row never stores — on every backend the update keeps
    /// the live values.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task UpdateNamingCreationMetadata_KeepsTheLiveValues()
    {
        var created = await CreatePage(NewId());
        var named = MeshNode.StorageStable(created.CreatedDate.AddDays(-30));

        await MeshService.UpdateNode(Page(created.Id, "# imported")
                with { CreatedDate = named, CreatedBy = "someone-else" }).Take(1)
            .Should().Within(60.Seconds()).Emit("the update lands",
                cancellationToken: TestContext.Current.CancellationToken);

        var after = await WaitFor(created.Path, n => TextOf(n, Mesh.JsonSerializerOptions) == "# imported",
            "the edited content is live");
        after.CreatedDate.Should().Be(created.CreatedDate, "an update never rewrites the creation date");
        after.CreatedBy.Should().Be(created.CreatedBy, "an update never rewrites the creator");
    }

    /// <summary>
    /// #6745 — a node that exists WITHOUT a creation stamp (written to the store without one, as an
    /// import of a file that carries none) stays without one when an update NAMES a stamp. An update
    /// never sets creation metadata; it always keeps the live node's values, as Postgres keeps the
    /// columns it wrote once on insert.
    ///
    /// <para>Negative control: the pipeline's former fallback (<c>live.CreatedDate != default ?
    /// live.CreatedDate : node.CreatedDate</c>, and the same for <c>CreatedBy</c>) — the update then
    /// stamps the node with the caller's values and both assertions below fail.</para>
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task UpdateNamingCreationMetadata_OnANodeWithoutAStamp_LeavesItWithout()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await storage.Write(Page(id, "# imported without a stamp") with { CreatedDate = default, CreatedBy = null },
                Mesh.JsonSerializerOptions)
            .Should().Within(60.Seconds()).Emit("the stamp-less node is stored",
                cancellationToken: TestContext.Current.CancellationToken);

        var live = await WaitFor(path, n => TextOf(n, Mesh.JsonSerializerOptions) == "# imported without a stamp",
            "the stamp-less node is live");
        live.CreatedDate.Should().Be(default(DateTimeOffset), "the premise: the live node carries no creation date");
        live.CreatedBy.Should().BeNull("the premise: the live node carries no creator");

        await MeshService.UpdateNode(Page(id, "# edited")
                with { CreatedDate = MeshNode.StorageStable(DateTimeOffset.UtcNow.AddDays(-30)), CreatedBy = "someone-else" })
            .Take(1)
            .Should().Within(60.Seconds()).Emit("the update lands",
                cancellationToken: TestContext.Current.CancellationToken);

        var after = await WaitFor(path, n => TextOf(n, Mesh.JsonSerializerOptions) == "# edited",
            "the edited content is live");
        after.CreatedDate.Should().Be(default(DateTimeOffset),
            "an update never sets the creation date, not even on a node that has none");
        after.CreatedBy.Should().BeNull("an update never sets the creator, not even on a node that has none");
    }
}
