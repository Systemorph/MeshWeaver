using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Dedicated coverage for the "current path + parent chain via CombineLatest"
/// access-control semantics: a permission check on a path must resolve a grant
/// living anywhere along the scope chain — the receiving hub's own
/// <c>{path}/_Access</c> AND every ancestor's <c>_Access</c>, walked
/// recursively via <see cref="SecurityService"/> internal
/// <c>ObserveScopeAssignments</c>. Repro target: the FutuRe Group_*
/// cascading failures where <c>FutuRe/Analysis</c> needed Read on
/// <c>FutuRe/EuropeRe/Analysis</c> via the grant at
/// <c>FutuRe/EuropeRe/Analysis/_Access/FutuRe_Analysis_Access.json</c> and
/// the 10s timeout fired because the recursion chain stalled.
///
/// <para>Uses STATIC <see cref="AssignmentNodeFactory"/> seeds so the
/// test isolates the SecurityService scope-recursion math from
/// file-system persistence + synced-query plumbing. The companion
/// in-progress investigation tracks the file-system + synced-query path
/// separately — see ActivityNodeType WithTypes fix.</para>
/// </summary>
public class FilePersistedAccessRecursionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout =>
        new CancellationTokenSource(15.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                // Grant at deep scope — the recursion chain must reach this
                // when the consumer asks for permission at the same scope.
                AssignmentNodeFactory.UserRole(
                    userId: "DataReader",
                    roleId: "Viewer",
                    scope: "tenant/team/project"));

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    /// <summary>
    /// Baseline: a user with NO assignment anywhere gets <c>Permission.None</c>.
    /// </summary>
    [Fact(Timeout = 15_000)]
    public async Task UnknownUser_OnDeepScope_HasNoPermission()
    {
        var perms = await Mesh.GetPermissionAsync("tenant/team/project", "unknown-user", TestTimeout);
        perms.Should().Be(Permission.None);
    }

    /// <summary>
    /// The load-bearing assertion: a Viewer grant seeded at
    /// <c>tenant/team/project/_Access</c> must be discovered by a permission
    /// check at the SAME path. This is the "current path" leg of the
    /// CombineLatest chain — without recursion to non-empty scopes,
    /// <c>ObserveScopeAssignments</c> would only see the root scope's
    /// statics and miss this grant.
    /// </summary>
    [Fact(Timeout = 15_000)]
    public async Task ScopedAssignment_AtCurrentPath_ResolvesViewerRole()
    {
        var perms = await Mesh.GetPermissionAsync(
            "tenant/team/project", "DataReader",
            until: p => p.HasFlag(Permission.Read), TestTimeout);
        perms.Should().HaveFlag(Permission.Read,
            "Viewer assignment at the SAME scope as the check must grant Read — " +
            "this exercises the SELF leg of ObserveScopeAssignments' CombineLatest.");
        perms.Should().NotHaveFlag(Permission.Update,
            "Viewer is read-only.");
    }

    /// <summary>
    /// The recursion contract: the same Viewer grant seeded at
    /// <c>tenant/team/project</c> must flow DOWN to a descendant check
    /// at <c>tenant/team/project/child</c>. The descendant scope's SELF
    /// query has no assignment; the resolution comes purely from the
    /// recursive parent chain.
    /// </summary>
    [Fact(Timeout = 15_000)]
    public async Task ScopedAssignment_AtAncestorPath_InheritsToDescendant()
    {
        var perms = await Mesh.GetPermissionAsync(
            "tenant/team/project/child", "DataReader",
            until: p => p.HasFlag(Permission.Read), TestTimeout);
        perms.Should().HaveFlag(Permission.Read,
            "ancestor's Viewer assignment must flow down through the recursive " +
            "ObserveScopeAssignments chain — the descendant's SELF scope has no " +
            "_Access entry, the grant comes from the parent leg.");
    }
}
