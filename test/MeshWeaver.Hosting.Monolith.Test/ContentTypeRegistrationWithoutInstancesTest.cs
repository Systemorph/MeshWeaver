using System;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 <b>A NodeType's content type must register WITHOUT a single instance existing.</b>
///
/// <para>Registration lived exclusively inside the type's HubConfiguration
/// (<c>MeshDataSource.WithContentType</c>), which runs when an INSTANCE hub cold-activates. A
/// portal where a type is defined and compiled but has no live instance therefore never learns the
/// content type at all — and every read seam then degrades that <c>$type</c> to an untyped
/// JsonElement by design.</para>
///
/// <para><b>Measured on a real portal, 2026-09-01:</b> zero nodes carried
/// <c>nodeType: Store/Plugin</c> (installed course roots are re-typed to <c>Space</c>), so
/// <c>PluginContent</c> was never registered; every Store cover computed NO action buttons (no
/// Get, no Install, no Update), and with the Update lane dead, installed course content became
/// unrefreshable — one missing registration disabling the whole commerce surface. The cloud
/// escapes only by the accident of having live <c>Store/Plugin</c> instances.</para>
///
/// <para>The cure this test pins: a STATIC definition being KNOWN is enough — at start the
/// platform sweeps every <c>AddMeshNodes</c> definition carrying a HubConfiguration and registers
/// its content types through the same transient-probe build the schema probes use (configuration
/// build only, nothing started). Compiled (dynamic) types are deliberately out of the sweep's
/// scope — they register at instance activation, and probing their bytes eagerly is the boot cost
/// (and the store-corrupting self-heal trigger) <c>ShippedPrebuiltBundlesTest</c> forbids.</para>
/// </summary>
public class ContentTypeRegistrationWithoutInstancesTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string InstancelessNodeType = "InstancelessGadget";

    /// <summary>Content type of the never-instantiated NodeType below.</summary>
    public record InstancelessGadgetContent
    {
        /// <summary>Gadget label.</summary>
        public string? Label { get; init; }
    }

    /// <summary>Fresh mesh: the assertion is about what registration exists WITHOUT any
    /// activation, so nothing from a sibling test may leak in.</summary>
    protected override bool ShareMeshAcrossTests => false;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(InstancelessNodeType)
            {
                Name = "Instanceless Gadget",
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<InstancelessGadgetContent>())
            });

    /// <summary>
    /// 🚨 THE assertion: no instance of the NodeType is ever created, no hub of it ever activates —
    /// and the mesh-wide registry must still answer for its path, because the DEFINITION is known.
    /// This is exactly the state a portal is in for every commerce/content type whose instances
    /// happen not to exist yet, and "no instances" must never mean "content of this type is
    /// unreadable everywhere".
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void ADefinedNodeType_RegistersItsContentType_WithoutAnyInstanceActivating()
    {
        var registry = Mesh.ServiceProvider.GetRequiredService<IMeshContentTypeRegistry>();

        registry.TryResolveByNodeType(InstancelessNodeType, out var resolved).Should().BeTrue(
            "a defined NodeType's content type must be registered from the definition itself — "
            + "requiring an instance to activate first means every read of such content degrades "
            + "to an untyped JsonElement on a portal that happens to have no instances (the "
            + "dead-Store defect, measured 2026-09-01)");
        resolved.Should().Be(typeof(InstancelessGadgetContent),
            "the registered type must be the definition's declared content type");
    }
}
