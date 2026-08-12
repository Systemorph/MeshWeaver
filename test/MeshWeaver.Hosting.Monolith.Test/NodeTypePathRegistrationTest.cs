using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 Pins the WIRING of the exact content-type route: that a NodeType's path actually REACHES
/// <c>MeshDataSource.WithContentType</c> and lands in <c>IMeshContentTypeRegistry</c> keyed by that
/// path.
///
/// <para><b>Why this specific assertion.</b> <c>TryResolveByNodeType</c> existed for a long time
/// with ZERO production callers: <c>WithContentType</c> called <c>Register(dataType)</c> with no
/// path, so <c>_byNodeType</c> was never populated outside unit tests and every lookup fell through
/// to the contestable bare name. Unit-testing <c>TryRecoverByNodeType</c> in isolation would have
/// stayed green through all of that — it tests the lookup, not whether anything ever writes the
/// key. The path now arrives ambiently (<c>NodeTypePathHolder</c>, stamped where a NodeType's
/// compiled HubConfiguration is applied), and ambient plumbing is exactly the kind that can be
/// silently absent: a byte-identical plugin-gate report cannot tell "the stamp works but this
/// workload never exercises it" from "the stamp never happens". So the assertion is about the KEY
/// BEING PRESENT after a real activation, not about lookup behaviour.</para>
///
/// <para>A STATIC NodeType is used deliberately — it exercises the same
/// <c>MeshNodeHubFactory</c> → stamp → <c>WithContentType</c> path a runtime-compiled one does,
/// without dragging Roslyn into the test.</para>
/// </summary>
public class NodeTypePathRegistrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string StampedNodeType = "StampedProduct";

    /// <summary>Content type of the static NodeType below.</summary>
    public record StampedProduct
    {
        /// <summary>Product name.</summary>
        public string? Name { get; init; }
    }

    /// <summary>
    /// 🚨 A FRESH mesh per [Fact], deliberately — do not "optimise" this to true. The causality
    /// assertion below reads the registry BEFORE any activation and requires it to be empty for
    /// this NodeType. Both facts in this class activate that NodeType, and xUnit does not guarantee
    /// method order, so a shared mesh would make the assertion pass or fail depending on which ran
    /// first — the flake would look like a defect in the wiring rather than in the fixture.
    /// </summary>
    protected override bool ShareMeshAcrossTests => false;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(StampedNodeType)
            {
                Name = "Stamped Product",
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<StampedProduct>())
            });

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Activating an instance of the NodeType must leave the registry able to answer for that
    /// NodeType PATH. Before the activation it must NOT — otherwise the assertion would pass on a
    /// registration that happened for some unrelated reason and prove nothing about the stamp.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ActivatingAnInstance_RegistersItsContentTypeUnderTheNodeTypePath()
    {
        var registry = Mesh.ServiceProvider.GetRequiredService<IMeshContentTypeRegistry>();

        // CAUSALITY: nothing has activated this NodeType yet, so the key must be absent. Without
        // this the test could not distinguish "the stamp wrote the key" from "the key was already
        // there".
        registry.TryResolveByNodeType(StampedNodeType, out _).Should().BeFalse(
            "no instance of the NodeType has activated yet — nothing should have registered its "
            + "content type under that path");

        var id = $"stamped-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "Widget",
            NodeType = StampedNodeType,
            Content = new StampedProduct { Name = "Widget" },
        }).Should().Emit();

        // Activate the per-node hub — this is what applies the NodeType's HubConfiguration, and
        // therefore what runs WithContentType with the stamped path in scope.
        await Mesh.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
            .Should().Emit();

        // 🚨 THE ASSERTION: the NodeType PATH is a key in the mesh-wide registry. This is what was
        // never true before the wiring, and it is what makes the exact route usable at the read
        // seams (which resolve by node.NodeType).
        registry.TryResolveByNodeType(StampedNodeType, out var resolved).Should().BeTrue(
            "activating an instance must record its content type under the NodeType path — if this "
            + "fails, NodeTypePathHolder is not reaching MeshDataSource.WithContentType and the "
            + "exact route is dead code exactly as it was before");
        resolved.Should().Be(typeof(StampedProduct),
            "the registered type must be the NodeType's declared content type");
    }

    /// <summary>
    /// A cross-hub read yields typed content, not a blank record — the shape behind the production
    /// symptom, where a layout area doing <c>node.Content as T ?? new T()</c> renders BLANK whenever
    /// the read seam hands back an untyped JsonElement.
    ///
    /// <para>🚨 <b>This is a regression GUARD, not evidence for the wiring.</b> It was verified to
    /// pass both WITH and WITHOUT the NodeType-path registration, because <c>StampedProduct</c> is a
    /// statically-compiled type the reading hub can resolve on its own — the exact route is never
    /// reached. Reproducing the real failure needs a COLLECTIBLE content type (a runtime-compiled
    /// NodeType), which this fixture deliberately avoids. The test that discriminates is
    /// <see cref="ActivatingAnInstance_RegistersItsContentTypeUnderTheNodeTypePath"/>, which was
    /// confirmed red against the pre-wiring <c>Register(dataType)</c>.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ContentReadFromAForeignHub_IsTypedNotBlank()
    {
        var id = $"stamped-read-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "Gadget",
            NodeType = StampedNodeType,
            Content = new StampedProduct { Name = "Gadget" },
        }).Should().Emit();

        // The MESH workspace is not the node's owner — this read crosses the cache seam.
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .FirstAsync().Timeout(Timeout).ToTask();

        node.ContentAs<StampedProduct>(Mesh.JsonSerializerOptions)?.Name.Should().Be("Gadget",
            "a cross-hub read must yield the real content, not a blank record");
    }
}
