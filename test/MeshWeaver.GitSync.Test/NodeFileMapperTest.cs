using MeshWeaver.GitSync;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Pure unit tests for the node ↔ file path mapping (no mesh). These pin the
/// export/import round-trip convention: root ↔ <c>index.*</c>, leaf ↔ <c>A.*</c>,
/// directory node ↔ <c>A/index.*</c>, nested ↔ <c>A/B.*</c>.
/// </summary>
public class NodeFileMapperTest
{
    [Theory]
    [InlineData("Acme", "Acme", "")]                 // the partition root
    [InlineData("Acme/Welcome", "Acme", "Welcome")]
    [InlineData("Acme/Docs/Intro", "Acme", "Docs/Intro")]
    public void RelativePath_StripsPartitionPrefix(string path, string partition, string expected)
        => Assert.Equal(expected, NodeFileMapper.RelativePath(path, partition));

    [Fact]
    public void ToRepoPath_Root_IsTopLevelIndex()
        => Assert.Equal("index.json", NodeFileMapper.ToRepoPath("Acme", "Acme", ".json", hasChildren: false));

    [Fact]
    public void ToRepoPath_Leaf_IsFile()
        => Assert.Equal("Welcome.md", NodeFileMapper.ToRepoPath("Acme/Welcome", "Acme", ".md", hasChildren: false));

    [Fact]
    public void ToRepoPath_DirectoryNode_IsIndexInFolder()
        => Assert.Equal("Docs/index.md", NodeFileMapper.ToRepoPath("Acme/Docs", "Acme", ".md", hasChildren: true));

    [Fact]
    public void ToRepoPath_Nested_IsNestedFile()
        => Assert.Equal("Docs/Intro.md", NodeFileMapper.ToRepoPath("Acme/Docs/Intro", "Acme", ".md", hasChildren: false));

    [Theory]
    [InlineData("Welcome.md", "Welcome", "")]
    [InlineData("Docs/Intro.md", "Intro", "Docs")]
    [InlineData("Docs/index.md", "Docs", "")]
    [InlineData("A/B/index.json", "B", "A")]
    public void FromRelativePath_IsInverseOfToRepoPath(string rel, string expectedId, string expectedNs)
    {
        var (id, ns) = NodeFileMapper.FromRelativePath(rel);
        Assert.Equal(expectedId, id);
        Assert.Equal(expectedNs, ns);
    }

    [Theory]
    [InlineData("index.md", true)]
    [InlineData("index.json", true)]
    [InlineData("Docs/index.md", false)]
    [InlineData("Welcome.md", false)]
    public void IsRootIndex_OnlyTopLevelIndex(string rel, bool expected)
        => Assert.Equal(expected, NodeFileMapper.IsRootIndex(rel));

    [Fact]
    public void HasChildren_DetectsDescendants()
    {
        var paths = new[] { "Acme/Docs", "Acme/Docs/Intro", "Acme/Welcome" };
        Assert.True(NodeFileMapper.HasChildren("Acme/Docs", paths));
        Assert.False(NodeFileMapper.HasChildren("Acme/Welcome", paths));
    }

    /// <summary>The export path → import (id, ns) round-trips for a directory node and its child.</summary>
    [Fact]
    public void RoundTrip_DirectoryAndChild()
    {
        // Export: Docs has a child → Docs/index.md ; Docs/Intro is a leaf → Docs/Intro.md
        var docsFile = NodeFileMapper.ToRepoPath("Acme/Docs", "Acme", ".md", hasChildren: true);
        var introFile = NodeFileMapper.ToRepoPath("Acme/Docs/Intro", "Acme", ".md", hasChildren: false);

        // Import: derive (id, ns) back, rebase under a new partition "Beta".
        var (docsId, docsNs) = NodeFileMapper.FromRelativePath(docsFile);
        var (introId, introNs) = NodeFileMapper.FromRelativePath(introFile);

        Assert.Equal(("Docs", ""), (docsId, docsNs));
        Assert.Equal(("Intro", "Docs"), (introId, introNs));
    }
}
