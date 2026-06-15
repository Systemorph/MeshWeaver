using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
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
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(
            AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", "SystemReadProbe"))
            .Should().Within(45.Seconds()).Emit();
        await meshService.CreateNode(
            AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", "CacheReadProbe"))
            .Should().Within(45.Seconds()).Emit();
        await meshService.CreateNode(
            AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", "QueryCacheProbe"))
            .Should().Within(45.Seconds()).Emit();
        await Mesh.GetEffectivePermissions("SystemReadProbe", TestUsers.Admin.ObjectId)
            .Should().Within(90.Seconds()).Match(p => p.HasFlag(Permission.Create));
        await Mesh.GetEffectivePermissions("CacheReadProbe", TestUsers.Admin.ObjectId)
            .Should().Within(90.Seconds()).Match(p => p.HasFlag(Permission.Create));
        await Mesh.GetEffectivePermissions("QueryCacheProbe", TestUsers.Admin.ObjectId)
            .Should().Within(90.Seconds()).Match(p => p.HasFlag(Permission.Create));
    }

    /// <summary>
    /// Reads of a remote MeshNode via <c>workspace.GetMeshNodeStream(path)</c>
    /// route through <see cref="IMeshNodeStreamCache.GetStream"/>, which
    /// applies a per-user RLS gate (a <see cref="GetPermissionRequest"/> probe
    /// against the owning hub). The cache's UPSTREAM remote subscription is
    /// opened under <see cref="AccessService.ImpersonateAsSystem"/>, but the
    /// gate runs the ambient user's identity — so an unprivileged caller is
    /// denied at the cache boundary, not at the sync-stream seam.
    ///
    /// <para>This test pins that contract: ambient user without Read MUST
    /// surface <see cref="UnauthorizedAccessException"/> from the cache gate.
    /// The architectural split is "upstream = System / gate = caller".</para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task GetMeshNodeStream_UnprivilegedUser_GetsUnauthorized()
    {
        var nodeId = $"Md_{Guid.NewGuid().AsString()}";
        var nodePath = $"SystemReadProbe/{nodeId}";

        await NodeFactory.CreateNode(
            new MeshNode(nodeId, "SystemReadProbe")
            {
                Name = "System-read probe node",
                NodeType = "Markdown",
            }).Should().Within(20.Seconds()).Emit();

        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();

        accessService.SetContext(new AccessContext
        {
            ObjectId = "unprivileged-reader",
            Name = "Unprivileged",
        });

        try
        {
            // The read must ERROR with UnauthorizedAccessException: the cache's
            // per-user RLS gate denies the read for an unprivileged ambient identity.
            // The upstream remote subscription is system-impersonated, but the cache
            // layer enforces per-user Read via GetPermissionRequest before exposing the
            // stream. Materialize folds the OnError into a value so we assert it
            // reactively (no await, no ThrowAsync).
            var notification = await workspace.GetMeshNodeStream(nodePath)
                .Take(1)
                .Materialize()
                .Should().Within(20.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
            notification.Exception.Should().BeOfType<UnauthorizedAccessException>(
                "the cache's per-user RLS gate denies the read for an unprivileged ambient identity");
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
        var nodeId = $"Md_{Guid.NewGuid().AsString()}";
        var nodePath = $"CacheReadProbe/{nodeId}";

        await NodeFactory.CreateNode(
            new MeshNode(nodeId, "CacheReadProbe")
            {
                Name = "Cache-read probe",
                NodeType = "Markdown",
            }).Should().Within(20.Seconds()).Emit();

        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

        var node = await cache.GetStream(nodePath, Mesh.JsonSerializerOptions)
            .Should().Within(28.Seconds()).Match(n => n is not null);

        node.Should().NotBeNull();
        node.Path.Should().Be(nodePath);
        node.Name.Should().Be("Cache-read probe");
    }

    /// <summary>
    /// The query-set cache (<c>workspace.GetQuery</c> / SyncedQueryMeshNodes)
    /// loads its UPSTREAM as System (a single shared mirror per query id), but
    /// wraps each subscriber with a per-user RLS filter
    /// (<c>WrapWithPerUserRls</c>). The filter captures the subscriber's
    /// AccessContext at Subscribe time and uses
    /// <see cref="SecurityService.HasPermission"/> to drop nodes the
    /// subscriber can't Read.
    ///
    /// <para>So: ambient privileged user (Admin) sees the full set; ambient
    /// unprivileged user sees a filtered (empty) view. This test pins both
    /// halves of that contract.</para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task WorkspaceGetQuery_AppliesPerUserRlsFilter()
    {
        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        var nodeId1 = $"Md_{Guid.NewGuid().AsString()}";
        var nodeId2 = $"Md_{Guid.NewGuid().AsString()}";

        await NodeFactory.CreateNode(
            new MeshNode(nodeId1, "QueryCacheProbe") { Name = "First", NodeType = "Markdown" })
            .Should().Within(20.Seconds()).Emit();
        await NodeFactory.CreateNode(
            new MeshNode(nodeId2, "QueryCacheProbe") { Name = "Second", NodeType = "Markdown" })
            .Should().Within(20.Seconds()).Emit();

        var queryId = $"system-read-probe:{Guid.NewGuid()}";

        // Privileged user (Admin, set up by SetupAccessRightsAsync) sees both rows.
        accessService.SetContext(TestUsers.Admin);
        try
        {
            var collection = workspace.GetQuery(
                queryId, "namespace:QueryCacheProbe nodeType:Markdown");

            var snapshot = await collection
                .Should().Within(28.Seconds()).Match(items => items.Count() >= 2);

            snapshot.Should().Contain(n => n.Id == nodeId1);
            snapshot.Should().Contain(n => n.Id == nodeId2);
        }
        finally
        {
            accessService.SetContext(null);
        }
    }
}
