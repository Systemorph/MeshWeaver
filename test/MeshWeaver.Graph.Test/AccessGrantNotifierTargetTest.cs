using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins <see cref="AccessGrantNotifier.ResolveGrantedNode"/> — the node an access-granted
/// notification names and links to. It must ALWAYS be the governed node, never the <c>_Access</c>
/// satellite container: the reported bug was a mail reading "You've been given access to
/// CollaborationNotus/_Access" whose button opened <c>/CollaborationNotus/_Access</c> instead of the
/// space. Cause: the notifier used <see cref="MeshNode.MainNode"/>, which an assignment written
/// without an explicit MainNode gets auto-stamped to its NAMESPACE — the container.
/// </summary>
public class AccessGrantNotifierTargetTest
{
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode Assignment(
        string subject, string @namespace, string mainNode, string createdBy = "admin", string role = "Admin") =>
        new($"{subject}_Access", @namespace)
        {
            NodeType = "AccessAssignment",
            MainNode = mainNode,
            CreatedBy = createdBy,
            Content = new AccessAssignment
            {
                AccessObject = subject,
                Roles = [new RoleAssignment { Role = role }],
            },
        };

    [Fact]
    public void AutoStampedMainNode_StripsTheAccessContainer()
    {
        // The reported shape: no explicit MainNode at write time → auto-stamped to the namespace.
        var node = Assignment("alice", "CollaborationNotus/_Access", "CollaborationNotus/_Access");

        Assert.True(AccessGrantNotifier.TryResolveGrant(
            node, Options, out var recipient, out var granted, out var roleText));
        Assert.Equal("alice", recipient);
        Assert.Equal("CollaborationNotus", granted);
        Assert.Equal("Admin", roleText);
    }

    [Fact]
    public void NestedNode_ResolvesToTheGrantedNodeNotThePartition()
    {
        // A grant on a node deep inside a space must link to THAT node, not the space root.
        var node = Assignment("alice", "Space/Docs/Report/_Access", "Space/Docs/Report/_Access");

        Assert.True(AccessGrantNotifier.TryResolveGrant(node, Options, out _, out var granted, out _));
        Assert.Equal("Space/Docs/Report", granted);
    }

    [Fact]
    public void ExplicitMainNode_IsAlreadyTheGovernedNode_AndIsLeftAlone()
    {
        // The Access Control tab writes MainNode = the granted node path.
        var node = Assignment("alice", "TeamSpace/_Access", "TeamSpace");

        Assert.True(AccessGrantNotifier.TryResolveGrant(node, Options, out _, out var granted, out _));
        Assert.Equal("TeamSpace", granted);
    }

    [Fact]
    public void LegacyShape_WithoutAnAccessSegment_ResolvesToItsNamespace()
    {
        // Pre-_Access-segment placement (production shape repaired by V01): `{scope}/{subject}_Access`.
        var node = Assignment("alice", "TestOrg", "TestOrg");

        Assert.True(AccessGrantNotifier.TryResolveGrant(node, Options, out _, out var granted, out _));
        Assert.Equal("TestOrg", granted);
    }

    [Fact]
    public void MainNodeFallback_WhenTheAssignmentHasNoNamespace()
    {
        var node = Assignment("alice", "", "TeamSpace/_Access");

        Assert.True(AccessGrantNotifier.TryResolveGrant(node, Options, out _, out var granted, out _));
        Assert.Equal("TeamSpace", granted);
    }

    [Fact]
    public void RootScopeGrant_RaisesNoNotification()
    {
        // A mesh-wide grant (global-admin seed) governs no node: there is nothing to name or link
        // to, so it must be skipped rather than mail "access to _Access" / an empty title.
        var node = Assignment("alice", "_Access", "_Access");

        Assert.False(AccessGrantNotifier.TryResolveGrant(node, Options, out _, out _, out _));
    }

    [Fact]
    public void ASegmentThatMerelyStartsWithAccess_IsNotStripped()
    {
        // "_AccessLog" is not the "_Access" satellite segment — the path must survive intact.
        var node = Assignment("alice", "Space/_AccessLog", "Space/_AccessLog");

        Assert.True(AccessGrantNotifier.TryResolveGrant(node, Options, out _, out var granted, out _));
        Assert.Equal("Space/_AccessLog", granted);
    }
}
