#pragma warning disable CS1591

using System;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit coverage for the new <see cref="PatchDataRequest"/> + handler. This is the
/// user-facing partial-update primitive: a caller posts a JSON merge patch against
/// a <see cref="WorkspaceReference"/> on some target hub; the handler applies the
/// merge to the stream's current value and commits via <c>stream.Update</c> â€” no
/// pre-existing subscription required, no client-side read needed.
///
/// Covers: applies a partial patch, leaves omitted fields intact, post-patch
/// GetDataRequest sees the new state (round-trip consistency).
/// </summary>
public class PatchDataRequestTest : MonolithMeshTestBase
{
    private const string TestNodeType = nameof(TestProduct);
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData-patchreq");

    public PatchDataRequestTest(ITestOutputHelper output) : base(output) { }

    // Share Mesh/SP across [Fact]s.
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            .AddMeshNodes(new MeshNode(TestNodeType)
            {
                Name = "Test Product",
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<TestProduct>())
                    .AddDefaultLayoutAreas()
            });

    [Fact]
    public async Task PatchDataRequest_MergesPartialFields_LeavesOmittedIntact()
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var id = $"pdr-{Guid.NewGuid():N}";
        await mesh.CreateNode(new MeshNode(id, "ACME")
        {
            Name = "Original",
            NodeType = TestNodeType,
            Content = new TestProduct { Name = "Widget", Price = 1.00m, Quantity = 1 }
        }).Should().Emit();

        var path = $"ACME/{id}";

        // Post the PatchDataRequest with only { name: "Patched" } â€” the hub handler
        // applies this as a merge patch on its own MeshNode workspace stream.
        var patchJson = JsonSerializer.Serialize(new { name = "Patched" });
        var patchResp = (await Mesh.Observe(
            new PatchDataRequest(new MeshNodeReference(), new RawJson(patchJson)),
            o => o.WithTarget(new Address(path)))
            .Should().Emit()).Message;
        patchResp.Success.Should().BeTrue(patchResp.Error ?? "no error provided");

        // Round-trip: GetDataRequest on MeshNodeReference must see the merged state.
        var getResponse = await Mesh.Observe(
            new GetDataRequest(new MeshNodeReference()),
            o => o.WithTarget(new Address(path)))
            .Should().Emit();
        var node = getResponse.Message.Data as MeshNode;
        node.Should().NotBeNull();
        node!.Name.Should().Be("Patched",
            because: "PatchDataRequest merged only the 'name' field");
        node.NodeType.Should().Be(TestNodeType,
            because: "NodeType was not in the patch â€” must be preserved");
        node.Content.Should().NotBeNull(
            because: "Content was not in the patch â€” must be preserved");
    }

    /// <summary>
    /// End-to-end repro of the reordered/stale cross-hub patch flap. After an intervening write advances
    /// the node, a patch carrying the ORIGINAL (now stale) base must THREE-WAY merge at the owner:
    /// a disjoint string edit merges with the intervening one (no clobber), and a conflicting scalar is
    /// REFUSED (kept at the newer live value — no flap). This is the wedge behind the FutuRe overview
    /// never settling.
    /// </summary>
    [Fact]
    public async Task PatchDataRequest_StaleBase_MergesStringAndRefusesScalar()
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var id = $"pdr-stale-{Guid.NewGuid():N}";
        await mesh.CreateNode(new MeshNode(id, "ACME")
        {
            Name = "hello world",
            NodeType = TestNodeType,
            Content = new TestProduct { Name = "Widget", Price = 1.00m, Quantity = 1 }
        }).Should().Emit();
        var path = $"ACME/{id}";

        // Intervening write (no base ⇒ applies last-write-wins): uppercase word 1, Quantity 1 → 5.
        (await Mesh.Observe(
            new PatchDataRequest(new MeshNodeReference(),
                new RawJson("""{"name":"HELLO world","content":{"quantity":5}}""")),
            o => o.WithTarget(new Address(path))).Should().Emit())
            .Message.Success.Should().BeTrue();

        // Reordered/stale patch: computed against the ORIGINAL base ("hello world", quantity 1).
        // It uppercases word 2 and tries to set Quantity 9.
        (await Mesh.Observe(
            new PatchDataRequest(new MeshNodeReference(),
                new RawJson("""{"name":"hello WORLD","content":{"quantity":9}}"""))
            {
                BaseValues = new RawJson("""{"name":"hello world","content":{"quantity":1}}""")
            },
            o => o.WithTarget(new Address(path))).Should().Emit())
            .Message.Success.Should().BeTrue();

        var node = (await Mesh.Observe(new GetDataRequest(new MeshNodeReference()),
            o => o.WithTarget(new Address(path))).Should().Emit()).Message.Data as MeshNode;
        node.Should().NotBeNull();
        node!.Name.Should().Be("HELLO WORLD",
            because: "disjoint string edits (word1 live, word2 stale-patch) must MERGE, not clobber");
        (node.Content as TestProduct)!.Quantity.Should().Be(5,
            because: "the scalar Quantity changed since the stale patch's base → REFUSE, keep newer live (5), never flap to 9");
    }
}
