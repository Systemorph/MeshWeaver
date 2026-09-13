// <meshweaver>
// Id: Testing/Graph/SatelliteContentAndMenuStateTest
// DisplayName: Testing/Graph/SatelliteContentAndMenuStateTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

/// <summary>
/// Tests for MeshNode.MainNode, MeshNode.IsSatellite, GetPrimaryPath, and NavigationContext.PrimaryPath/IsSatellite.
/// </summary>
public class SatelliteContentAndMenuStateTest
{
    #region MeshNode.MainNode / IsSatellite Tests

    [MeshFact]
    public void MeshNode_IsSatellite_WhenMainNodeDiffersFromPath_ReturnsTrue()
    {
        var node = new MeshNode("c1", "org/doc/comments") { MainNode = "org/doc" };
        (node.MainNode != node.Path).Should().BeTrue();
    }

    [MeshFact]
    public void MeshNode_IsSatellite_WhenMainNodeEqualsPath_ReturnsFalse()
    {
        var node = new MeshNode("doc", "org") { MainNode = "org/doc" };
        (node.MainNode != node.Path).Should().BeFalse();
    }

    [MeshFact]
    public void MeshNode_MainNodeDefaultsToPath()
    {
        var node = new MeshNode("doc", "org");
        node.MainNode.Should().Be(node.Path);
        (node.MainNode != node.Path).Should().BeFalse();
    }

    [MeshFact]
    public void Comment_PrimaryNodePath_ReturnsPrimaryNodePath()
    {
        var comment = new Comment { PrimaryNodePath = "org/project/doc" };
        comment.PrimaryNodePath.Should().Be("org/project/doc");
    }

    [MeshFact]
    public void Comment_PrimaryNodePath_WhenPrimaryNodePathEmpty_ReturnsEmpty()
    {
        var comment = new Comment { PrimaryNodePath = "" };
        comment.PrimaryNodePath.Should().Be("");
    }

    #endregion

    #region MeshNode.GetPrimaryPath Tests

    [MeshFact]
    public void GetPrimaryPath_ForRegularNode_ReturnsNodePath()
    {
        var node = new MeshNode("readme", "docs");
        node.GetPrimaryPath().Should().Be("docs/readme");
    }

    [MeshFact]
    public void GetPrimaryPath_ForMainNode_ReturnsNodePath()
    {
        var node = new MeshNode("readme", "docs") { MainNode = "docs/readme" };
        node.GetPrimaryPath().Should().Be("docs/readme");
    }

    [MeshFact]
    public void GetPrimaryPath_ForSatelliteNode_ReturnsMainNode()
    {
        var node = new MeshNode("comment1", "org/project/doc/comments") { MainNode = "org/project/doc" };
        node.GetPrimaryPath().Should().Be("org/project/doc");
    }

    [MeshFact]
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

    [MeshFact]
    public void NavigationContext_PrimaryPath_ForRegularNode_ReturnsNamespace()
    {
        var node = new MeshNode("readme", "docs");
        var ctx = CreateContext(node, "docs/readme");

        ctx.PrimaryPath.Should().Be(ctx.Namespace);
        ctx.IsSatellite.Should().BeFalse();
    }

    [MeshFact]
    public void NavigationContext_PrimaryPath_ForSatelliteNode_ReturnsMainNode()
    {
        var node = new MeshNode("c1", "org/project/doc/comments") { MainNode = "org/project/doc" };
        var ctx = CreateContext(node, "org/project/doc/comments/c1");

        ctx.PrimaryPath.Should().Be("org/project/doc");
        ctx.IsSatellite.Should().BeTrue();
    }

    [MeshFact]
    public void NavigationContext_PrimaryPath_ForMainNode_ReturnsMainNode()
    {
        var node = new MeshNode("doc", "org/project") { MainNode = "org/project/doc" };
        var ctx = CreateContext(node, "org/project/doc");

        ctx.PrimaryPath.Should().Be("org/project/doc");
        ctx.IsSatellite.Should().BeFalse();
    }

    [MeshFact]
    public void NavigationContext_PrimaryPath_WhenNodeIsNull_ReturnsNamespace()
    {
        var ctx = CreateContext(null, "some/path");

        ctx.PrimaryPath.Should().Be(ctx.Namespace);
        ctx.IsSatellite.Should().BeFalse();
    }

    [MeshFact]
    public void NavigationContext_IsSatellite_WhenMainNodeIsNull_ReturnsFalse()
    {
        var node = new MeshNode("c1", "path");
        var ctx = CreateContext(node, "path/c1");

        ctx.IsSatellite.Should().BeFalse();
    }

    #endregion

    #region GetPrimaryPath Tests

    [MeshFact]
    public void GetPrimaryPath_UsedByPermissionResolution_ResolvesCorrectly()
    {
        // Verifies that GetPrimaryPath correctly resolves satellite nodes
        // so hub.CheckPermission checks the right primary-node path
        var commentNode = new MeshNode("c1", "org/doc/comments") { MainNode = "org/doc" };

        // Permission check should go to "org/doc", not "org/doc/comments/c1"
        commentNode.GetPrimaryPath().Should().Be("org/doc");

        var threadNode = new MeshNode("t1", "org/project/threads") { MainNode = "org/project" };

        // Permission check should go to "org/project", not "org/project/threads/t1"
        threadNode.GetPrimaryPath().Should().Be("org/project");
    }

    #endregion
}
