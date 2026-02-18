using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for ISatelliteContent, GetPrimaryPath, NavigationContext.PrimaryPath/IsSatellite,
/// and NodeMenuState permission defaults.
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
            DocumentPath = "docs/readme"
        };

        comment.Should().BeAssignableTo<ISatelliteContent>();
        ((ISatelliteContent)comment).PrimaryNodePath.Should().Be("docs/readme");
    }

    [Fact]
    public void Comment_PrimaryNodePath_ReturnsDocumentPath()
    {
        var comment = new Comment { DocumentPath = "org/project/doc" };
        comment.PrimaryNodePath.Should().Be("org/project/doc");
    }

    [Fact]
    public void Comment_PrimaryNodePath_WhenDocumentPathEmpty_ReturnsEmpty()
    {
        var comment = new Comment { DocumentPath = "" };
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
    public void GetPrimaryPath_ForCommentNode_ReturnsDocumentPath()
    {
        var comment = new Comment { DocumentPath = "org/project/doc" };
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
    public void GetPrimaryPath_ForCommentWithEmptyDocumentPath_ReturnsNodePath()
    {
        // When DocumentPath is empty, PrimaryNodePath is empty,
        // so GetPrimaryPath falls back to node's own path
        var comment = new Comment { DocumentPath = "" };
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
    public void NavigationContext_PrimaryPath_ForCommentNode_ReturnsDocumentPath()
    {
        var comment = new Comment { DocumentPath = "org/project/doc" };
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
        var comment = new Comment { DocumentPath = "" };
        var node = new MeshNode("c1", "path") { Content = comment };
        var ctx = CreateContext(node, "path/c1");

        ctx.IsSatellite.Should().BeFalse();
    }

    #endregion

    #region NodeMenuState Permission Default Tests

    [Fact]
    public void NodeMenuState_Empty_HasNoPermissions()
    {
        var state = NodeMenuState.Empty;

        state.Permissions.Should().Be(Permission.None);
        state.CanCreate.Should().BeFalse("Empty state must deny Create");
        state.CanDelete.Should().BeFalse("Empty state must deny Delete");
        state.CanEdit.Should().BeFalse("Empty state must deny Edit");
    }

    [Fact]
    public void NodeMenuState_DefaultConstructor_HasNoPermissions()
    {
        var state = new NodeMenuState();

        state.Permissions.Should().Be(Permission.None);
        state.CanCreate.Should().BeFalse();
        state.CanDelete.Should().BeFalse();
        state.CanEdit.Should().BeFalse();
    }

    [Fact]
    public void NodeMenuState_WithAllPermissions_AllowsEverything()
    {
        var state = new NodeMenuState { Permissions = Permission.All };

        state.CanCreate.Should().BeTrue();
        state.CanDelete.Should().BeTrue();
        state.CanEdit.Should().BeTrue();
    }

    [Fact]
    public void NodeMenuState_WithViewerPermissions_DeniesCreateDeleteEdit()
    {
        var state = new NodeMenuState { Permissions = Permission.Read };

        state.CanCreate.Should().BeFalse();
        state.CanDelete.Should().BeFalse();
        state.CanEdit.Should().BeFalse();
    }

    [Fact]
    public void NodeMenuState_WithEditorPermissions_AllowsCreateEdit_DeniesDelete()
    {
        var state = new NodeMenuState
        {
            Permissions = Permission.Read | Permission.Create | Permission.Update | Permission.Comment
        };

        state.CanCreate.Should().BeTrue();
        state.CanEdit.Should().BeTrue();
        state.CanDelete.Should().BeFalse("Editor cannot delete");
    }

    [Fact]
    public void NodeMenuState_WithCommenterPermissions_DeniesAll()
    {
        var state = new NodeMenuState
        {
            Permissions = Permission.Read | Permission.Comment
        };

        state.CanCreate.Should().BeFalse();
        state.CanEdit.Should().BeFalse();
        state.CanDelete.Should().BeFalse();
    }

    #endregion

    #region PermissionHelper.GetEffectivePermissionsForNodeAsync Tests

    [Fact]
    public void GetPrimaryPath_UsedByPermissionHelper_ResolvesCorrectly()
    {
        // Verifies that GetPrimaryPath correctly resolves satellite nodes
        // so PermissionHelper.GetEffectivePermissionsForNodeAsync checks the right path
        var comment = new Comment { DocumentPath = "org/doc" };
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
