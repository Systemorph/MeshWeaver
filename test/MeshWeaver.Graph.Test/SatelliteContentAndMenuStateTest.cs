using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for MeshNode.MainNode, MeshNode.IsSatellite, GetPrimaryPath, and NavigationContext.PrimaryPath/IsSatellite.
/// </summary>
public class SatelliteContentAndMenuStateTest
{
    #region MeshNode.MainNode / IsSatellite Tests

    [Fact]
    public void MeshNode_IsSatellite_WhenMainNodeDiffersFromPath_ReturnsTrue()
    {
        var node = new MeshNode("c1", "org/doc/comments") { MainNode = "org/doc" };
        (node.MainNode != node.Path).Should().BeTrue();
    }

    [Fact]
    public void MeshNode_IsSatellite_WhenMainNodeEqualsPath_ReturnsFalse()
    {
        var node = new MeshNode("doc", "org") { MainNode = "org/doc" };
        (node.MainNode != node.Path).Should().BeFalse();
    }

    [Fact]
    public void MeshNode_MainNodeDefaultsToPath()
    {
        var node = new MeshNode("doc", "org");
        node.MainNode.Should().Be(node.Path);
        (node.MainNode != node.Path).Should().BeFalse();
    }

    [Fact]
    public void Comment_PrimaryNodePath_ReturnsPrimaryNodePath()
    {
        var comment = new Comment { PrimaryNodePath = "org/project/doc" };
        comment.PrimaryNodePath.Should().Be("org/project/doc");
    }

    [Fact]
    public void Comment_PrimaryNodePath_WhenPrimaryNodePathEmpty_ReturnsEmpty()
    {
        var comment = new Comment { PrimaryNodePath = "" };
        comment.PrimaryNodePath.Should().Be("");
    }

    [Fact]
    public void Thread_PrimaryNodePath_ReturnsParentPath()
    {
        var thread = new MeshWeaver.AI.Thread { ParentPath = "org/project/doc" };
        thread.PrimaryNodePath.Should().Be("org/project/doc");
    }

    [Fact]
    public void Thread_PrimaryNodePath_WhenParentPathNull_ReturnsNull()
    {
        var thread = new MeshWeaver.AI.Thread { ParentPath = null };
        thread.PrimaryNodePath.Should().BeNull();
    }

    #endregion

    #region MeshNode.GetPrimaryPath Tests

    [Fact]
    public void GetPrimaryPath_ForRegularNode_ReturnsNodePath()
    {
        var node = new MeshNode("readme", "docs");
        node.GetPrimaryPath().Should().Be("docs/readme");
    }

    [Fact]
    public void GetPrimaryPath_ForMainNode_ReturnsNodePath()
    {
        var node = new MeshNode("readme", "docs") { MainNode = "docs/readme" };
        node.GetPrimaryPath().Should().Be("docs/readme");
    }

    [Fact]
    public void GetPrimaryPath_ForSatelliteNode_ReturnsMainNode()
    {
        var node = new MeshNode("comment1", "org/project/doc/comments") { MainNode = "org/project/doc" };
        node.GetPrimaryPath().Should().Be("org/project/doc");
    }

    [Fact]
    public void GetPrimaryPath_ForNullMainNode_ReturnsNodePath()
    {
        var node = new MeshNode("readme", "docs");
        node.GetPrimaryPath().Should().Be("docs/readme");
    }

    #endregion

    #region NavigationContext.PrimaryPath / IsSatellite Tests

    private static NavigationContext CreateContext(MeshNode? node, string prefix = "test")
    {
        return new NavigationContext
        {
            Path = prefix,
            Resolution = new AddressResolution(prefix, null),
            Node = node
        };
    }

    [Fact]
    public void NavigationContext_PrimaryPath_ForRegularNode_ReturnsNamespace()
    {
        var node = new MeshNode("readme", "docs");
        var ctx = CreateContext(node, "docs/readme");

        ctx.PrimaryPath.Should().Be(ctx.Namespace);
        ctx.IsSatellite.Should().BeFalse();
    }

    [Fact]
    public void NavigationContext_PrimaryPath_ForSatelliteNode_ReturnsMainNode()
    {
        var node = new MeshNode("c1", "org/project/doc/comments") { MainNode = "org/project/doc" };
        var ctx = CreateContext(node, "org/project/doc/comments/c1");

        ctx.PrimaryPath.Should().Be("org/project/doc");
        ctx.IsSatellite.Should().BeTrue();
    }

    [Fact]
    public void NavigationContext_PrimaryPath_ForMainNode_ReturnsMainNode()
    {
        var node = new MeshNode("doc", "org/project") { MainNode = "org/project/doc" };
        var ctx = CreateContext(node, "org/project/doc");

        ctx.PrimaryPath.Should().Be("org/project/doc");
        ctx.IsSatellite.Should().BeFalse();
    }

    [Fact]
    public void NavigationContext_PrimaryPath_WhenNodeIsNull_ReturnsNamespace()
    {
        var ctx = CreateContext(null, "some/path");

        ctx.PrimaryPath.Should().Be(ctx.Namespace);
        ctx.IsSatellite.Should().BeFalse();
    }

    [Fact]
    public void NavigationContext_IsSatellite_WhenMainNodeIsNull_ReturnsFalse()
    {
        var node = new MeshNode("c1", "path");
        var ctx = CreateContext(node, "path/c1");

        ctx.IsSatellite.Should().BeFalse();
    }

    #endregion

    #region PermissionHelper.GetEffectivePermissionsForNodeAsync Tests

    [Fact]
    public void GetPrimaryPath_UsedByPermissionHelper_ResolvesCorrectly()
    {
        // Verifies that GetPrimaryPath correctly resolves satellite nodes
        // so PermissionHelper.GetEffectivePermissionsForNodeAsync checks the right path
        var commentNode = new MeshNode("c1", "org/doc/comments") { MainNode = "org/doc" };

        // Permission check should go to "org/doc", not "org/doc/comments/c1"
        commentNode.GetPrimaryPath().Should().Be("org/doc");

        var threadNode = new MeshNode("t1", "org/project/threads") { MainNode = "org/project" };

        // Permission check should go to "org/project", not "org/project/threads/t1"
        threadNode.GetPrimaryPath().Should().Be("org/project");
    }

    #endregion
}
