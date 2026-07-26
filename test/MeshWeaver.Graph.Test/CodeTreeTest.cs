using System.Collections.Generic;
using System.Linq;
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

        tree.Folders.Keys.Should().BeEquivalentTo(new[] { "Models" }, System.Text.Json.JsonSerializerOptions.Default);
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
        tree.Folders.Keys.Should().Contain("Integration");
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

        tree.Folders.Keys.Should().BeEquivalentTo(new[] { "Models", "Services" }, System.Text.Json.JsonSerializerOptions.Default);
        tree.Folders["Models"].Folders.Keys.Should().BeEquivalentTo(new[] { "Nested" }, System.Text.Json.JsonSerializerOptions.Default);
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
            current.Folders.Keys.Should().Contain(seg, $"tree should descend through '{seg}'");
            current = current.Folders[seg];
        }
        current.Leaves.Should().ContainSingle(l => l.Name == "Leaf.cs");
    }

    // -----------------------------------------------------------------------
    // BuildCodeTreeForNavigation — the side-menu variant: takes a resolved list
    // (Sources or Tests query output); foreign files group per PACKAGE (their
    // partition root), marked with PackagePath so the renderer can link the
    // folder header to the package page.
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

        tree.Folders.Keys.Should().Contain("Source");
        tree.Folders["Source"].Leaves.Should().ContainSingle(l => l.Name == "Program.cs");
        tree.Folders["Source"].Folders["Models"].Leaves.Should().ContainSingle(l => l.Name == "Person.cs");
        tree.Folders["Source"].PackagePath.Should().BeNull("local folders are not packages");
    }

    [Fact]
    public void BuildCodeTreeForNavigation_ForeignFiles_GroupPerPackage()
    {
        // Shared code pulled in via "@Shared/Utils" or "namespace:Other/Lib…" must
        // still be visible, and its owning PACKAGE named directly — one folder per
        // partition root, files at their package-relative path, so the renderer
        // can put the package icon on the folder and link its header to /{package}.
        var nodes = new List<MeshNode>
        {
            Code($"{RootPath}/Source/Local.cs"),
            Code("Shared/Utils/Helper.cs"),
            Code("Other/Lib/CommonTypes.cs"),
        };

        var tree = NodeTypeLayoutAreas.BuildCodeTreeForNavigation(RootPath, nodes);

        tree.Folders.Keys.Should().Contain("Source", "local file is relativised");
        tree.Folders.Keys.Should().NotContain("(shared)", "the umbrella folder is replaced by per-package folders");

        tree.Folders.Keys.Should().Contain("Shared");
        tree.Folders["Shared"].PackagePath.Should().Be("Shared");
        tree.Folders["Shared"].Folders["Utils"].Leaves.Should().ContainSingle(l => l.Name == "Helper.cs");

        tree.Folders.Keys.Should().Contain("Other");
        tree.Folders["Other"].PackagePath.Should().Be("Other");
        tree.Folders["Other"].Folders["Lib"].Leaves.Should().ContainSingle(l => l.Name == "CommonTypes.cs");
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

        tree.Folders.Keys.Should().BeEquivalentTo(new[] { "Shared" }, System.Text.Json.JsonSerializerOptions.Default);
        tree.Folders["Shared"].PackagePath.Should().Be("Shared");
        tree.Folders["Shared"].Folders["Utils"].Leaves.Should().HaveCount(2);
    }

    [Fact]
    public void BuildCodeTreeForNavigation_SameParentPackage_GroupsSharedPoolsTogether()
    {
        // The real-world shape that motivated per-package grouping: a NodeType
        // (Underwriting/Guideline) pulling the partition-shared pools
        // shared=@Underwriting/Source + shared=@Underwriting/SampleData/Source.
        // Both belong to the SAME package (the partition root "Underwriting"),
        // so they must land inside ONE package folder, package-relative.
        var nodes = new List<MeshNode>
        {
            Code("Underwriting/Source/IScope.cs"),
            Code("Underwriting/Source/RulesEngine.cs"),
            Code("Underwriting/SampleData/Source/SampleData.cs"),
        };

        var tree = NodeTypeLayoutAreas.BuildCodeTreeForNavigation("Underwriting/Guideline", nodes);

        tree.Folders.Keys.Should().BeEquivalentTo(new[] { "Underwriting" }, System.Text.Json.JsonSerializerOptions.Default);
        var package = tree.Folders["Underwriting"];
        package.PackagePath.Should().Be("Underwriting");
        package.Folders["Source"].Leaves.Should().HaveCount(2);
        package.Folders["SampleData"].Folders["Source"].Leaves.Should().ContainSingle(l => l.Name == "SampleData.cs");
    }

    [Fact]
    public void BuildCodeTreeForNavigation_PackageCollidingWithLocalFolder_MergesWithoutLosingFiles()
    {
        // A package named like a local relative folder must MERGE into it —
        // the old AddFolder-style dictionary overwrite would silently drop the
        // local files. No file may ever fall out of the tree.
        var nodes = new List<MeshNode>
        {
            Code($"{RootPath}/Shared/Local.cs"),
            Code("Shared/Utils/Foreign.cs"),
        };

        var tree = NodeTypeLayoutAreas.BuildCodeTreeForNavigation(RootPath, nodes);

        var merged = tree.Folders["Shared"];
        merged.PackagePath.Should().Be("Shared");
        merged.Leaves.Should().ContainSingle(l => l.Name == "Local.cs");
        merged.Folders["Utils"].Leaves.Should().ContainSingle(l => l.Name == "Foreign.cs");
    }

    [Fact]
    public void BuildCodeTreeForNavigation_OrderedChildren_LocalFoldersBeforePackages()
    {
        // Packages sort AFTER local folders (and before leaves): the type's own
        // code stays up top, pulled-in packages read as an appendix.
        var nodes = new List<MeshNode>
        {
            Code($"{RootPath}/Source/Local.cs"),
            Code("Alpha/Source/A.cs"),
        };

        var tree = NodeTypeLayoutAreas.BuildCodeTreeForNavigation(RootPath, nodes);

        var ordered = tree.OrderedChildren().Select(n => n.Name).ToArray();
        ordered.Should().Equal("Source", "Alpha");
    }

    // -----------------------------------------------------------------------
    // CompressChain — the render-time path compression of pure pass-through
    // folder chains ("SampleData → Source" with no files at either level
    // renders as one "SampleData/Source" group).
    // -----------------------------------------------------------------------

    [Fact]
    public void CompressChain_PassThroughChain_MergesLabels()
    {
        var nodes = new List<MeshNode>
        {
            Code("Underwriting/SampleData/Source/SampleData.cs"),
        };
        var tree = NodeTypeLayoutAreas.BuildCodeTreeForNavigation("Underwriting/Guideline", nodes);
        var package = tree.Folders["Underwriting"];

        var (label, effective) = NodeTypeLayoutAreas.CompressChain(package.Folders["SampleData"]);

        label.Should().Be("SampleData/Source");
        effective.Leaves.Should().ContainSingle(l => l.Name == "SampleData.cs");
    }

    [Fact]
    public void CompressChain_StopsAtFilesAndBranches()
    {
        // A folder with its own files, or more than one sub-folder, is a real
        // level — no compression.
        var nodes = new List<MeshNode>
        {
            Code($"{RootPath}/Source/A.cs"),
            Code($"{RootPath}/Source/Models/B.cs"),
        };
        var tree = NodeTypeLayoutAreas.BuildCodeTreeForNavigation(RootPath, nodes);

        var (label, effective) = NodeTypeLayoutAreas.CompressChain(tree.Folders["Source"]);

        label.Should().Be("Source");
        effective.Should().BeSameAs(tree.Folders["Source"]);
    }

    [Fact]
    public void CompressChain_PackageFolder_NeverCompresses()
    {
        // The package folder's header carries the package identity (icon + link
        // to the package page) — compressing "Underwriting" with its single
        // "Source" child into "Underwriting/Source" would destroy it.
        var nodes = new List<MeshNode>
        {
            Code("Underwriting/Source/IScope.cs"),
        };
        var tree = NodeTypeLayoutAreas.BuildCodeTreeForNavigation("Acme/Type", nodes);

        var (label, effective) = NodeTypeLayoutAreas.CompressChain(tree.Folders["Underwriting"]);

        label.Should().Be("Underwriting");
        effective.Should().BeSameAs(tree.Folders["Underwriting"]);
    }
}
