using System;
using System.Reactive.Linq;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using NSubstitute;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for <see cref="NotificationService.CreateNotification"/>. Use
/// NSubstitute to capture the MeshNode handed to <c>IMeshService.CreateNode</c>
/// so we verify the satellite shape (path, MainNode, content) without
/// spinning up Postgres or Orleans. The PG-table-routing concern is covered
/// separately by SatelliteNodeTests; this is the "did NotificationService
/// construct the right thing" check.
/// </summary>
public class NotificationServiceTest
{
    private static (IMeshService Mock, Func<MeshNode?> Captured) MakeCapturingMesh()
    {
        var mesh = Substitute.For<IMeshService>();
        MeshNode? captured = null;
        mesh.CreateNode(Arg.Do<MeshNode>(n => captured = n))
            .Returns(call => Observable.Return((MeshNode)call[0]));
        return (mesh, () => captured);
    }

    [Fact]
    public void CreateNotification_BuildsSatelliteShape_WithMainNodeAndPath()
    {
        var (mesh, captured) = MakeCapturingMesh();
        const string main = "ACME/_Thread/chat-abc";

        NotificationService.CreateNotification(
            mesh,
            mainNodePath: main,
            title: "Chat ready",
            message: "Your conversation is complete.",
            type: NotificationType.General,
            targetNodePath: main,
            createdBy: "agent",
            icon: "/static/NodeTypeIcons/chat.svg")
            .Subscribe();

        var node = captured();
        node.Should().NotBeNull("CreateNode must be invoked once");

        // Satellite shape — Path is rooted at the main entity under _Notification.
        node!.MainNode.Should().Be(main);
        node.Namespace.Should().Be($"{main}/{NotificationService.SatelliteSegment}");
        node.Path.Should().StartWith($"{main}/{NotificationService.SatelliteSegment}/");
        node.NodeType.Should().Be(NotificationNodeType.NodeType);
        node.State.Should().Be(MeshNodeState.Active);
        node.Name.Should().Be("Chat ready");
    }

    [Fact]
    public void CreateNotification_PopulatesContent_WithUnreadDefaultAndProvidedFields()
    {
        var (mesh, captured) = MakeCapturingMesh();

        NotificationService.CreateNotification(
            mesh,
            mainNodePath: "ACME/Docs/spec",
            title: "Approval needed",
            message: "Carol asked for sign-off.",
            type: NotificationType.ApprovalRequired,
            targetNodePath: "ACME/Docs/spec/Approval/abc",
            createdBy: "carol",
            icon: "bell.svg")
            .Subscribe();

        var content = (Notification)captured()!.Content!;
        content.Title.Should().Be("Approval needed");
        content.Message.Should().Be("Carol asked for sign-off.");
        content.NotificationType.Should().Be(NotificationType.ApprovalRequired);
        content.TargetNodePath.Should().Be("ACME/Docs/spec/Approval/abc");
        content.CreatedBy.Should().Be("carol");
        content.Icon.Should().Be("bell.svg");
        content.IsRead.Should().BeFalse("new notifications start unread");
        content.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void CreateNotification_DefaultsTargetToMainNodePath_WhenOmitted()
    {
        var (mesh, captured) = MakeCapturingMesh();
        const string main = "ACME/_Thread/chat-xyz";

        NotificationService.CreateNotification(
            mesh,
            mainNodePath: main,
            title: "Ready",
            message: "",
            type: NotificationType.General)
            .Subscribe();

        ((Notification)captured()!.Content!).TargetNodePath.Should().Be(main,
            "the bell click should land on the main entity when no other target is set");
    }

    [Fact]
    public void CreateNotification_EachCallProducesUniqueId()
    {
        var (mesh, captured) = MakeCapturingMesh();
        const string main = "ACME";

        NotificationService.CreateNotification(mesh, main, "a", "", NotificationType.General).Subscribe();
        var first = captured()!.Id;

        NotificationService.CreateNotification(mesh, main, "b", "", NotificationType.General).Subscribe();
        var second = captured()!.Id;

        first.Should().NotBe(second);
    }
}
