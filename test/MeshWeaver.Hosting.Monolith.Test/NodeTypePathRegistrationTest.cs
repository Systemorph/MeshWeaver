using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
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

    /// <summary>
    /// 🚨 A NodeType DEFINITION node must never claim its OWN path in the content-type registry.
    ///
    /// <para><b>The key belongs to the instances, not to the definition.</b> Every read seam
    /// resolves by <c>node.NodeType</c>, so the entry at path <c>P</c> answers the question "what
    /// CLR type is the content of a node whose NodeType is <c>P</c>?". For a definition node at
    /// <c>P</c> the answer that its own activation can supply is <see cref="NodeTypeDefinition"/> —
    /// the content type of the <c>NodeType</c> configuration it is running — and that is the answer
    /// to a DIFFERENT question. Instances of <c>P</c> carry the type <c>P</c> declares
    /// (<c>PluginContent</c>, <c>StampedProduct</c>, …), which the definition node's hub never even
    /// loads: it applies <see cref="NodeTypeNodeType"/>'s configuration, not the compiled one.</para>
    ///
    /// <para><b>Why the wrong entry is silent and not merely absent.</b>
    /// <c>MeshContentTypeRegistry.Register</c> is last-writer-wins per key, so a definition node
    /// activating AFTER an instance overwrites the instance's correct entry. The degrade seams
    /// (<c>MeshNodeStreamCache.ConvertContentJsonElementToTyped</c>,
    /// <c>MeshNodeTypeSource.ResolveJsonElementContent</c>) then hand
    /// <c>TryRecoverForNodeType(node.NodeType, …)</c> an instance's JSON and get
    /// <see cref="NodeTypeDefinition"/> back — System.Text.Json ignores the members it does not
    /// know and materialises defaults for the rest, so the recovery SUCCEEDS and the instance
    /// serves its type's definition as its own content, at its unchanged Version, on every read.
    /// That is Systemorph/MeshWeaver#1379: a paid Store package whose fulfilment read the
    /// declaration, found a <see cref="NodeTypeDefinition"/>, and reported "nothing to install".</para>
    ///
    /// <para>The ordering here is FIXED, not raced: the definition node activates second, which is
    /// the order that exposes the overwrite. In production the order is whichever hub cold-activates
    /// last, which is why the symptom was intermittent (~1 gate run in 20) rather than constant.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ActivatingANodeTypeDefinition_DoesNotClaimItsOwnPathForItsInstances()
    {
        var registry = Mesh.ServiceProvider.GetRequiredService<IMeshContentTypeRegistry>();

        var id = $"declared-{Guid.NewGuid():N}";
        var definitionPath = $"{TestPartition}/{id}";

        // A NodeType definition node exactly as a dynamic type is persisted: NodeType is the
        // literal "NodeType", Content is a NodeTypeDefinition. No sources / configuration, so no
        // compile is kicked off — this test is about the registration, not about Roslyn.
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "Declared Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition { Description = "a type whose instances are not definitions" },
        }).Should().Emit();

        // Activate the definition node's own hub — this applies NodeTypeNodeType's configuration
        // and therefore runs its WithContentType<NodeTypeDefinition>().
        await Mesh.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(definitionPath)))
            .Should().Emit();

        // THE ASSERTION: the definition's own path is the key its INSTANCES read. Nothing the
        // definition node's hub knows may be published under it.
        registry.TryResolveByNodeType(definitionPath, out var claimed).Should().BeFalse(
            $"the definition node's activation must not claim '{definitionPath}' — that key answers "
            + "'what is the content type of a node whose NodeType is this?', and the only answer this "
            + "hub can give is NodeTypeDefinition, which is the content type of the DEFINITION, not of "
            + $"its instances. Claimed: {claimed?.FullName ?? "(none)"}");

        // …and the entry that IS correct must still be written: a definition node read through a
        // degrade seam resolves by its own NodeType, the literal "NodeType".
        registry.TryResolveByNodeType(MeshNode.NodeTypePath, out var forDefinitions).Should().BeTrue(
            "a node whose NodeType is the literal 'NodeType' does carry NodeTypeDefinition content — "
            + "that entry is the correct one and must not be lost by fixing the wrong one");
        forDefinitions.Should().Be(typeof(NodeTypeDefinition));
    }
}
