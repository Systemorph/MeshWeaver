using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests the VUser node creation flow as used by VirtualUserMiddleware.
/// Verifies that CreateNodeAsync (via AwaitResponse) works correctly
/// and handles the "already exists" case without hanging.
/// </summary>
public class VirtualUserNodeCreationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Each [Fact] uses a distinct visitor ID (visitor1/visitor2/visitor3),
    // so SP-sharing is collision-safe.
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity();

    /// <summary>
    /// A portal hub can create VUser nodes via ImpersonateAsHub scope.
    /// This mirrors the VirtualUserMiddleware flow where the portal hub's
    /// address (portal/xxx) is used as the identity for the access check.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void PortalHub_CreateVUser_Succeeds()
    {
        var portalHub = CreatePortalHub();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var node = CreateVUserNode("visitor1");
        using (accessService.ImpersonateAsHub(portalHub))
        {
            var created = meshService.CreateNode(node).Should().Emit();
            created.Should().NotBeNull();
            created.Path.Should().Be("VUser/visitor1");
            created.State.Should().Be(MeshNodeState.Active);
        }
    }

    /// <summary>
    /// Creating a VUser that already exists should throw, not hang.
    /// This was the root cause of the portal startup hang — the Post+RegisterCallback
    /// race condition in HubNodePersistence caused the task to never complete.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void PortalHub_CreateVUser_AlreadyExists_ReturnsFailure()
    {
        var portalHub = CreatePortalHub();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var node = CreateVUserNode("visitor2");

        using (accessService.ImpersonateAsHub(portalHub))
        {
            // First creation succeeds
            meshService.CreateNode(node).Should().Emit();

            // Second creation should throw, not hang
            Action act = () => meshService.CreateNode(node).Wait();
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*already exists*");
        }
    }

    /// <summary>
    /// Mimics the full VirtualUserMiddleware.EnsureVirtualUserNodeAsync flow:
    /// 1. Check if VUser exists via persistence query
    /// 2. If not, create it via IMeshService with hub identity scope
    /// 3. Second call detects VUser exists, skips creation
    /// </summary>
    [Fact(Timeout = 20000)]
    public void EnsureVirtualUserNode_CheckThenCreate_NoHang()
    {
        var portalHub = CreatePortalHub();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var virtualUserId = "visitor3";
        var node = CreateVUserNode(virtualUserId);

        using (accessService.ImpersonateAsHub(portalHub))
        {
            // First call: VUser doesn't exist in persistence
            var existsBefore = NodeExists(meshService, $"VUser/{virtualUserId}");
            existsBefore.Should().BeFalse("VUser node should not exist yet");

            meshService.CreateNode(node).Should().Emit();

            // Second call: VUser exists after creation — wait reactively for it to surface.
            var existsAfter = meshService.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:VUser/{virtualUserId}"))
                .Select(c => c.Items.Any())
                .Should().Match(any => any);
            existsAfter.Should().BeTrue("VUser node should exist after creation");
        }
    }

    /// <summary>
    /// Checks if a node exists via the initial query snapshot.
    /// </summary>
    private static bool NodeExists(IMeshService meshService, string path)
        => meshService.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}"))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items.Count > 0;

    private IMessageHub CreatePortalHub()
        => Mesh.ServiceProvider.CreateMessageHub(
            new Address("portal", "test-" + Guid.NewGuid().ToString("N")[..8]),
            c => c);

    private static MeshNode CreateVUserNode(string id) => new(id, "VUser")
    {
        Name = "Guest",
        NodeType = "VUser",
        State = MeshNodeState.Active,
        Content = new AccessObject
        {
            IsVirtual = true
        }
    };
}
