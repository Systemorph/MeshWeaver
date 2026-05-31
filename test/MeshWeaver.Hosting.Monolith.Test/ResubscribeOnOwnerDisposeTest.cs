using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Verifies the subscriber-side auto-resubscribe path: when a remote owner hub is
/// disposed (e.g. via Recycle), the subscriber's heartbeat fails on the next interval,
/// triggering a fresh SubscribeRequest. The new owner grain activates, reads the
/// up-to-date persistence, and replays an Initial snapshot — without requiring the
/// Blazor circuit to refresh.
/// </summary>
public class ResubscribeOnOwnerDisposeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private static readonly TimeSpan ShortHeartbeat = TimeSpan.FromMilliseconds(150);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                // Compress the heartbeat so the resubscribe path fires within test budget.
                services.Configure<SyncStreamOptions>(o => o.HeartbeatInterval = ShortHeartbeat);
                return services;
            });

    [Fact(Timeout = 60000)]
    public void SubscriberResubscribes_AfterOwnerDispose()
    {
        // Arrange — create a node with an initial name; activates the owner hub on first read.
        var path = $"{TestPartition}/resub-target";
        NodeFactory.CreateNode(
            new MeshNode("resub-target", TestPartition) { Name = "Original", NodeType = "Markdown" })
            .Should().Within(30.Seconds()).Emit();

        var client = GetClient(c => c.AddData());
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<MeshNode>(
            new Address(path), new MeshNodeReference());

        var names = stream.Select(ci => ci.Value?.Name).Where(n => n != null);

        // Wait for the initial snapshot — proves the subscription is wired up.
        // The live stream IS the authoritative source here, so we read `current`
        // off it rather than re-activating the owner via ReadNode.
        var firstSnapshot = names.Should().Within(15.Seconds()).Match(n => n == "Original");
        firstSnapshot.Should().Be("Original", "subscriber should receive the initial snapshot");
        var current = stream.Should().Within(15.Seconds()).Match(ci => ci.Value is { Name: "Original" }).Value!;

        // Act — kill the owner grain, then update the node. The update flows through
        // the freshly-reactivated owner; the OLD subscriber is silent until its
        // heartbeat fails and resubscribes.
        client.Post(new DisposeRequest(), o => o.WithTarget(new Address(path)));

        // The write races the in-flight dispose: an UpdateNode that lands while the
        // owner is mid-teardown is dropped / never completes. Rather than a fixed
        // settle delay, retry the write on a short cadence until the freshly
        // reactivated owner accepts it — the reactive "wait for owner ready" shape.
        Observable.Interval(TimeSpan.FromMilliseconds(250)).StartWith(0L)
            .SelectMany(_ => NodeFactory.UpdateNode(current with { Name = "Updated" })
                .Select(n => (MeshNode?)n)
                .Catch((Exception _) => Observable.Return<MeshNode?>(null)))
            .Should().Within(30.Seconds()).Match(n => n is { Name: "Updated" });

        // Assert — within a few heartbeat cycles, the subscriber must see the new value.
        // Without auto-resubscribe, the stream stays at "Original" forever; with it,
        // a fresh Initial arrives carrying the updated name.
        names.Should().Within(20.Seconds()).Match(n => n == "Updated",
            "subscriber must auto-resubscribe to the new owner grain and pick up post-dispose updates");
    }
}
