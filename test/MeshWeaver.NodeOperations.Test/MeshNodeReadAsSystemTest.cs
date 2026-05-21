using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Pins the architectural invariant: <b>MeshNode reads run as System</b>.
///
/// <para>Routing — mapping a path to a hub address — and the read-side
/// infrastructure caches (<see cref="IMeshNodeStreamCache"/>, the underlying
/// <see cref="MeshNodeStreamHandle"/>, and the query-mirror caches behind
/// <c>workspace.GetQuery</c>) are framework infrastructure. They are NOT
/// user-attributable operations. User-level access control happens at the
/// APPLICATION layer where the read value is consumed (handlers, layout
/// areas, AI tools) — not at the sync-stream / SubscribeRequest seam.</para>
///
/// <para>Without the System bypass, the SubscribeRequest carries whatever
/// ambient AccessContext happens to be on the calling thread — often a
/// <c>sync/{streamId}</c> hub address (workspace emission threads) or <c>null</c>.
/// The owner's <see cref="AccessControlPipeline"/> then denies because no
/// <see cref="AccessAssignment"/> exists for those addresses, the stream
/// goes empty, callers see <c>NotFound</c> for paths that DO exist, and
/// downstream operations (delete, recompile, navigation) behave incoherently.</para>
///
/// <para>The two cache types covered here:
/// <list type="bullet">
///   <item>(a) <see cref="IMeshNodeStreamCache"/> — single-node-by-path cache.
///        Hydrated under <c>AccessService.ImpersonateAsSystem</c> at Connect time.</item>
///   <item>(b) <c>workspace.GetQuery</c> / <see cref="SyncedQueryMeshNodes"/> —
///        query-set cache (also used by access-rights propagation).
///        Hydrated with <c>WellKnownUsers.System</c> on the
///        <see cref="MeshQueryRequest"/>.</item>
/// </list>
/// Both are infrastructure; both run as System.</para>
/// </summary>
public class MeshNodeReadAsSystemTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddRowLevelSecurity();

    protected override async Task SetupAccessRightsAsync()
    {
        // Grant Admin role on the probe namespaces so test setup (CreateNode)
        // succeeds. The tests then SWITCH the ambient identity to an unprivileged
        // user mid-test to assert that READS still work — that's the system-bypass
        // we're proving.
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(
            AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", "SystemReadProbe"))
            .FirstAsync().ToTask(ct);
        await meshService.CreateNode(
            AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", "CacheReadProbe"))
            .FirstAsync().ToTask(ct);
        await meshService.CreateNode(
            AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", "QueryCacheProbe"))
            .FirstAsync().ToTask(ct);
        await Mesh.WaitForPermissionAsync("SystemReadProbe", TestUsers.Admin.ObjectId, Permission.Create, ct);
        await Mesh.WaitForPermissionAsync("CacheReadProbe", TestUsers.Admin.ObjectId, Permission.Create, ct);
        await Mesh.WaitForPermissionAsync("QueryCacheProbe", TestUsers.Admin.ObjectId, Permission.Create, ct);
    }

    /// <summary>
    /// Reads of a remote MeshNode via <c>workspace.GetMeshNodeStream(path)</c>
    /// MUST succeed even when the ambient <see cref="AccessService.Context"/>
    /// belongs to a user with NO read permission on the path. The handle
    /// opens the underlying remote subscription under
    /// <see cref="AccessService.ImpersonateAsSystem"/>, so the
    /// <c>SubscribeRequest</c> bypasses RLS at the owner.
    ///
    /// <para>Application-layer access control (e.g. the
    /// <see cref="IMeshNodeStreamCache.GetStream"/> gate that probes
    /// <see cref="GetPermissionRequest"/>) is what enforces user-level RLS —
    /// the sync-stream seam is system infrastructure.</para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task GetMeshNodeStream_RemotePath_UsesSystemIdentity_NotAmbientUser()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodeId = $"Md_{Guid.NewGuid().AsString()}";
        var nodePath = $"SystemReadProbe/{nodeId}";

        await NodeFactory.CreateNode(
            new MeshNode(nodeId, "SystemReadProbe")
            {
                Name = "System-read probe node",
                NodeType = "Markdown",
            });

        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();

        // Set ambient context to a user with no granted permissions anywhere.
        // Without the System bypass on the read path, the SubscribeRequest
        // would carry this user's identity (or fall back to a sync/<id> hub
        // address on the emission thread), the owner's RLS would deny, and
        // the stream would emit nothing within the timeout.
        accessService.SetContext(new AccessContext
        {
            ObjectId = "unprivileged-reader",
            Name = "Unprivileged",
        });

        try
        {
            var node = await workspace.GetMeshNodeStream(nodePath)
                .Take(1)
                .Timeout(10.Seconds())
                .FirstAsync()
                .ToTask(ct);

            node.Should().NotBeNull(
                because: "MeshNode reads are infrastructure — the handle opens the " +
                         "underlying remote subscription under ImpersonateAsSystem so " +
                         "the SubscribeRequest bypasses the owner's RLS check. Per-user " +
                         "enforcement lives at the consumer layer (cache.GetStream / " +
                         "application handlers), not at the sync-stream seam.");
            node.Path.Should().Be(nodePath);
            node.Name.Should().Be("System-read probe node");
        }
        finally
        {
            accessService.SetContext(null);
        }
    }

    /// <summary>
    /// The cached <see cref="IMeshNodeStreamCache.GetStream"/> path also reads
    /// under System (the upstream is hydrated once with
    /// <see cref="AccessService.ImpersonateAsSystem"/>). The per-user gate at
    /// the cache layer is a SEPARATE <see cref="GetPermissionRequest"/> probe
    /// against the owning node hub — the upstream read itself is
    /// system-impersonated.
    ///
    /// <para>This test pins the contract from the consumer's perspective: if
    /// the ambient user has Read permission, the cached stream emits the
    /// node. The upstream system-read is invisible to the consumer.</para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task CacheGetStream_WithReadPermission_EmitsNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodeId = $"Md_{Guid.NewGuid().AsString()}";
        var nodePath = $"CacheReadProbe/{nodeId}";

        await NodeFactory.CreateNode(
            new MeshNode(nodeId, "CacheReadProbe")
            {
                Name = "Cache-read probe",
                NodeType = "Markdown",
            });

        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

        var node = await cache.GetStream(nodePath)
            .Where(n => n is not null)
            .Take(1)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        node.Should().NotBeNull();
        node.Path.Should().Be(nodePath);
        node.Name.Should().Be("Cache-read probe");
    }

    /// <summary>
    /// The query-set cache (<c>workspace.GetQuery</c> / SyncedQueryMeshNodes)
    /// also runs as System — its <see cref="MeshQueryRequest"/> is constructed
    /// with <see cref="WellKnownUsers.System"/> identity so the upstream
    /// query mirror doesn't go through per-user RLS. This is critical for e.g.
    /// the SecurityService's own access-assignment mirror: the live view of
    /// WHO has access must NOT itself depend on per-user access (chicken-and-
    /// egg).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task WorkspaceGetQuery_RunsAsSystem_EmitsResultsRegardlessOfAmbientUser()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        var nodeId1 = $"Md_{Guid.NewGuid().AsString()}";
        var nodeId2 = $"Md_{Guid.NewGuid().AsString()}";

        await NodeFactory.CreateNode(
            new MeshNode(nodeId1, "QueryCacheProbe") { Name = "First", NodeType = "Markdown" });
        await NodeFactory.CreateNode(
            new MeshNode(nodeId2, "QueryCacheProbe") { Name = "Second", NodeType = "Markdown" });

        // Switch to an unprivileged user — the query cache's upstream is
        // system-impersonated, so its result is independent of ambient
        // identity. (Consumer-side filtering, if any, is a separate concern.)
        accessService.SetContext(new AccessContext { ObjectId = "no-rights", Name = "No Rights" });

        try
        {
            var collection = workspace.GetQuery(
                $"system-read-probe:{Guid.NewGuid()}",
                "namespace:QueryCacheProbe nodeType:Markdown");

            var snapshot = await collection
                .Where(items => items.Count() >= 2)
                .Take(1)
                .Timeout(10.Seconds())
                .FirstAsync()
                .ToTask(ct);

            snapshot.Should().Contain(n => n.Id == nodeId1,
                because: "workspace.GetQuery's upstream runs with WellKnownUsers.System; " +
                         "results are not filtered by the calling thread's ambient identity.");
            snapshot.Should().Contain(n => n.Id == nodeId2);
        }
        finally
        {
            accessService.SetContext(null);
        }
    }
}
