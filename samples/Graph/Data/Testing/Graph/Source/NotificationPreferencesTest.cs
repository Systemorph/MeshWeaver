// <meshweaver>
// Id: Testing/Graph/NotificationPreferencesTest
// DisplayName: Testing/Graph/NotificationPreferencesTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

/// <summary>
/// Pure unit tests for the notification preference model + the access-grant decision. No mesh needed.
/// </summary>
public class NotificationPreferencesTest
{
    [MeshFact]
    public void Defaults_BellOnForAll_EmailOnForAccessAndApprovalsOnly()
    {
        var s = new NotificationSettings();

        // Bell (in-app) on for every category.
        Assert.True(s.InApp(NotificationCategory.Approvals));
        Assert.True(s.InApp(NotificationCategory.AccessGranted));
        Assert.True(s.InApp(NotificationCategory.ChatReady));
        Assert.True(s.InApp(NotificationCategory.System));

        // Email on by default only for access grants + approvals.
        Assert.True(s.Email(NotificationCategory.Approvals));
        Assert.True(s.Email(NotificationCategory.AccessGranted));
        Assert.False(s.Email(NotificationCategory.ChatReady));
        Assert.False(s.Email(NotificationCategory.System));
    }

    [MeshTheory]
    [MeshInlineData(NotificationType.ApprovalRequired, NotificationCategory.Approvals)]
    [MeshInlineData(NotificationType.ApprovalGiven, NotificationCategory.Approvals)]
    [MeshInlineData(NotificationType.ApprovalRejected, NotificationCategory.Approvals)]
    [MeshInlineData(NotificationType.AccessGranted, NotificationCategory.AccessGranted)]
    [MeshInlineData(NotificationType.ChatReady, NotificationCategory.ChatReady)]
    [MeshInlineData(NotificationType.System, NotificationCategory.System)]
    [MeshInlineData(NotificationType.General, NotificationCategory.System)]
    public void ToCategory_MapsEachType(NotificationType type, NotificationCategory expected)
        => Assert.Equal(expected, type.ToCategory());

    [MeshFact]
    public void TryResolveGrant_ValidGrant_ReturnsRecipientNodeAndRole()
    {
        var node = Assignment(subject: "bob", role: "Editor", denied: false, createdBy: "admin", mainNode: "TeamSpace");

        var ok = AccessGrantNotifier.TryResolveGrant(node, Options, out var recipient, out var granted, out var roleText);

        Assert.True(ok);
        Assert.Equal("bob", recipient);
        Assert.Equal("TeamSpace", granted);
        Assert.Equal("Editor", roleText);
    }

    [MeshFact]
    public void TryResolveGrant_Denial_IsIgnored()
    {
        var node = Assignment(subject: "bob", role: "Editor", denied: true, createdBy: "admin", mainNode: "TeamSpace");
        Assert.False(AccessGrantNotifier.TryResolveGrant(node, Options, out _, out _, out _));
    }

    [MeshFact]
    public void TryResolveGrant_SelfGrant_IsSuppressed()
    {
        // Creator == subject (e.g. the space creator's own Admin assignment) → no notification.
        var node = Assignment(subject: "bob", role: "Admin", denied: false, createdBy: "bob", mainNode: "TeamSpace");
        Assert.False(AccessGrantNotifier.TryResolveGrant(node, Options, out _, out _, out _));
    }

    [MeshFact]
    public void TryResolveGrant_NoTargetNode_IsIgnored()
    {
        var node = Assignment(subject: "bob", role: "Editor", denied: false, createdBy: "admin", mainNode: "");
        Assert.False(AccessGrantNotifier.TryResolveGrant(node, Options, out _, out _, out _));
    }

    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode Assignment(string subject, string role, bool denied, string createdBy, string mainNode) =>
        new($"{subject}_Access", $"{mainNode}/_Access")
        {
            NodeType = "AccessAssignment",
            MainNode = mainNode,
            CreatedBy = createdBy,
            Content = new AccessAssignment
            {
                AccessObject = subject,
                Roles = [new RoleAssignment { Role = role, Denied = denied }],
            },
        };
}
