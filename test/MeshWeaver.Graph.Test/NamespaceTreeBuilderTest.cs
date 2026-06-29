using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for <see cref="NamespaceTreeBuilder"/> — the pure helper behind
/// <c>MeshSearchRenderMode.NamespaceTree</c> (the mesh catalog on Space/node
/// Overviews). Covers relativisation to the root namespace, first-level
/// folder/leaf split, node-into-folder absorption, contained counts, nesting,
/// ordering, and the lazily-loaded level shape built from child-count probes.
/// </summary>
public class NamespaceTreeBuilderTest
{
    private const string Root = "acme";

    private static MeshNode Node(string path, string? name = null, int? order = null)
    {
        var lastSlash = path.LastIndexOf('/');
        var id = lastSlash < 0 ? path : path[(lastSlash + 1)..];
        var ns = lastSlash < 0 ? "" : path[..lastSlash];
        return new MeshNode(id, ns)
        {
            NodeType = "Markdown",
            Name = name ?? id,
            Order = order
        };
    }

    [Fact]
    public void Build_FirstLevel_SplitsFoldersAndLeaves()
    {
        var nodes = new List<MeshNode>
        {
            Node($"{Root}/readme"),
            Node($"{Root}/Projects", name: "Projects"),
            Node($"{Root}/Projects/p1"),
            Node($"{Root}/Projects/p2"),
            Node($"{Root}/HR/handbook"),
        };

        var items = NamespaceTreeBuilder.Build(Root, nodes);

        // Folders first (HR, Projects — alphabetical), then leaves (readme).
        items.Should().HaveCount(3);
        var folders = items.OfType<NamespaceTreeFolder>().ToList();
        folders.Select(f => f.Name).Should().ContainInOrder("HR", "Projects");
        items.IndexOf(folders[1]).Should().BeLessThan(
            items.IndexOf(items.OfType<NamespaceTreeLeaf>().Single()),
            "folders sort before leaves");

        var hr = folders.Single(f => f.Name == "HR");
        hr.Path.Should().Be($"{Root}/HR");
        hr.Node.Should().BeNull("no node exists at the HR folder path itself");
        hr.Count.Should().Be(1);
        hr.Children.Should().ContainSingle()
            .Which.Should().BeOfType<NamespaceTreeLeaf>()
            .Which.Node.Id.Should().Be("handbook");

        var projects = folders.Single(f => f.Name == "Projects");
        projects.Node.Should().NotBeNull("the Projects node is absorbed into its folder");
        projects.Node!.Id.Should().Be("Projects");
        projects.Count.Should().Be(2, "p1 and p2 are strictly inside; the folder's own node is the header");
        var projectIds = projects.Children.OfType<NamespaceTreeLeaf>().Select(l => l.Node.Id).ToList();
        projectIds.Should().HaveCount(2);
        projectIds.Should().Contain(new[] { "p1", "p2" });

        items.OfType<NamespaceTreeLeaf>().Single().Node.Id.Should().Be("readme");
    }

    [Fact]
    public void Build_RelativizesToRoot_IgnoringRootNodeAndOutsiders()
    {
        var nodes = new List<MeshNode>
        {
            Node(Root),                    // the catalog root itself — not part of its own catalog
            Node($"{Root}/page"),
            Node("other/stray"),           // outside the root namespace
            Node("acmeCorp/lookalike"),    // prefix lookalike, not a child of acme
        };

        var items = NamespaceTreeBuilder.Build(Root, nodes);

        items.Should().ContainSingle()
            .Which.Should().BeOfType<NamespaceTreeLeaf>()
            .Which.Node.Id.Should().Be("page");
    }

    [Fact]
    public void Build_NestsDeeperNamespaces_AndCountsContainedNodes()
    {
        var nodes = new List<MeshNode>
        {
            Node($"{Root}/P", name: "P"),
            Node($"{Root}/P/x"),
            Node($"{Root}/P/Q/y"),
        };

        var items = NamespaceTreeBuilder.Build(Root, nodes);

        var p = items.Should().ContainSingle()
            .Which.Should().BeOfType<NamespaceTreeFolder>().Subject;
        p.Node.Should().NotBeNull();
        p.Count.Should().Be(2, "x and y live strictly inside P; P's own node is the header, Q has no node");

        var q = p.Children.OfType<NamespaceTreeFolder>().Single();
        q.Name.Should().Be("Q");
        q.Path.Should().Be($"{Root}/P/Q");
        q.Node.Should().BeNull();
        q.Count.Should().Be(1);
        q.Children.Should().ContainSingle()
            .Which.Should().BeOfType<NamespaceTreeLeaf>()
            .Which.Node.Id.Should().Be("y");

        p.Children.OfType<NamespaceTreeLeaf>().Single().Node.Id.Should().Be("x");
    }

    [Fact]
    public void Build_FolderName_PrefersNodeName_FallsBackToSegment()
    {
        var nodes = new List<MeshNode>
        {
            Node($"{Root}/Projects", name: "Our Projects"),
            Node($"{Root}/Projects/p1"),
            Node($"{Root}/HR/handbook"),
        };

        var items = NamespaceTreeBuilder.Build(Root, nodes);
        var folders = items.OfType<NamespaceTreeFolder>().ToList();

        folders.Single(f => f.Path == $"{Root}/Projects").Name.Should().Be("Our Projects");
        folders.Single(f => f.Path == $"{Root}/HR").Name.Should().Be("HR");
    }

    [Fact]
    public void Build_Leaves_OrderByOrderThenName()
    {
        var nodes = new List<MeshNode>
        {
            Node($"{Root}/zeta", name: "Zeta", order: 1),
            Node($"{Root}/alpha", name: "Alpha", order: 2),
            Node($"{Root}/beta", name: "Beta", order: 1),
        };

        var items = NamespaceTreeBuilder.Build(Root, nodes);

        items.OfType<NamespaceTreeLeaf>().Select(l => l.Node.Name)
            .Should().ContainInOrder("Beta", "Zeta", "Alpha");
    }

    [Fact]
    public void BuildLevel_UsesProbeCounts_ToSplitFoldersAndLeaves()
    {
        var children = new List<MeshNode>
        {
            Node($"{Root}/readme"),
            Node($"{Root}/Projects", name: "Projects"),
            Node($"{Root}/Archive", name: "Archive"),
        };
        var counts = new Dictionary<string, int>
        {
            [$"{Root}/Projects"] = 7,
            [$"{Root}/Archive"] = 0,
            [$"{Root}/readme"] = 0,
        };

        var items = NamespaceTreeBuilder.BuildLevel(Root, children, counts);

        items.Should().HaveCount(3);
        var folder = items.OfType<NamespaceTreeFolder>().Should().ContainSingle().Subject;
        folder.Path.Should().Be($"{Root}/Projects");
        folder.Count.Should().Be(7);
        folder.Node.Should().NotBeNull();
        folder.Children.Should().BeEmpty("lazy levels load their content on expand");

        items[0].Should().Be(folder, "folders sort before leaves");
        var leafIds = items.OfType<NamespaceTreeLeaf>().Select(l => l.Node.Id).ToList();
        leafIds.Should().HaveCount(2);
        leafIds.Should().Contain(new[] { "readme", "Archive" });
    }

    [Fact]
    public void BuildLevel_MissingProbeEntry_DefaultsToLeaf()
    {
        var children = new List<MeshNode> { Node($"{Root}/page") };

        var items = NamespaceTreeBuilder.BuildLevel(
            Root, children, ImmutableDictionary<string, int>.Empty);

        items.Should().ContainSingle().Which.Should().BeOfType<NamespaceTreeLeaf>();
    }
}
