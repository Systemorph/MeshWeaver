using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// #1029 repro scaffold: a read of a JUST-DELETED path must reach a VERDICT
/// (an authoritative NotFound error), never sit silent until the caller's budget expires.
/// </summary>
public class PostDeleteReadVerdictTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    [Fact(Timeout = 120_000)]
    public async Task ReadAfterDelete_ReachesVerdict()
    {
        var cache = Cache;
        var path = $"{TestPartition}/delete-verdict-{Guid.NewGuid():N}";
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "Original",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(60.Seconds()).Emit();

        // Warm the read path exactly like the CRUD workflow's Get does.
        var warm = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Where(n => n is not null)
            .Should().Within(30.Seconds()).Emit();
        warm!.Path.Should().Be(path);

        await NodeFactory.DeleteNode(path).Should().Within(60.Seconds()).Emit();

        var started = DateTimeOffset.UtcNow;
        var outcome = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Materialize()
            .Should().Within(30.Seconds()).Match(
                n => n.Kind != NotificationKind.OnNext || n.Value is null,
                "the read of a deleted path must reach a verdict, not sit silent");
        Output.WriteLine(
            $"[post-delete read] kind={outcome.Kind} after {(DateTimeOffset.UtcNow - started).TotalMilliseconds:F0}ms "
            + $"ex={outcome.Exception?.GetType().Name}: {outcome.Exception?.Message}");
        outcome.Kind.Should().Be(NotificationKind.OnError,
            "a deleted node must surface an authoritative NotFound to the reader");
    }
}
