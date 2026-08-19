using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The production wiring of the compilation-overlay re-evaluation
/// (<see cref="NodeTypeEnrichmentHelpers.AuthoritativeTypeRead"/>) — issue #1814 defect B.
///
/// <para>🚨 Why this test exists separately from
/// <c>OverlaySelfHealWatcherTest</c>: that one hands the watcher a re-read lambda and proves the
/// watcher uses it. Every one of its assertions would still pass if the REAL re-read were wired to
/// something that can never answer — which is exactly the failure being fixed, one layer down. So
/// this pins the seam itself: it goes to the mesh's QUERY PROVIDERS (storage), under the System
/// identity, and it answers about the requested path and no other.</para>
/// </summary>
public class OverlayReEvaluationReadTest
{
    private const string NodeTypePath = "Store/Plugin";

    private static MeshNode TypeNode(string id, string ns) =>
        new(id, ns) { NodeType = MeshNode.NodeTypePath, Version = 3259 };

    private static IMessageHub HubWith(params object[] services)
    {
        var provider = new ServiceCollection();
        foreach (var service in services)
        {
            if (service is IMeshQueryCore core)
                provider.AddSingleton(core);
        }
        var hub = Substitute.For<IMessageHub>();
        hub.ServiceProvider.Returns(provider.BuildServiceProvider());
        hub.JsonSerializerOptions.Returns(new JsonSerializerOptions());
        return hub;
    }

    private static IMeshQueryCore CoreReturning(
        params MeshNode[] items)
    {
        var core = Substitute.For<IMeshQueryCore>();
        core.Query<MeshNode>(Arg.Any<MeshQueryRequest>(), Arg.Any<JsonSerializerOptions>())
            .Returns(Observable.Return(new QueryResultChange<MeshNode>
            {
                ChangeType = QueryChangeType.Initial,
                Items = items,
            }));
        return core;
    }

    /// <summary>
    /// The read resolves through <c>IMeshQueryCore</c> — the mesh's query-provider fan-out over
    /// storage — and asks for the NodeType's path as System. Not through
    /// <c>GetWorkspace().GetMeshNodeStream(...)</c>, whose cached snapshot is the thing that went
    /// permanently stale.
    /// </summary>
    [Fact]
    public void ReadsThroughTheQueryCore_AsSystem_ForTheRequestedPath()
    {
        var requests = new List<MeshQueryRequest>();
        var core = Substitute.For<IMeshQueryCore>();
        core.Query<MeshNode>(Arg.Do<MeshQueryRequest>(requests.Add), Arg.Any<JsonSerializerOptions>())
            .Returns(Observable.Return(new QueryResultChange<MeshNode>
            {
                ChangeType = QueryChangeType.Initial,
                Items = [TypeNode("Plugin", "Store")],
            }));

        var read = NodeTypeEnrichmentHelpers.AuthoritativeTypeRead(HubWith(core), NodeTypePath);
        read.Should().NotBeNull();

        var node = read!().Wait();

        node.Should().NotBeNull();
        node!.Path.Should().Be(NodeTypePath);
        requests.Should().HaveCount(1);
        requests[0].UserId.Should().Be(WellKnownUsers.System,
            "the re-evaluation is infrastructure — it runs under the System identity like every "
            + "other read on the enrichment path, not under whoever happened to open the page");
    }

    /// <summary>
    /// A ranked or fuzzy hit for a NEIGHBOURING node is not an answer about this type: acting on it
    /// would recycle an instance against a build it is not waiting for.
    /// </summary>
    [Fact]
    public void ANeighbouringHit_IsNotAnAnswer()
    {
        var core = CoreReturning(TypeNode("PluginContent", "Store"), TypeNode("Coupon", "Store"));

        var read = NodeTypeEnrichmentHelpers.AuthoritativeTypeRead(HubWith(core), NodeTypePath);
        read!().Wait().Should().BeNull();
    }

    /// <summary>
    /// Nothing at the path is "no answer", not "healed" — the watcher's ladder simply asks again.
    /// </summary>
    [Fact]
    public void AnEmptyResult_IsNull_NotAHealSignal()
    {
        var read = NodeTypeEnrichmentHelpers.AuthoritativeTypeRead(HubWith(CoreReturning()), NodeTypePath);
        read!().Wait().Should().BeNull();
    }

    /// <summary>
    /// A host with no query core (a unit-test hub) gets NO re-read rather than a fake one — the
    /// watcher then keeps its push-only behaviour instead of pretending to re-evaluate.
    /// </summary>
    [Fact]
    public void NoQueryCore_MeansNoReRead()
        => NodeTypeEnrichmentHelpers.AuthoritativeTypeRead(HubWith(), NodeTypePath)
            .Should().BeNull();
}
