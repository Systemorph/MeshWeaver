using System;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for <see cref="NotificationService.CreateNotification"/>: verifies the satellite shape
/// (path, MainNode, content) it constructs. The PG-table-routing concern is covered separately by
/// SatelliteNodeTests; this is the "did NotificationService construct the right thing" check.
///
/// <para>🚨 Drives <see cref="NotificationService.CreateNotification"/> against a REAL
/// <see cref="IMeshService"/> (<see cref="MonolithMeshTestBase"/>) — never a mocked one
/// (Systemorph/MeshWeaver#1810: AGENTS.md forbids mocking <c>IMeshService</c>). Reads back the
/// node <c>CreateNode</c> itself returns (the materialized, round-tripped shape) rather than
/// capturing what was merely passed in — a stronger check than the substitute-based version could
/// make, since it also proves the write actually lands. <c>CreateNotification</c> writes to an
/// arbitrary <c>mainNodePath</c> it does not own, so the calls run under System — the same
/// impersonation its real caller (<see cref="NotificationService.Dispatch"/>) already applies.</para>
/// </summary>
public class NotificationServiceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private async Task<MeshNode> CreateNotification(
        string mainNodePath, string title, string message, NotificationType type,
        string? targetNodePath = null, string? createdBy = null, string? icon = null)
    {
        using (Access.ImpersonateAsSystem())
            return await NotificationService.CreateNotification(
                    MeshService, mainNodePath, title, message, type, targetNodePath, createdBy, icon)
                .Should().Emit();
    }

    [Fact(Timeout = 30000)]
    public async Task CreateNotification_BuildsSatelliteShape_WithMainNodeAndPath()
    {
        const string main = $"{TestPartition}/_Thread/chat-abc";

        var node = await CreateNotification(
            mainNodePath: main,
            title: "Chat ready",
            message: "Your conversation is complete.",
            type: NotificationType.General,
            targetNodePath: main,
            createdBy: "agent",
            icon: "/static/NodeTypeIcons/chat.svg");

        // Satellite shape — Path is rooted at the main entity under _Notification.
        node.MainNode.Should().Be(main);
        node.Namespace.Should().Be($"{main}/{NotificationService.SatelliteSegment}");
        node.Path.Should().StartWith($"{main}/{NotificationService.SatelliteSegment}/");
        node.NodeType.Should().Be(NotificationNodeType.NodeType);
        node.State.Should().Be(MeshNodeState.Active);
        node.Name.Should().Be("Chat ready");
    }

    [Fact(Timeout = 30000)]
    public async Task CreateNotification_PopulatesContent_WithUnreadDefaultAndProvidedFields()
    {
        // CreatedAt is stamped (DateTimeOffset.UtcNow) inside NotificationService.CreateNotification
        // itself, before the write round-trips through the real hub — bracket the actual call
        // window instead of a large fixed tolerance, which would hide a regression that stamped
        // the wrong instant (e.g. write-completion time instead of call time).
        var before = DateTimeOffset.UtcNow;
        var node = await CreateNotification(
            mainNodePath: $"{TestPartition}/Docs/spec",
            title: "Approval needed",
            message: "Carol asked for sign-off.",
            type: NotificationType.ApprovalRequired,
            targetNodePath: $"{TestPartition}/Docs/spec/Approval/abc",
            createdBy: "carol",
            icon: "bell.svg");
        var after = DateTimeOffset.UtcNow;

        var content = (Notification)node.Content!;
        content.Title.Should().Be("Approval needed");
        content.Message.Should().Be("Carol asked for sign-off.");
        content.NotificationType.Should().Be(NotificationType.ApprovalRequired);
        content.TargetNodePath.Should().Be($"{TestPartition}/Docs/spec/Approval/abc");
        content.CreatedBy.Should().Be("carol");
        content.Icon.Should().Be("bell.svg");
        content.IsRead.Should().BeFalse("new notifications start unread");
        content.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after,
            "CreatedAt is stamped at call time, before the write round-trips");
    }

    [Fact(Timeout = 30000)]
    public async Task CreateNotification_DefaultsTargetToMainNodePath_WhenOmitted()
    {
        const string main = $"{TestPartition}/_Thread/chat-xyz";

        var node = await CreateNotification(
            mainNodePath: main,
            title: "Ready",
            message: "",
            type: NotificationType.General);

        ((Notification)node.Content!).TargetNodePath.Should().Be(main,
            "the bell click should land on the main entity when no other target is set");
    }

    [Fact(Timeout = 30000)]
    public async Task CreateNotification_EachCallProducesUniqueId()
    {
        var first = await CreateNotification(TestPartition, "a", "", NotificationType.General);
        var second = await CreateNotification(TestPartition, "b", "", NotificationType.General);

        first.Id.Should().NotBe(second.Id);
    }
}
