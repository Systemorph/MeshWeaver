using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for <see cref="GraphNavigatorBuilder"/> — the pure model behind
/// <c>MeshSearchRenderMode.GraphNavigator</c>: ancestors above (ordered shallow → deep), the current
/// node, and the next level below (ordered Order then Name), with the current node dropped from both.
/// </summary>
public class GraphNavigatorBuilderTest
{
    private static MeshNode Node(string path, string? name = null, int? order = null)
    {
        var lastSlash = path.LastIndexOf('/');
        var id = lastSlash < 0 ? path : path[(lastSlash + 1)..];
        var ns = lastSlash < 0 ? "" : path[..lastSlash];
        return new MeshNode(id, ns) { NodeType = "Markdown", Name = name ?? id, Order = order };
    }

    [Fact]
    public void Build_OrdersAncestorsShallowToDeep_AndDropsCurrent()
    {
        var current = Node("acme/team/project");
        var ancestorsAndSelf = new[]
        {
            Node("acme/team/project"), // self — must be dropped from the rail
            Node("acme/team"),
            Node("acme"),
        };

        var model = GraphNavigatorBuilder.Build(
            "acme/team/project", ancestorsAndSelf, System.Array.Empty<MeshNode>(), current);

        model.Ancestors.Select(a => a.Path).Should().ContainInOrder("acme", "acme/team");
        model.Ancestors.Should().NotContain(a => a.Path == "acme/team/project");
        model.Current.Should().BeSameAs(current);
    }

    [Fact]
    public void Build_OrdersBelowByOrderThenName_AndDropsRoot()
    {
        var below = new[]
        {
            Node("p/b", name: "Bravo", order: 2),
            Node("p/a", name: "Alpha", order: 1),
            Node("p", name: "Root"),       // the root itself — must be dropped
            Node("p/c", name: "Charlie", order: 1),
        };

        var model = GraphNavigatorBuilder.Build("p", System.Array.Empty<MeshNode>(), below);

        // order 1 (Alpha, Charlie by name) then order 2 (Bravo); root excluded.
        model.Below.Select(n => n.Path).Should().ContainInOrder("p/a", "p/c", "p/b");
        model.Below.Should().NotContain(n => n.Path == "p");
    }

    [Fact]
    public void Build_RootNamespace_EmptyAncestors()
    {
        var below = new[] { Node("acme"), Node("rbuergi") };

        var model = GraphNavigatorBuilder.Build("", System.Array.Empty<MeshNode>(), below);

        model.Ancestors.Should().BeEmpty();
        model.Below.Select(n => n.Path).OrderBy(p => p, System.StringComparer.Ordinal)
            .Should().Equal("acme", "rbuergi");
    }

    [Fact]
    public void Build_DedupesByPath_CaseInsensitive()
    {
        var below = new List<MeshNode> { Node("p/a"), Node("P/A") };

        var model = GraphNavigatorBuilder.Build("p", System.Array.Empty<MeshNode>(), below);

        model.Below.Should().HaveCount(1);
    }
}
