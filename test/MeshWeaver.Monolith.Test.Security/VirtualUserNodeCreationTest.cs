using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Monolith.Test.Security;

/// <summary>
/// Tests the VUser node creation flow as used by VirtualUserMiddleware.
/// Verifies that CreateNodeAsync (via AwaitResponse) works correctly
/// and handles the "already exists" case without hanging.
/// </summary>
public class VirtualUserNodeCreationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity();

    /// <summary>
    /// A portal hub can create VUser nodes via ImpersonateAsHub scope.
    /// This mirrors the VirtualUserMiddleware flow where the portal hub's
    /// address (portal/xxx) is used as the identity for the access check.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task PortalHub_CreateVUser_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var portalHub = CreatePortalHub();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var node = CreateVUserNode("visitor1");
        using (accessService.ImpersonateAsHub(portalHub))
        {
            var created = await meshService.CreateNodeAsync(node, ct);
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
    [Fact(Timeout = 10000)]
    public async Task PortalHub_CreateVUser_AlreadyExists_ReturnsFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var portalHub = CreatePortalHub();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var node = CreateVUserNode("visitor2");

        using (accessService.ImpersonateAsHub(portalHub))
        {
            // First creation succeeds
            await meshService.CreateNodeAsync(node, ct);

            // Second creation should throw, not hang
            var act = async () => await meshService.CreateNodeAsync(node, ct);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*already exists*");
        }
    }

    /// <summary>
    /// Mimics the full VirtualUserMiddleware.EnsureVirtualUserNodeAsync flow:
    /// 1. Check if VUser exists (PingRequest to VUser address)
    /// 2. If not, create it via IMeshService with hub identity scope
    /// 3. Second call detects VUser exists via Ping, skips creation
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task EnsureVirtualUserNode_CheckThenCreate_NoHang()
    {
        var ct = TestContext.Current.CancellationToken;
        var portalHub = CreatePortalHub();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var virtualUserId = "visitor3";
        var vUserAddress = new Address("VUser", virtualUserId);
        var node = CreateVUserNode(virtualUserId);

        using (accessService.ImpersonateAsHub(portalHub))
        {
            // First call: VUser doesn't exist, Ping should fail, then create
            var existsBefore = await CheckNodeExistsAsync(portalHub, vUserAddress);
            existsBefore.Should().BeFalse("VUser node should not exist yet");

            await meshService.CreateNodeAsync(node, ct);

            // Second call: VUser exists, Ping should succeed, skip creation
            var existsAfter = await CheckNodeExistsAsync(portalHub, vUserAddress);
            existsAfter.Should().BeTrue("VUser node should exist after creation");
        }
    }

    /// <summary>
    /// Mirrors the CheckNodeExistsAsync logic from VirtualUserMiddleware.
    /// </summary>
    private static async Task<bool> CheckNodeExistsAsync(IMessageHub hub, Address nodeAddress)
    {
        try
        {
            await hub.AwaitResponse(
                new PingRequest(),
                o => o.WithTarget(nodeAddress),
                new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

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
