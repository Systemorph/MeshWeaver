using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for <see cref="NodeTypeLayoutAreas.BuildCodeTree"/> — the pure helper
/// the Configuration side menu uses to group code files into a hierarchical tree.
/// Covers sources-vs-tests split, namespace nesting, ordering, and the "files
/// outside the NodeType namespace" case that shouldn't leak into the tree.
/// </summary>
public class CodeTreeTest
{
    private const string RootPath = "Acme/Project";

    private static MeshNode Code(string path, string? name = null)
    {
        var lastSlash = path.LastIndexOf('/');
        var id = lastSlash < 0 ? path : path[(lastSlash + 1)..];
        var ns = lastSlash < 0 ? "" : path[..lastSlash];
        return new MeshNode(id, ns)
        {
            NodeType = CodeNodeType.NodeType,
            Name = name ?? id
        };
    }

    [Fact]
    public void BuildCodeTree_Sources_PicksOnlyFilesUnderSourceSubNamespace()
    {
        var nodes = new List<MeshNode>
        {
            Code($"{RootPath}/Source/Program.cs"),
            Code($"{RootPath}/Source/Models/Person.cs"),
            Code($"{RootPath}/Test/ProgramTest.cs"),
            Code("Other/SomewhereElse/Stray.cs"),
        };

        var tree = NodeTypeLayoutAreas.BuildCodeTree(RootPath, CodeNodeType.SourceSubNamespace, nodes);

        tree.Folders.Keys.Should().BeEquivalentTo(["Models"]);
        tree.Leaves.Should().ContainSingle(l => l.Name == "Program.cs");
    }

    [Fact]
    public void BuildCodeTree_Tests_PicksOnlyFilesUnderTestSubNamespace()
    {
        var nodes = new List<MeshNode>
        {
            Code($"{RootPath}/Source/Program.cs"),
            Code($"{RootPath}/Test/ProgramTest.cs"),
            Code($"{RootPath}/Test/Integration/EndToEndTest.cs"),
        };

        var tree = NodeTypeLayoutAreas.BuildCodeTree(RootPath, CodeNodeType.TestSubNamespace, nodes);

        tree.Leaves.Should().ContainSingle(l => l.Name == "ProgramTest.cs");
        tree.Folders.Should().ContainKey("Integration");
        tree.Folders["Integration"].Leaves.Should().ContainSingle(l => l.Name == "EndToEndTest.cs");
    }

    [Fact]
    public void BuildCodeTree_FilesOutsideNamespace_AreFilteredOut()
    {
        // User feedback: "add also test coverage for code files outside the namespace".
        // A NodeType can pull shared code via @path shorthand or foreign namespace:
        // queries; when those expand into paths that don't live under the NodeType's
        // own Source/ or Test/ folder they must be filtered out of the side menu's
        // Sources/Tests sections. They belong in a different NodeType's tree.
        var nodes = new List<MeshNode>
        {
            Code($"{RootPath}/Source/Local.cs"),
            Code("Shared/Source/Shared.cs"),
            Code("Other/NodeType/Source/Foreign.cs"),
            Code("DifferentRoot/Acme/Project/Source/LookalikePath.cs"),
        };

        var sources = NodeTypeLayoutAreas.BuildCodeTree(RootPath, CodeNodeType.SourceSubNamespace, nodes);

        sources.Leaves.Should().ContainSingle(l => l.Name == "Local.cs");
        sources.Folders.Should().BeEmpty();
        // Paths that merely end with "/Acme/Project/Source/…" must NOT be considered under
        // the root — the prefix test is anchored, not substring.
        sources.Leaves.Should().NotContain(l => l.Name == "LookalikePath.cs");
        sources.Leaves.Should().NotContain(l => l.Name == "Shared.cs");
        sources.Leaves.Should().NotContain(l => l.Name == "Foreign.cs");
    }

    [Fact]
    public void BuildCodeTree_NestedNamespaces_BuildsFolderHierarchy()
    {
        var nodes = new List<MeshNode>
        {
            Code($"{RootPath}/Source/A.cs"),
            Code($"{RootPath}/Source/Models/B.cs"),
            Code($"{RootPath}/Source/Models/Nested/C.cs"),
            Code($"{RootPath}/Source/Services/D.cs"),
        };

        var tree = NodeTypeLayoutAreas.BuildCodeTree(RootPath, CodeNodeType.SourceSubNamespace, nodes);

        tree.Folders.Keys.Should().BeEquivalentTo(["Models", "Services"]);
        tree.Folders["Models"].Folders.Keys.Should().BeEquivalentTo(["Nested"]);
        tree.Folders["Models"].Folders["Nested"].Leaves.Should().ContainSingle(l => l.Name == "C.cs");
        tree.Folders["Models"].Leaves.Should().ContainSingle(l => l.Name == "B.cs");
        tree.Folders["Services"].Leaves.Should().ContainSingle(l => l.Name == "D.cs");
        tree.Leaves.Should().ContainSingle(l => l.Name == "A.cs");
    }

    [Fact]
    public void BuildCodeTree_EmptyInput_ReturnsEmptyTree()
    {
        var tree = NodeTypeLayoutAreas.BuildCodeTree(RootPath, CodeNodeType.SourceSubNamespace, new List<MeshNode>());
        tree.Folders.Should().BeEmpty();
        tree.Leaves.Should().BeEmpty();
    }

    [Fact]
    public void BuildCodeTree_OrderedChildren_ReturnsFoldersBeforeLeaves()
    {
        // The helper sorts folders alphabetically and appends leaves afterwards.
        // Callers rely on this to render nested groups before flat links so the
        // visual order is "folders up top, files below".
        var nodes = new List<MeshNode>
        {
            Code($"{RootPath}/Source/Z.cs"),
            Code($"{RootPath}/Source/A.cs"),
            Code($"{RootPath}/Source/Models/X.cs"),
            Code($"{RootPath}/Source/Beta/Y.cs"),
        };

        var tree = NodeTypeLayoutAreas.BuildCodeTree(RootPath, CodeNodeType.SourceSubNamespace, nodes);

        var ordered = tree.OrderedChildren().Select(n => n.Name).ToArray();
        ordered.Should().Equal("Beta", "Models", "A.cs", "Z.cs");
    }

    [Fact]
    public void BuildCodeTree_DeepNesting_PreservesAllSegments()
    {
        var nodes = new List<MeshNode>
        {
            Code($"{RootPath}/Source/a/b/c/d/Leaf.cs"),
        };

        var tree = NodeTypeLayoutAreas.BuildCodeTree(RootPath, CodeNodeType.SourceSubNamespace, nodes);

        var current = tree;
        foreach (var seg in new[] { "a", "b", "c", "d" })
        {
            current.Folders.Should().ContainKey(seg, $"tree should descend through '{seg}'");
            current = current.Folders[seg];
        }
        current.Leaves.Should().ContainSingle(l => l.Name == "Leaf.cs");
    }

    // -----------------------------------------------------------------------
    // BuildCodeTreeForNavigation — the side-menu variant: takes a resolved list
    // (Sources or Tests query output) and renders foreign files under "(shared)".
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildCodeTreeForNavigation_LocalFiles_AreRelativised()
    {
        var nodes = new List<MeshNode>
        {
            Code($"{RootPath}/Source/Program.cs"),
            Code($"{RootPath}/Source/Models/Person.cs"),
        };

        var tree = NodeTypeLayoutAreas.BuildCodeTreeForNavigation(RootPath, nodes);

        tree.Folders.Should().ContainKey("Source");
        tree.Folders["Source"].Leaves.Should().ContainSingle(l => l.Name == "Program.cs");
        tree.Folders["Source"].Folders["Models"].Leaves.Should().ContainSingle(l => l.Name == "Person.cs");
        tree.Folders.Should().NotContainKey("(shared)");
    }

    [Fact]
    public void BuildCodeTreeForNavigation_ForeignFiles_GoUnderSharedFolder()
    {
        // Shared code pulled in via "@Shared/Utils" or "namespace:Other/Lib…" must
        // still be visible — the user asked for "show all files as queried by
        // Source resolution" — but labelled as shared so its origin is obvious.
        var nodes = new List<MeshNode>
        {
            Code($"{RootPath}/Source/Local.cs"),
            Code("Shared/Utils/Helper.cs"),
            Code("Other/Lib/CommonTypes.cs"),
        };

        var tree = NodeTypeLayoutAreas.BuildCodeTreeForNavigation(RootPath, nodes);

        tree.Folders.Should().ContainKey("Source", "local file is relativised");
        tree.Folders.Should().ContainKey("(shared)", "foreign files collect under a shared folder");

        var shared = tree.Folders["(shared)"];
        // Foreign files keep their full path so operators see where they came from.
        shared.Folders.Should().ContainKey("Shared");
        shared.Folders["Shared"].Folders["Utils"].Leaves.Should().ContainSingle(l => l.Name == "Helper.cs");
        shared.Folders.Should().ContainKey("Other");
        shared.Folders["Other"].Folders["Lib"].Leaves.Should().ContainSingle(l => l.Name == "CommonTypes.cs");
    }

    [Fact]
    public void BuildCodeTreeForNavigation_Empty_ReturnsEmptyTree()
    {
        var tree = NodeTypeLayoutAreas.BuildCodeTreeForNavigation(RootPath, new List<MeshNode>());
        tree.Folders.Should().BeEmpty();
        tree.Leaves.Should().BeEmpty();
    }

    [Fact]
    public void BuildCodeTreeForNavigation_OnlyForeignFiles_StillRenders()
    {
        var nodes = new List<MeshNode>
        {
            Code("Shared/Utils/A.cs"),
            Code("Shared/Utils/B.cs"),
        };

        var tree = NodeTypeLayoutAreas.BuildCodeTreeForNavigation(RootPath, nodes);

        tree.Folders.Should().ContainKey("(shared)");
        tree.Folders.Should().NotContainKey("Source", "no local files means no local root folder");
    }
}
