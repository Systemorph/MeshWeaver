#pragma warning disable CS1591

using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Dedicated, MCP-free coverage for the contract that underpins read-your-writes:
/// <c>workspace.GetMeshNodeStream(path)</c> is an <see cref="IObservable{MeshNode}"/>
/// that MUST
///   (a) re-emit a new value whenever the owning node issues a change (a cross-hub
///       update lands on a live subscriber), and
///   (b) replay the latest value to a subscriber that attaches AFTER the change
///       (the cache keeps the value in a ReplaySubject).
///
/// A create stamps the node's version and propagates; an UPDATE must do the same —
/// the owning hub is the single version clock and must advance it on every change so
/// the emitted frame is monotonically newer and reaches every mirror. When that
/// breaks, the live subscription never sees the update and a late read returns the
/// stale pre-update value (the read-your-writes-after-update regression that
/// <see cref="McpReadYourWritesTest"/> catches through the MCP surface).
/// </summary>
public class MeshNodeStreamEmissionTest : MonolithMeshTestBase
{
    private const string TestNodeType = nameof(TestProduct);
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData-stream-emit");

    public MeshNodeStreamEmissionTest(ITestOutputHelper output) : base(output) { }

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

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task GetMeshNodeStream_ReEmitsUpdatedNode_AndReplaysToLateSubscriber()
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();
        var id = $"emit-{Guid.NewGuid():N}";
        var path = $"ACME/{id}";

        // 1. Create the node (a brand-new node — propagates because adds are version-stamped).
        await mesh.CreateNode(new MeshNode(id, "ACME")
            {
                Name = "Before",
                NodeType = TestNodeType,
                Content = new TestProduct { Name = "Widget", Price = 1m, Quantity = 1 }
            })
            .FirstAsync().Timeout(Timeout).ToTask();

        // 2. Open a LIVE subscription BEFORE the update and arm it for the updated value.
        //    FirstAsync().ToTask() subscribes immediately, so it is listening from now on.
        var reEmitted = workspace.GetMeshNodeStream(path)
            .Where(n => n is not null && n.Name == "After")
            .FirstAsync()
            .Timeout(Timeout)
            .ToTask();

        // Ensure the initial "Before" is visible (subscribe handshake settled) before writing.
        var before = await workspace.GetMeshNodeStream(path)
            .Where(n => n is not null && n.Name == "Before")
            .FirstAsync().Timeout(Timeout).ToTask();
        before.Name.Should().Be("Before");

        // 3. Cross-hub update via the canonical API — only the Name field changes.
        await workspace.GetMeshNodeStream(path)
            .Update(node => node with { Name = "After" })
            .FirstAsync().Timeout(Timeout).ToTask();

        // 4. (b) The pre-opened live subscription MUST observe the update.
        var live = await reEmitted;
        live.Name.Should().Be("After",
            because: "the mesh node stream must re-emit when the owning node issues a change");

        // 5. Read-after-write: a subscriber attaching AFTER the change must replay the
        //    latest value, not the stale pre-update snapshot.
        var late = await workspace.GetMeshNodeStream(path)
            .Where(n => n is not null)
            .FirstAsync().Timeout(Timeout).ToTask();
        late.Name.Should().Be("After",
            because: "the cache keeps the latest value in a ReplaySubject for late subscribers");
    }

    /// <summary>
    /// Same contract, but the write goes through <c>IMeshService.UpdateNode(fullNode)</c>
    /// (the path MCP Patch/Update use): fetch the node, modify a field, send the whole node
    /// back. This isolates whether the full-node write path (NodeUpdatePipeline) propagates
    /// the same way the minimal-diff <c>Update(node =&gt; node with {...})</c> does.
    /// </summary>
    [Fact]
    public async Task MeshService_UpdateNode_FullNode_ReEmits()
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();
        var id = $"emitsvc-{Guid.NewGuid():N}";
        var path = $"ACME/{id}";

        await mesh.CreateNode(new MeshNode(id, "ACME")
            {
                Name = "Before",
                NodeType = TestNodeType,
                Content = new TestProduct { Name = "Widget", Price = 1m, Quantity = 1 }
            })
            .FirstAsync().Timeout(Timeout).ToTask();

        var reEmitted = workspace.GetMeshNodeStream(path)
            .Where(n => n is not null && n.Name == "After")
            .FirstAsync().Timeout(Timeout).ToTask();

        // Fetch the live node, modify one field, send the WHOLE node back (MCP shape).
        var current = await workspace.GetMeshNodeStream(path)
            .Where(n => n is not null && n.Name == "Before")
            .FirstAsync().Timeout(Timeout).ToTask();

        await mesh.UpdateNode(current with { Name = "After" })
            .FirstAsync().Timeout(Timeout).ToTask();

        var live = await reEmitted;
        live.Name.Should().Be("After",
            because: "a full-node UpdateNode must propagate the change to subscribers too");

        var late = await workspace.GetMeshNodeStream(path)
            .Where(n => n is not null)
            .FirstAsync().Timeout(Timeout).ToTask();
        late.Name.Should().Be("After",
            because: "read-after-write must see the full-node update");
    }
}
