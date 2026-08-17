using System;
using MeshWeaver.Approvals;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins that the default-node-hub configuration chain is IDEMPOTENT — applying it twice to one
/// hub must be harmless (#1700).
///
/// <para>Twice is not hypothetical: <c>NodeTypeEnrichmentHelpers.WithCompilationErrorOverlay</c>
/// composes <c>MeshConfiguration.DefaultNodeHubConfiguration</c> INTO the instance's hub
/// configuration (<c>config =&gt; overlay(baseConfig(config))</c>), on top of whatever default
/// application the hub creation path already performs — so every package-root node that lands on
/// the unresolvable-NodeType overlay (e.g. a <c>Store/Plugin</c>-typed root in a gate mesh that
/// does not mount Store) runs the chain twice.</para>
///
/// <para>Before the fix, the Approvals module's per-node registration added a
/// <c>MeshDataSource</c> whose id was <c>Guid.NewGuid()</c> on EVERY application, defeating
/// <c>DataContext.Initialize</c>'s documented keep-last-by-id dedupe: the second application
/// produced a second type source for the <c>Approval</c> collection, workspace activation threw
/// <c>ArgumentException: An item with the same key has already been added. Key: Approval</c>,
/// hub creation failed, and every delivery to the hub parked forever behind its init gates —
/// observed as the LinkedIn/Post Tests-area timeout in the MeshWeaver.SocialMedia gate on every
/// post-extraction image. Comments had the identical latent defect.</para>
/// </summary>
public class DefaultNodeChainIdempotenceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The production arrangement: the Approvals module registered on the mesh (every portal
    /// lists it under <c>Modules:Assemblies</c>; the plugin-gate tester calls the same builder
    /// extension), so its per-node-hub registration rides the default node chain.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddApprovals();

    [Fact(Timeout = 30_000)]
    public void DefaultNodeChain_AppliedTwice_StillActivatesTheWorkspace()
    {
        var meshConfiguration = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();
        var defaultChain = meshConfiguration.DefaultNodeHubConfiguration;
        defaultChain.Should().NotBeNull(
            "the mesh registers the Approvals module, whose per-node-hub half rides the default node chain");

        // The overlay's exact composition shape: the default chain applied on top of itself.
        var hub = Mesh.GetHostedHub(
            new Address("double-default-chain", "1"),
            config => defaultChain!(defaultChain(config)));
        hub.Should().NotBeNull();

        // Resolving IWorkspace forces DataContext.Initialize — the duplicate-registration
        // defect threw right here ('duplicate key: Approval') and left the hub uncreatable.
        hub.ServiceProvider.GetRequiredService<IWorkspace>().Should().NotBeNull();
    }
}
