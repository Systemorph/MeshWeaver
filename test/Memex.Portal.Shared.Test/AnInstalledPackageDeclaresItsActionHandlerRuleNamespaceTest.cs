using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.ActionLinks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// MeshWeaver#4692, shape 3 — action-handler rules count only from DECLARED namespaces, and an
/// installed package's own partition is declared by its install record, read LIVE.
///
/// <para><b>What is observed.</b> A real monolith mesh with the plugin catalog. A rule is written
/// into a package partition BEFORE the package is installed: it is refused as
/// <see cref="ActionHandlerRules.UndeclaredNamespace"/> and named in the unclaimed detail. Then the
/// install record (<c>Plugins/{id}</c>, the installer's own declaration of the partition) is
/// written while the portal runs, and the SAME rule resolves on the next chain emission — no
/// recycle, no restart.</para>
/// </summary>
public class AnInstalledPackageDeclaresItsActionHandlerRuleNamespaceTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>A partition-root NodeType standing in for an installed package's root.</summary>
    private const string PluginLikeNodeType = "TestStore/InstalledPlugin";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .AddMeshNodes(new MeshNode(PluginLikeNodeType)
            {
                Name = "Installed plugin root",
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition { DefaultNamespace = "", RestrictedToNamespaces = [""] },
            });

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private async Task CreateAsSystem(MeshNode node)
    {
        var response = await Access
            .RunAsSystem(() => ObserveNodeOperation(new CreateNodeRequest(node)))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(TestTimeouts.Convergence)
            .Await();
        response.Success.Should().BeTrue($"the fixture write '{node.Path}' must land: {response.Error}");
    }

    [Fact]
    public async Task ANamespaceDeclaredAfterStart_ByInstallingItsPackage_CountsLive()
    {
        var packageId = "rulepkg" + Guid.NewGuid().ToString("N")[..8];
        var key = "test.action-" + Guid.NewGuid().ToString("N")[..8];
        var rulePath = $"{packageId}/ActionHandlers/coupon";

        await CreateAsSystem(new MeshNode(packageId)
        {
            Name = packageId, NodeType = PluginLikeNodeType, State = MeshNodeState.Active,
        });
        await CreateAsSystem(new MeshNode("coupon", $"{packageId}/ActionHandlers")
        {
            Name = "coupon rule",
            NodeType = ActionHandlerRuleNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new ActionHandlerRule { Id = "coupon", Claims = [key], Handler = $"{packageId}/Coupons/Review" },
        });

        // Before the install: the rule is in the snapshot and refused by name.
        await Mesh.ActionHandlerChain()
            .Where(chain => chain.Rejected.Contains(
                new RejectedActionHandlerRule(rulePath, ActionHandlerRules.UndeclaredNamespace)))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        var before = await Mesh.ResolveActionHandler(new ActionInvocation { HandlerKey = key, HandlerVersion = 1 })
            .Timeout(TestTimeouts.Convergence).Await();
        before.Outcome.Status.Should().Be(ActionOutcomeStatus.UnknownHandler,
            "a partition nobody declared does not count, however the node got there");
        before.Outcome.Detail.Should().Contain($"{rulePath} ({ActionHandlerRules.UndeclaredNamespace})");

        // The install record IS the declaration — written while the portal runs.
        await CreateAsSystem(MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{packageId}") with
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = packageId,
            State = MeshNodeState.Active,
            Content = new PackageManifest { Id = packageId, Name = packageId, Version = "1.0.0", TargetPartition = packageId },
        });

        var chain = await Mesh.ActionHandlerChain()
            .Where(c => c.Handlers.Any(h => h.Capability.HandlerKey == key))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        chain.DeclaredNamespaces.Should().Contain(packageId);
        chain.Rejected.Should().NotContain(r => r.Path == rulePath);

        var after = await Mesh.ResolveActionHandler(new ActionInvocation { HandlerKey = key, HandlerVersion = 1 })
            .Timeout(TestTimeouts.Convergence).Await();
        after.Outcome.Status.Should().Be(ActionOutcomeStatus.Ready,
            "a newly installed package's rules count on the next resolution — no recycle");
    }
}
