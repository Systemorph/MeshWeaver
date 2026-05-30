using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Verifies <c>Workspace._remoteStreamCache</c> evicts entries when their owner node
/// changes (delete / update / recreate). Without this, the singleton workspace serves
/// the same cached stream across Blazor circuit refreshes — so even an F5 keeps
/// showing the old data.
///
/// The two cached-stream paths under test:
///   1. Existing subscribers keep getting live DataChanged events for in-place updates.
///   2. NEW subscribers — i.e. anyone who calls <c>GetRemoteStream</c> after the change
///      — must NOT receive the cached pre-change snapshot. This test asserts (2).
/// </summary>
public class WorkspaceCacheEvictionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    [Fact(Timeout = 30000)]
    public async Task NewSubscriber_AfterUpdate_GetsFreshSnapshot()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;

        var path = $"{TestPartition}/cache-evict";
        await NodeFactory.CreateNode(
            new MeshNode("cache-evict", TestPartition) { Name = "Original", NodeType = "Markdown" });

        // First subscription warms up the singleton workspace's _remoteStreamCache.
        var client1 = GetClient(c => c.AddData());
        var workspace1 = client1.GetWorkspace();
        var stream1 = workspace1.GetRemoteStream<MeshNode>(
            new Address(path), new MeshNodeReference());

        var first = await stream1
            .Select(ci => ci.Value?.Name)
            .Where(n => n != null)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);
        first.Should().Be("Original");

        // Subscribe to the change feed BEFORE the update so we never race the
        // event. The Workspace's own subscription to the feed evicts the cache
        // entry; once we see the Updated event on the feed, the eviction has
        // happened by the time .OnNext returns (handlers run synchronously).
        var feed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var updateObserved = new TaskCompletionSource();
        using var feedSub = feed.Subscribe(ev =>
        {
            if (ev.Path == path && ev.Kind == MeshChangeKind.Updated)
                updateObserved.TrySetResult();
        });

        // Update the node — handler publishes MeshChangeEvent.Updated to IMeshChangeFeed.
        var current = await ReadNodeAsync(path, ct);
        current.Should().NotBeNull();
        await NodeFactory.UpdateNode(current! with { Name = "Updated" });

        // Stream-wait for the eviction to have happened — replaces a fixed
        // Task.Delay(150). The feed handler runs synchronously off Publish,
        // so by the time our TCS resolves, Workspace's subscriber has also
        // run and evicted the cache.
        await updateObserved.Task.WaitAsync(5.Seconds(), ct);

        // A SECOND, completely fresh subscription must observe "Updated" as its first
        // emission. If the cache wasn't evicted, GetRemoteStream returns the previously
        // cached stream and replays "Original" first, which would fail the assertion.
        var client2 = GetClient(c => c.AddData());
        var workspace2 = client2.GetWorkspace();
        var stream2 = workspace2.GetRemoteStream<MeshNode>(
            new Address(path), new MeshNodeReference());

        var freshFirst = await stream2
            .Select(ci => ci.Value?.Name)
            .Where(n => n != null)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);
        freshFirst.Should().Be("Updated",
            "a fresh subscriber after an update must see the post-update name, not a cached snapshot");
    }

    [Fact(Timeout = 30000)]
    public async Task NewSubscriber_AfterRecreate_GetsFreshSnapshot()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;

        var path = $"{TestPartition}/cache-recreate";
        await NodeFactory.CreateNode(
            new MeshNode("cache-recreate", TestPartition) { Name = "First", NodeType = "Markdown" });

        // Warm cache with a subscription.
        var client1 = GetClient(c => c.AddData());
        var stream1 = client1.GetWorkspace().GetRemoteStream<MeshNode>(
            new Address(path), new MeshNodeReference());
        var first = await stream1
            .Select(ci => ci.Value?.Name)
            .Where(n => n != null)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);
        first.Should().Be("First");

        // Subscribe to the change feed BEFORE delete/recreate. The workspace
        // subscriber to the feed runs first (registered at startup) so by the
        // time our TCS resolves for the Created event, the cache eviction is
        // already done.
        var feed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var deleteObserved = new TaskCompletionSource();
        var createObserved = new TaskCompletionSource();
        using var feedSub = feed.Subscribe(ev =>
        {
            if (ev.Path != path) return;
            if (ev.Kind == MeshChangeKind.Deleted) deleteObserved.TrySetResult();
            if (ev.Kind == MeshChangeKind.Created) createObserved.TrySetResult();
        });

        // Delete + recreate — emits Deleted then Created on the change feed.
        await NodeFactory.DeleteNode(path);
        // Stream-wait for the Deleted event to have fanned out (workspace's
        // cache evicted) — replaces a fixed Task.Delay(50).
        await deleteObserved.Task.WaitAsync(5.Seconds(), ct);

        await NodeFactory.CreateNode(
            new MeshNode("cache-recreate", TestPartition) { Name = "Second", NodeType = "Markdown" });
        // Stream-wait for the Created event — replaces a fixed Task.Delay(150).
        await createObserved.Task.WaitAsync(5.Seconds(), ct);

        var client2 = GetClient(c => c.AddData());
        var stream2 = client2.GetWorkspace().GetRemoteStream<MeshNode>(
            new Address(path), new MeshNodeReference());

        var freshFirst = await stream2
            .Select(ci => ci.Value?.Name)
            .Where(n => n != null)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);
        freshFirst.Should().Be("Second",
            "a fresh subscriber after delete+recreate must see the new node, not the original cached one");
    }
}
