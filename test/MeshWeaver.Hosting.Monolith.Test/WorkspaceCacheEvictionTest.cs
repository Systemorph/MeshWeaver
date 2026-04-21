using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
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
    [Fact(Timeout = 30000)]
    public async Task NewSubscriber_AfterUpdate_GetsFreshSnapshot()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;

        var path = $"{TestPartition}/cache-evict";
        await NodeFactory.CreateNodeAsync(
            new MeshNode("cache-evict", TestPartition) { Name = "Original", NodeType = "Markdown" },
            ct);

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

        // Update the node — handler publishes MeshChangeEvent.Updated to IMeshChangeFeed.
        // Workspace's subscription to the feed must evict the cache entry for this path.
        MeshNode? current = null;
        await foreach (var n in NodeFactory.QueryAsync<MeshNode>($"path:{path}", ct: ct).WithCancellation(ct))
        {
            current = n;
            break;
        }
        current.Should().NotBeNull();
        await NodeFactory.UpdateNodeAsync(current! with { Name = "Updated" }, ct);

        // Give the change-feed handler a moment to evict.
        await Task.Delay(150, ct);

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
        await NodeFactory.CreateNodeAsync(
            new MeshNode("cache-recreate", TestPartition) { Name = "First", NodeType = "Markdown" },
            ct);

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

        // Delete + recreate — emits Deleted then Created on the change feed. Either
        // event must clear the cache entry for the path.
        await NodeFactory.DeleteNodeAsync(path, ct);
        await Task.Delay(50, ct);
        await NodeFactory.CreateNodeAsync(
            new MeshNode("cache-recreate", TestPartition) { Name = "Second", NodeType = "Markdown" },
            ct);
        await Task.Delay(150, ct);

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
