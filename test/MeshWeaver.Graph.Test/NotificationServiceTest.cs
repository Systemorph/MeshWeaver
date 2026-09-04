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
        string? recipient = null,
        string? identity = null)
    {
        using (Access.ImpersonateAsSystem())
            return await NotificationService.CreateNotification(
                    MeshService, mainNodePath, title, message, type, targetNodePath, createdBy, icon,
                    recipient, identity)
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
        node.ContentAs<Notification>(Mesh.JsonSerializerOptions)!.Recipient.Should().Be(addressee);
        node.ContentAs<Notification>(Mesh.JsonSerializerOptions)!.TargetNodePath.Should().Be(main,
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
        node.ContentAs<Notification>(Mesh.JsonSerializerOptions)!.Recipient.Should().Be(TestPartition);
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

        var content = node.ContentAs<Notification>(Mesh.JsonSerializerOptions)!;
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

        node.ContentAs<Notification>(Mesh.JsonSerializerOptions)!.TargetNodePath.Should().Be(main,
            "the bell click should land on the main entity when no other target is set");
    }

    [Fact(Timeout = 30000)]
    public async Task CreateNotification_EachCallProducesUniqueId()
    {
        var first = await CreateNotification(TestPartition, "a", "", NotificationType.General);
        var second = await CreateNotification(TestPartition, "b", "", NotificationType.General);

        first.Id.Should().NotBe(second.Id);
    }

    /// <summary>
    /// 🚨 The INTERACTION neither half of the addressed-notification work covered on its own, and the
    /// one a careless rebase silently reverts: a notification that is BOTH addressed
    /// (Systemorph/MeshWeaver#3156/#3216 — delivered to the recipient, not the entity's partition)
    /// AND deterministic (Systemorph/MeshWeaver#3213 — a repeat for the same condition upserts the
    /// SAME node instead of minting a row).
    ///
    /// <para>The load-bearing detail is that the deterministic id is derived from the
    /// <b>addressee</b>, not from <c>mainNodePath</c>. The node lives at
    /// <c>{addressee}/_Notification/{id}</c>, so keying on the entity would let two different
    /// addressees told about the SAME entity+condition derive the SAME id in different partitions.
    /// This pins both halves at once: same addressee + same identity ⇒ one node; different addressee
    /// + same identity ⇒ two distinct nodes, each in its own bell.</para>
    /// </summary>
    [Fact]
    public async Task AnAddressedNotification_IsIdempotentPerAddressee_NotPerEntity()
    {
        const string entity = $"{TestPartition}/SharedThing";
        const string identity = "condition|v2";
        const string addresseeA = "alice";
        const string addresseeB = "bob";

        var first = await CreateNotification(
            mainNodePath: entity, title: "Update available", message: "v2 is out",
            type: NotificationType.System, targetNodePath: entity,
            recipient: addresseeA, identity: identity);

        // Same addressee, same condition, a second pass: the SAME node, refreshed — not a new row.
        var repeat = await CreateNotification(
            mainNodePath: entity, title: "Update available", message: "v2 is out (again)",
            type: NotificationType.System, targetNodePath: entity,
            recipient: addresseeA, identity: identity);

        repeat.Path.Should().Be(first.Path,
            "a repeat for the same (addressee, condition) must upsert the same node — the #3213 guard");
        ((Notification)repeat.Content!).Recipient.Should().Be(addresseeA,
            "the addressed delivery must survive the deterministic id — the #3156 half");
        repeat.Namespace.Should().Be($"{addresseeA}/{NotificationService.SatelliteSegment}");

        // Same entity, same condition, DIFFERENT addressee: a distinct node in the other bell.
        var other = await CreateNotification(
            mainNodePath: entity, title: "Update available", message: "v2 is out",
            type: NotificationType.System, targetNodePath: entity,
            recipient: addresseeB, identity: identity);

        // 🚨 Assert the ID, not the PATH. The path is `{addressee}/_Notification/{id}`, so two
        // addressees always differ in the path segment even when the derived id is identical —
        // a path assertion here passes under BOTH keyings and pins nothing. (Measured: mutating
        // the key back to mainNodePath left a path-based assertion green.) The id is the part
        // the keying actually decides.
        other.Id.Should().NotBe(first.Id,
            "the id is keyed on the ADDRESSEE, so telling two people about one entity must not "
            + "derive one shared id — that is a cross-partition collision waiting for the first "
            + "per-user reminder about a shared entity");
        other.Namespace.Should().Be($"{addresseeB}/{NotificationService.SatelliteSegment}");
        ((Notification)other.Content!).TargetNodePath.Should().Be(entity,
            "both bells still click through to the one entity");
    }

}
