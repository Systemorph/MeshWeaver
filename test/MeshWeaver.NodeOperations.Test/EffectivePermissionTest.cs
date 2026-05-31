using System;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Tests that GetEffectivePermissionsAsync correctly finds persisted AccessAssignment nodes.
/// Protocol:
/// 1) Install SpaceNodeType
/// 2) Create Space "Systemorph"
/// 3) Ask SecurityService.HasPermission → should NOT return None
/// </summary>
public class EffectivePermissionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddSpaceType();

    protected override async Task SetupAccessRightsAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", null)).FirstAsync().ToTask(TestTimeout);
    }

    // async Task + `await … FirstAsync()` (§2a): the blocking reactive `.Should().Emit()` on
    // the CreateNode round-trip starves the mesh-hub pump on the test thread under RLS and
    // deadlocks (it ran the full 45s with no emission). The await yields the thread so the
    // pump runs and the create completes. The GetEffectivePermissions assertion stays blocking
    // — it's a synced-query read served off the test thread (proven by the passing
    // CreateNode_ImpersonateAsHub_UsesHubIdentity which blocks on the same primitive).
    [Fact(Timeout = 60000)]
    public async Task CreateSpace_HasPermission_ReturnsAdmin()
    {
        var ct = TestContext.Current.CancellationToken;

        var spaceNode = MeshNode.FromPath("Systemorph") with
        {
            Name = "Systemorph",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space { Name = "Systemorph" }
        };
        await NodeFactory.CreateNode(spaceNode).FirstAsync().ToTask(ct);

        var permissions = Mesh.GetEffectivePermissions("Systemorph", TestUsers.Admin.ObjectId)
            .Should().Within(90.Seconds()).Match(p => p != Permission.None);

        permissions.Should().NotBe(Permission.None,
            "Creator should have permissions from persisted AccessAssignment on the Space");
        permissions.Should().Be(Permission.All,
            "Admin role grants all permissions");
    }
}
