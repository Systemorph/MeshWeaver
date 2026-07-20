using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// #385 RC4 — "Shared with me". A user invited into a module that lives in ANOTHER partition can
/// reach it by URL but it is invisible in nav. The home's "Shared with me" tab sources the caller's
/// own <c>AccessAssignment</c> grants (<c>content.accessObject == me</c>) and resolves each to its
/// governed cross-partition target scope (<see cref="MeshNode.MainNode"/>) — the exact projection
/// <c>UserActivityLayoutAreas.SharedTargetPaths</c> performs for the band (unit-tested in
/// <c>HomeCatalogTest</c>). This is purely additive: it READS the caller's own readable grants,
/// no security surface changes.
/// </summary>
public class SharedWithMeDiscoveryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddSpaceType()
            .AddMeshNodes(
                // A space in ANOTHER partition (OrgA) with a module the invitee is granted into.
                new MeshNode("OrgA")
                {
                    Name = "Org A", NodeType = "Space", State = MeshNodeState.Active, Content = new Space()
                },
                new MeshNode("Module", "OrgA")
                {
                    Name = "Shared Module", NodeType = "Markdown", State = MeshNodeState.Active
                },
                // alice is invited into OrgA/Module (Viewer = Read) — a cross-partition grant.
                AssignmentNodeFactory.UserRole("alice", "Viewer", "OrgA/Module")
            );

    /// <summary>Security discovery test — no blanket admin grant (that would mask the per-user path).</summary>
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    /// <summary>The same projection the "Shared with me" tab applies (public AccessSubjectQueries helpers).</summary>
    private static IReadOnlyList<string> CrossPartitionTargets(IEnumerable<MeshNode> assignments, string ownerId)
        => assignments
            .Select(a => AccessSubjectQueries.ScopeOfAssignment(
                string.IsNullOrEmpty(a.MainNode) ? a.Path : a.MainNode)?.Trim('/'))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .Where(s => !string.Equals(AccessSubjectQueries.Partition(s), ownerId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    [Fact(Timeout = 20000)]
    public async Task InvitedCrossPartitionModule_AppearsInSharedTargets()
    {
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = "alice", Name = "Alice" });

        // The exact query ObserveSharedTargets issues for the "Shared with me" tab.
        var change = await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
                "nodeType:AccessAssignment content.accessObject:alice"))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial);

        Output.WriteLine($"alice's readable assignments: {change.Items.Count}");
        foreach (var a in change.Items)
            Output.WriteLine($"  - {a.Path} (mainNode={a.MainNode})");

        CrossPartitionTargets(change.Items, "alice").Should().Contain("OrgA/Module",
            "an invited cross-partition module must be discoverable via the Shared-with-me projection");
    }

    [Fact(Timeout = 20000)]
    public async Task OwnPartitionGrant_IsNotListedAsSharedWithMe()
    {
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = "alice", Name = "Alice" });

        var change = await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
                "nodeType:AccessAssignment content.accessObject:alice"))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial);

        // Treat OrgA as the owner's own partition → the grant is NOT "shared with me from elsewhere".
        CrossPartitionTargets(change.Items, "OrgA").Should().NotContain("OrgA/Module",
            "a grant inside the owner's own partition is not surfaced as shared-with-me");
    }
}
