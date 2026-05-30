using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.Validation;
using MeshWeaver.Graph;
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
/// Tests hub-to-hub access control: subscription rejection propagation
/// and hub-identity-based VUser node creation via ImpersonateAsHub().
/// </summary>
public class HubAccessControlTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Each [Fact] uses a distinct VUser name (testVUser / anonUser / guest42 /
    // guest99); SecuredHub is fixture-time and never mutated. Safe to share.
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(new MeshNode("SecuredHub") { Name = "Secured Hub" })
            .ConfigureDefaultNodeHub(c => c
                .AddData(d => d)
                .WithServices(sc => sc.AddScoped<IDataValidator, RejectAllReadsValidator>()));

    /// <summary>
    /// When a subscriber sends a SubscribeRequest to a hub whose Read validator rejects it,
    /// the error should propagate as an exception on the stream â€” not cause a silent timeout.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void SubscribeRequest_RejectedByValidator_PropagatesException()
    {
        var client = GetClient();
        var hubAddress = new Address("SecuredHub");

        client.Observe(new PingRequest(), o => o.WithTarget(hubAddress)).Should().Emit();

        var subscriberHub = Mesh.ServiceProvider.CreateMessageHub(
            new Address("subscriber", "1"),
            c => c.AddData(d => d));

        var workspace = subscriberHub.GetWorkspace();
        var stream = workspace.GetRemoteStream<EntityStore>(hubAddress, new CollectionsReference("test"));

        Action act = () => stream
            .Timeout(5.Seconds())
            .Wait();

        var ex = act.Should().Throw<Exception>().Which;
        ex.Should().NotBeOfType<TimeoutException>(
            "subscription rejection should propagate as an exception, not cause a silent timeout");
        ex.ToString().Should().Contain("Access denied",
            "the error should contain the validator's rejection message");
    }

    /// <summary>
    /// A portal hub can create VUser nodes using ImpersonateAsHub scope.
    /// The VUserAccessRule allows portal namespace identities.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void PortalHub_CanCreateVUserNode_WithImpersonateScope()
    {
        var portalHub = Mesh.ServiceProvider.CreateMessageHub(
            new Address("portal", "test1"),
            c => c);

        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var nodeFactory = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var vUserNode = new MeshNode("testVUser", "VUser")
        {
            Name = "Test Guest",
            NodeType = "VUser",
            State = MeshNodeState.Active,
            Content = new AccessObject
            {
                IsVirtual = true
            }
        };

        using (accessService.ImpersonateAsHub(portalHub))
        {
            var created = nodeFactory.CreateNode(vUserNode).Should().Emit();
            created.Should().NotBeNull();
            created.Path.Should().Be("VUser/testVUser");
        }
    }

    /// <summary>
    /// Non-portal identities should NOT be able to create VUser nodes.
    /// The VUserAccessRule replaces RLS and only allows portal namespace identities.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void NonPortalIdentity_CannotCreateVUserNode()
    {
        var nodeFactory = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var vUserNode = new MeshNode("anonUser", "VUser")
        {
            Name = "Anonymous Guest",
            NodeType = "VUser",
            State = MeshNodeState.Active,
            Content = new AccessObject
            {
                IsVirtual = true
            }
        };

        // "some-user" is not in the portal namespace â€” VUserAccessRule denies
        Action act = () => nodeFactory.CreateNode(vUserNode).Wait();
        act.Should().Throw<Exception>();
    }

    /// <summary>
    /// A portal hub posts CreateNodeRequest directly with ImpersonateAsHub().
    /// The hub's address becomes the identity via the post pipeline â€” no IMeshService needed.
    /// This mirrors how VirtualUserMiddleware creates VUser nodes.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void PortalHub_ImpersonateAsHub_CanCreateVUser()
    {
        // Create a portal hub
        var portalHub = Mesh.ServiceProvider.CreateMessageHub(
            new Address("portal", "mysite"),
            c => c);

        var vUserNode = new MeshNode("guest42", "VUser")
        {
            Name = "Guest 42",
            NodeType = "VUser",
            State = MeshNodeState.Active,
            Content = new AccessObject
            {
                IsVirtual = true
            }
        };

        // Post CreateNodeRequest directly with ImpersonateAsHub() â€” bypasses IMeshService.
        // Target the mesh hub where CreateNodeRequest handler is registered.
        var response = portalHub.Observe(new CreateNodeRequest(vUserNode), o => o.WithTarget(Mesh.Address).ImpersonateAsHub())
            .Should().Emit();

        response.Message.Success.Should().BeTrue(
            "portal hub should be allowed to create VUser nodes via ImpersonateAsHub()");
        response.Message.Node.Should().NotBeNull();
        response.Message.Node!.Path.Should().Be("VUser/guest42");
    }

    /// <summary>
    /// A non-portal hub posting CreateNodeRequest with ImpersonateAsHub() should fail.
    /// The VUserAccessRule only allows identities in the "portal" namespace.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void NonPortalHub_ImpersonateAsHub_CannotCreateVUser()
    {
        // Create a non-portal hub
        var analyticsHub = Mesh.ServiceProvider.CreateMessageHub(
            new Address("analytics", "hub1"),
            c => c);

        var vUserNode = new MeshNode("guest99", "VUser")
        {
            Name = "Guest 99",
            NodeType = "VUser",
            State = MeshNodeState.Active,
            Content = new AccessObject
            {
                IsVirtual = true
            }
        };

        // Post CreateNodeRequest with ImpersonateAsHub() â€” non-portal identity should be denied.
        // Target the mesh hub where CreateNodeRequest handler is registered.
        var response = analyticsHub.Observe(new CreateNodeRequest(vUserNode), o => o.WithTarget(Mesh.Address).ImpersonateAsHub())
            .Should().Emit();

        response.Message.Success.Should().BeFalse(
            "non-portal hub should be denied when creating VUser nodes");
    }

    private class RejectAllReadsValidator : IDataValidator
    {
        public IReadOnlyCollection<DataOperation> SupportedOperations => [DataOperation.Read];

        public IObservable<DataValidationResult> Validate(DataValidationContext context)
            => Observable.Return(DataValidationResult.Invalid(
                "Access denied: no read permissions",
                DataValidationRejectionReason.Unauthorized));
    }
}
