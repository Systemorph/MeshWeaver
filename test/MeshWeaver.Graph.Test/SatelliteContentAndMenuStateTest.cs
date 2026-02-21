using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for ISatelliteContent, GetPrimaryPath, and NavigationContext.PrimaryPath/IsSatellite.
/// </summary>
public class SatelliteContentAndMenuStateTest
{
    #region ISatelliteContent Implementation Tests

    [Fact]
    public void Comment_Implements_ISatelliteContent()
    {
        var comment = new Comment
        {
            Text = "Test comment",
            Author = "user",
            PrimaryNodePath = "docs/readme"
        };

        comment.Should().BeAssignableTo<ISatelliteContent>();
        ((ISatelliteContent)comment).PrimaryNodePath.Should().Be("docs/readme");
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
    public void Thread_Implements_ISatelliteContent()
    {
        var thread = new MeshWeaver.AI.Thread { ParentPath = "org/project" };

        thread.Should().BeAssignableTo<ISatelliteContent>();
        ((ISatelliteContent)thread).PrimaryNodePath.Should().Be("org/project");
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
        var node = new MeshNode("readme", "docs") { Content = "plain text" };
        node.GetPrimaryPath().Should().Be("docs/readme");
    }

    [Fact]
    public void GetPrimaryPath_ForNullContent_ReturnsNodePath()
    {
        var node = new MeshNode("readme", "docs") { Content = null };
        node.GetPrimaryPath().Should().Be("docs/readme");
    }

    [Fact]
    public void GetPrimaryPath_ForCommentNode_ReturnsPrimaryNodePath()
    {
        var comment = new Comment { PrimaryNodePath = "org/project/doc" };
        var node = new MeshNode("comment1", "org/project/doc/comments") { Content = comment };

        node.GetPrimaryPath().Should().Be("org/project/doc");
    }

    [Fact]
    public void GetPrimaryPath_ForThreadNode_ReturnsParentPath()
    {
        var thread = new MeshWeaver.AI.Thread { ParentPath = "org/project" };
        var node = new MeshNode("thread1", "org/project/threads") { Content = thread };

        node.GetPrimaryPath().Should().Be("org/project");
    }

    [Fact]
    public void GetPrimaryPath_ForCommentWithEmptyPrimaryNodePath_ReturnsNodePath()
    {
        // When PrimaryNodePath is empty, PrimaryNodePath is empty,
        // so GetPrimaryPath falls back to node's own path
        var comment = new Comment { PrimaryNodePath = "" };
        var node = new MeshNode("comment1", "some/path") { Content = comment };

        node.GetPrimaryPath().Should().Be("some/path/comment1");
    }

    [Fact]
    public void GetPrimaryPath_ForThreadWithNullParentPath_ReturnsNodePath()
    {
        var thread = new MeshWeaver.AI.Thread { ParentPath = null };
        var node = new MeshNode("thread1", "some/path") { Content = thread };

        node.GetPrimaryPath().Should().Be("some/path/thread1");
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
        var node = new MeshNode("readme", "docs") { Content = "text" };
        var ctx = CreateContext(node, "docs/readme");

        ctx.PrimaryPath.Should().Be(ctx.Namespace);
        ctx.IsSatellite.Should().BeFalse();
    }

    [Fact]
    public void NavigationContext_PrimaryPath_ForCommentNode_ReturnsPrimaryNodePath()
    {
        var comment = new Comment { PrimaryNodePath = "org/project/doc" };
        var node = new MeshNode("c1", "org/project/doc/comments") { Content = comment };
        var ctx = CreateContext(node, "org/project/doc/comments/c1");

        ctx.PrimaryPath.Should().Be("org/project/doc");
        ctx.IsSatellite.Should().BeTrue();
    }

    [Fact]
    public void NavigationContext_PrimaryPath_ForThreadNode_ReturnsParentPath()
    {
        var thread = new MeshWeaver.AI.Thread { ParentPath = "org/project" };
        var node = new MeshNode("t1", "org/project/threads") { Content = thread };
        var ctx = CreateContext(node, "org/project/threads/t1");

        ctx.PrimaryPath.Should().Be("org/project");
        ctx.IsSatellite.Should().BeTrue();
    }

    [Fact]
    public void NavigationContext_PrimaryPath_WhenNodeIsNull_ReturnsNamespace()
    {
        var ctx = CreateContext(null, "some/path");

        ctx.PrimaryPath.Should().Be(ctx.Namespace);
        ctx.IsSatellite.Should().BeFalse();
    }

    [Fact]
    public void NavigationContext_IsSatellite_ForEmptyPrimaryNodePath_ReturnsFalse()
    {
        var comment = new Comment { PrimaryNodePath = "" };
        var node = new MeshNode("c1", "path") { Content = comment };
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
        var comment = new Comment { PrimaryNodePath = "org/doc" };
        var commentNode = new MeshNode("c1", "org/doc/comments") { Content = comment };

        // Permission check should go to "org/doc", not "org/doc/comments/c1"
        commentNode.GetPrimaryPath().Should().Be("org/doc");

        var thread = new MeshWeaver.AI.Thread { ParentPath = "org/project" };
        var threadNode = new MeshNode("t1", "org/project/threads") { Content = thread };

        // Permission check should go to "org/project", not "org/project/threads/t1"
        threadNode.GetPrimaryPath().Should().Be("org/project");
    }

    #endregion
}
