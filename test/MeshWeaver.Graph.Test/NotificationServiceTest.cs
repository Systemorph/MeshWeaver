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
/// Tests for <see cref="NotificationService.CreateNotification"/>: verifies the ADDRESSED shape
/// (path, MainNode, content) it constructs. The PG-table-routing concern is covered separately by
/// SatelliteNodeTests; this is the "did NotificationService construct the right thing" check. The
/// addressing rule itself, and the access boundary it draws, are pinned by
/// <see cref="NotificationAddressingTest"/>.
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
        string? targetNodePath = null, string? createdBy = null, string? icon = null,
        string? recipient = null)
    {
        using (Access.ImpersonateAsSystem())
            return await NotificationService.CreateNotification(
                    MeshService, mainNodePath, title, message, type, targetNodePath, createdBy, icon,
                    recipient)
                .Should().Emit();
    }

    /// <summary>
    /// 🚨 The node is a satellite of its ADDRESSEE, not of the entity it is about
    /// (Systemorph/MeshWeaver#3156): <c>{addressee}/_Notification/{id}</c> with
    /// <c>MainNode = {addressee}</c>. The thread the notification is about is 'chat-abc' in a space,
    /// and the notification is still delivered to the person — which is the entire point, because a
    /// path whose first segment is the READER is what lets the bell name one partition.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CreateNotification_BuildsAddressedShape_WithMainNodeAndPath()
    {
        const string main = $"{TestPartition}/_Thread/chat-abc";
        const string addressee = "chat-abc-owner";

        var node = await CreateNotification(
            mainNodePath: main,
            title: "Chat ready",
            message: "Your conversation is complete.",
            type: NotificationType.General,
            targetNodePath: main,
            createdBy: "agent",
            icon: "/static/NodeTypeIcons/chat.svg",
            recipient: addressee);

        node.MainNode.Should().Be(addressee);
        node.Namespace.Should().Be($"{addressee}/{NotificationService.SatelliteSegment}");
        node.Path.Should().StartWith($"{addressee}/{NotificationService.SatelliteSegment}/");
        node.NodeType.Should().Be(NotificationNodeType.NodeType);
        node.State.Should().Be(MeshNodeState.Active);
        node.Name.Should().Be("Chat ready");
        ((Notification)node.Content!).Recipient.Should().Be(addressee);
        ((Notification)node.Content!).TargetNodePath.Should().Be(main,
            "the entity survives as a reference, so grouping and the click-through are unchanged");
    }

    /// <summary>
    /// The compatibility path: a caller that names no addressee — the in-mesh callers that already
    /// pass the recipient AS the main node path — delivers to the main node's PARTITION. Kept
    /// because <c>CreateNotification</c> is public surface that in-mesh source compiles against at
    /// RUNTIME, so making the addressee mandatory would break code no compiler here can see.
    /// </summary>
    // 🚨 No literal timeout: the suite's xunit methodTimeout bounds it, and a hand-written 30 s is
    // wrong twice over (CI is slower, and 30 s IS the framework's own write bound — #2819).
    [Fact]
    public async Task CreateNotification_WithNoRecipient_AddressesTheMainNodesPartition()
    {
        var node = await CreateNotification(
            mainNodePath: $"{TestPartition}/Docs/spec",
            title: "Legacy caller",
            message: "",
            type: NotificationType.General);

        node.MainNode.Should().Be(TestPartition);
        node.Namespace.Should().Be($"{TestPartition}/{NotificationService.SatelliteSegment}");
        ((Notification)node.Content!).Recipient.Should().Be(TestPartition);
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
