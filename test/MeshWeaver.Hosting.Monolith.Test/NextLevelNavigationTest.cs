using System;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// End-to-end coverage for the <c>scope:nextLevel</c> "next populated level" navigation through the
/// real <c>hub.GetQuery</c> path (provider fan-out + the in-memory frontier). Verifies that empty
/// intermediate namespace segments are skipped (a multi-hop <c>y/z/deep</c> surfaces directly), that
/// a nearer real node suppresses deeper ones, and that adding a nearer node collapses the frontier.
/// </summary>
public class NextLevelNavigationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static MeshNode Markdown(string id, string ns) => new(id, ns)
    {
        Name = id,
        NodeType = "Markdown",
        State = MeshNodeState.Active,
    };

    [Fact(Timeout = 60_000)]
    public async Task NextLevel_SkipsEmptyNamespaces_AndSuppressesDeeperNodes()
    {
        var root = $"{TestPartition}/Nav-{Guid.NewGuid():N}";

        // x is a direct child (real); x/child is hidden behind it; y and y/z are EMPTY namespace
        // segments, so y/z/deep must surface directly at root's next level.
        await NodeFactory.CreateNode(Markdown("x", root)).Should().Within(15.Seconds()).Emit();
        await NodeFactory.CreateNode(Markdown("child", $"{root}/x")).Should().Within(15.Seconds()).Emit();
        await NodeFactory.CreateNode(Markdown("deep", $"{root}/y/z")).Should().Within(15.Seconds()).Emit();

        var query = $"namespace:{root} scope:nextLevel nodeType:Markdown";
        var snapshot = await Mesh.GetQuery($"nav-init:{root}", query)
            .Should().Within(20.Seconds())
            .Match(s => s.Any(n => n.Path == $"{root}/x") && s.Any(n => n.Path == $"{root}/y/z/deep"));

        var paths = snapshot.Select(n => n.Path).ToHashSet();
        paths.Should().Contain($"{root}/x");
        paths.Should().Contain($"{root}/y/z/deep", "empty namespace hops are skipped");
        paths.Should().NotContain($"{root}/x/child", "a nearer real ancestor (x) suppresses its descendants");
    }

    [Fact(Timeout = 60_000)]
    public async Task NextLevel_AddingNearerNode_CollapsesFrontier()
    {
        var root = $"{TestPartition}/NavCollapse-{Guid.NewGuid():N}";
        var query = $"namespace:{root} scope:nextLevel nodeType:Markdown";

        await NodeFactory.CreateNode(Markdown("deep", $"{root}/y/z")).Should().Within(15.Seconds()).Emit();

        // Before: y/z/deep is the frontier (y, y/z are empty).
        await Mesh.GetQuery($"nav-before:{root}", query)
            .Should().Within(20.Seconds())
            .Match(s => s.Any(n => n.Path == $"{root}/y/z/deep"));

        // Add a real node at y — now NEARER than y/z/deep.
        await NodeFactory.CreateNode(Markdown("y", root)).Should().Within(15.Seconds()).Emit();

        // After: a fresh frontier collapses to y; y/z/deep is suppressed behind it.
        var after = await Mesh.GetQuery($"nav-after:{root}", query)
            .Should().Within(20.Seconds())
            .Match(s => s.Any(n => n.Path == $"{root}/y"));

        var paths = after.Select(n => n.Path).ToHashSet();
        paths.Should().Contain($"{root}/y");
        paths.Should().NotContain($"{root}/y/z/deep", "y is now the nearer real ancestor");
    }
}
