using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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
    public async Task SubscriberResubscribes_AfterOwnerDispose()
    {
        // CI agents can be slower than local dev: grain dispose + reactivate + heartbeat
        // resubscribe cycle needs breathing room. 45 s > the 10 s per-phase WaitFor timeouts
        // below combined, with headroom for slow runners.
        var ct = new CancellationTokenSource(45.Seconds()).Token;

        // Arrange — create a node with an initial name; activates the owner hub on first read.
        var path = $"{TestPartition}/resub-target";
        await NodeFactory.CreateNode(
            new MeshNode("resub-target", TestPartition) { Name = "Original", NodeType = "Markdown" });

        var client = GetClient(c => c.AddData());
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<MeshNode>(
            new Address(path), new MeshNodeReference());

        var snapshots = new List<string?>();
        using var sub = stream
            .Select(ci => ci.Value?.Name)
            .Where(n => n != null)
            .Subscribe(n => snapshots.Add(n));

        // Wait for the initial snapshot — proves the subscription is wired up.
        await WaitFor(() => snapshots.Count >= 1, 15.Seconds(), ct);
        snapshots[0].Should().Be("Original", "subscriber should receive the initial snapshot");

        // Act — kill the owner grain, then update the node. The update flows through
        // the freshly-reactivated owner; the OLD subscriber is silent until its
        // heartbeat fails and resubscribes.
        client.Post(new DisposeRequest(), o => o.WithTarget(new Address(path)));
        await Task.Delay(50, ct); // let dispose settle

        var current = await ReadNodeAsync(path, ct);
        current.Should().NotBeNull();
        await NodeFactory.UpdateNode(current! with { Name = "Updated" });

        // Assert — within a few heartbeat cycles, the subscriber must see the new value.
        // Without auto-resubscribe, snapshots stays at ["Original"] forever; with it,
        // a fresh Initial arrives carrying the updated name.
        await WaitFor(() => snapshots.Contains("Updated"), 20.Seconds(), ct);
        snapshots.Should().Contain("Updated",
            "subscriber must auto-resubscribe to the new owner grain and pick up post-dispose updates");
    }

    private static async Task WaitFor(Func<bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Predicate did not become true within {timeout}.");
            await Task.Delay(50, ct);
        }
    }
}
