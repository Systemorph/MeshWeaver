using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for <see cref="GraphNavigatorBuilder"/> — the pure model behind
/// <c>MeshSearchRenderMode.GraphNavigator</c> (the Search layout area on a mesh node). It splits the
/// current level into NODES at this level (cards on top; a node that also has content gets a drill
/// flag) and pure sub-NAMESPACES (drill links at the bottom), plus the ancestor rail.
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
    public void Build_SplitsNodes_AndNamespaces()
    {
        var descendants = new[]
        {
            Node("acme/readme"),                 // node, no children → NODE (leaf)
            Node("acme/Projects", name: "Projects"),
            Node("acme/Projects/p1"),            // Projects is a node WITH a child → NODE + drill
            Node("acme/HR/handbook"),            // HR has no node of its own → NAMESPACE
        };

        var model = GraphNavigatorBuilder.Build("acme", Array.Empty<MeshNode>(), descendants);

        // Nodes: readme (leaf) + Projects (container), ordered by Name.
        model.Nodes.Select(n => n.Node.Path).Should().Equal("acme/Projects", "acme/readme");
        var projects = model.Nodes.Single(n => n.Node.Path == "acme/Projects");
        projects.HasChildren.Should().BeTrue();
        projects.ChildCount.Should().Be(1);
        model.Nodes.Single(n => n.Node.Path == "acme/readme").HasChildren.Should().BeFalse();

        // Namespaces: HR (pure grouping, no node).
        model.Namespaces.Should().ContainSingle();
        model.Namespaces[0].Name.Should().Be("HR");
        model.Namespaces[0].Path.Should().Be("acme/HR");
        model.Namespaces[0].Count.Should().Be(1);
    }

    [Fact]
    public void Build_EmptyNamespaceChain_SurfacesAsNamespaceLink()
    {
        // a and a/b are NOT nodes — at the root the immediate level is the namespace "a".
        var model = GraphNavigatorBuilder.Build("", Array.Empty<MeshNode>(), new[] { Node("a/b/node") });

        model.Nodes.Should().BeEmpty();
        model.Namespaces.Should().ContainSingle();
        model.Namespaces[0].Name.Should().Be("a");
        model.Namespaces[0].Path.Should().Be("a");
    }

    [Fact]
    public void Build_OrdersAncestorsShallowToDeep_AndDropsCurrent()
    {
        var current = Node("acme/team/project");
        var ancestorsAndSelf = new[]
        {
            Node("acme/team/project"), // self — dropped from the rail
            Node("acme/team"),
            Node("acme"),
        };

        var model = GraphNavigatorBuilder.Build(
            "acme/team/project", ancestorsAndSelf, Array.Empty<MeshNode>(), current);

        model.Ancestors.Select(a => a.Path).Should().Equal("acme", "acme/team");
        model.Ancestors.Should().NotContain(a => a.Path == "acme/team/project");
        model.Current.Should().BeSameAs(current);
    }

    [Fact]
    public void Build_OrdersNodesByOrderThenName()
    {
        var descendants = new[]
        {
            Node("p/b", name: "Bravo", order: 2),
            Node("p/a", name: "Alpha", order: 1),
            Node("p/c", name: "Charlie", order: 1),
        };

        var model = GraphNavigatorBuilder.Build("p", Array.Empty<MeshNode>(), descendants);

        model.Nodes.Select(n => n.Node.Path).Should().Equal("p/a", "p/c", "p/b");
    }
}
