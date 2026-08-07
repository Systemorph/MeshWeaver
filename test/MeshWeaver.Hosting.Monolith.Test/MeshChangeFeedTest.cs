using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for the MeshChangeFeed: events are published on create/delete,
/// filtered subscriptions work, and path resolver cache is invalidated correctly.
/// </summary>
public class MeshChangeFeedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph();

    private IMeshChangeFeed ChangeFeed => Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
    // The base class also exposes PathResolver; this accessor intentionally re-declares it
    // to resolve via the local Mesh hub rather than the base-class SP.
    private new IPathResolver PathResolver => Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
    private CancellationToken Ct => new CancellationTokenSource(10_000).Token;

    private async Task<MeshNode> CreateTestNode(string id, string? ns = null)
    {
        // Top-level fixtures (empty namespace) are partition roots; the PartitionWriteGuard
        // rejects a normal user creating a non-partition-owning type (Markdown) there. These
        // nodes only need to EXIST for the change-feed / path-resolver assertions, so seed
        // them under the System identity (the legitimate partition provisioner) which bypasses
        // the guard. SeedTopLevel routes through IMeshService.CreateNode — the same create
        // pipeline that publishes the MeshChangeFeed event and warms the path resolver.
        var node = new MeshNode(id, ns) { Name = $"Test {id}", NodeType = "Markdown" };
        return await SeedTopLevel(node);
    }

    private async Task DeleteTestNode(string path)
    {
        var response = await Mesh.Observe(new DeleteNodeRequest(path), o => o.WithTarget(Mesh.Address)).Should().Emit();
        response.Message.Error.Should().BeNullOrEmpty();
    }

    /// <summary>
    /// Waits until <paramref name="events"/> contains a match. 🚨 The change feed dispatches on
    /// its OWN serial loop, never on the publishing hub's thread (issue #899 — a synchronous
    /// fan-out deadlocked two concurrently-deleting hubs), so a subscriber's list is NOT
    /// guaranteed to be populated the instant the create/delete request returns. Wait on the
    /// condition, never read straight after the await.
    /// </summary>
    private static Task<MeshChangeEvent[]> Delivered(
        ConcurrentQueue<MeshChangeEvent> events,
        Func<MeshChangeEvent, bool> predicate,
        string because)
        => Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => events.ToArray())
            .Should().Within(TimeSpan.FromSeconds(15))
            .Match(snapshot => snapshot.Any(predicate), because);

    [Fact]
    public async Task CreateNode_PublishesCreatedEvent()
    {
        var events = new ConcurrentQueue<MeshChangeEvent>();
        using var sub = ChangeFeed.Subscribe(events.Enqueue);

        await CreateTestNode("feed-create-1");

        await Delivered(events, e => e.Kind == MeshChangeKind.Created && e.Id == "feed-create-1",
            "creating a node must publish a Created event");
    }

    [Fact]
    public async Task DeleteNode_PublishesDeletedEvent()
    {
        var created = await CreateTestNode("feed-del-1");

        var events = new ConcurrentQueue<MeshChangeEvent>();
        using var sub = ChangeFeed.Subscribe(events.Enqueue);

        await DeleteTestNode(created.Path);

        await Delivered(events, e => e.Kind == MeshChangeKind.Deleted && e.Path.Contains("feed-del-1"),
            "deleting a node must publish a Deleted event");
    }

    [Fact]
    public async Task FilteredSubscription_OnlyReceivesMatchingEvents()
    {
        var createEvents = new ConcurrentQueue<MeshChangeEvent>();
        var deleteEvents = new ConcurrentQueue<MeshChangeEvent>();
        using var createSub = ChangeFeed.Subscribe(createEvents.Enqueue, MeshChangeKind.Created);
        using var deleteSub = ChangeFeed.Subscribe(deleteEvents.Enqueue, MeshChangeKind.Deleted);

        var created = await CreateTestNode("feed-filter-1");
        await DeleteTestNode(created.Path);

        // Wait for BOTH kinds to have been delivered before asserting the filtering —
        // otherwise "only creates in createEvents" would pass vacuously on an empty queue.
        await Delivered(createEvents, e => e.Id == "feed-filter-1", "the Created event must arrive");
        await Delivered(deleteEvents, e => e.Path.Contains("feed-filter-1"), "the Deleted event must arrive");

        createEvents.Should().OnlyContain(e => e.Kind == MeshChangeKind.Created);
        deleteEvents.Should().OnlyContain(e => e.Kind == MeshChangeKind.Deleted);
    }

    /// <summary>
    /// Re-resolves <paramref name="path"/> until it satisfies <paramref name="predicate"/>. The
    /// resolution cache is invalidated by the change feed, which delivers on its own dispatch
    /// loop (#899) — and on a distributed deployment that invalidation has always been
    /// asynchronous (the Orleans cross-silo broadcast). Resolution after a write is therefore
    /// eventually consistent: wait for it rather than reading once.
    /// </summary>
    private Task<AddressResolution?> Resolves(
        string path, Func<AddressResolution?, bool> predicate, string because)
        => Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .SelectMany(_ => PathResolver.ResolvePath(path).Take(1))
            .Should().Within(TimeSpan.FromSeconds(15))
            .Match(predicate, because);

    [Fact]
    public async Task CreateNode_PathResolverFindsIt()
    {
        // Resolve before create Ã¢â‚¬â€ should not find it
        var before = await PathResolver.ResolvePath("feed-resolve-1").Should().Emit();

        await CreateTestNode("feed-resolve-1");

        // After create Ã¢â‚¬â€ cache was invalidated/pre-warmed by change event
        var after = await Resolves("feed-resolve-1",
            r => r is not null && r.Prefix.Contains("feed-resolve-1") && string.IsNullOrEmpty(r.Remainder),
            "a created node must become resolvable at its exact path");
        after.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteNode_PathResolverNoLongerFindsIt()
    {
        var created = await CreateTestNode("feed-gone-1");

        // Verify resolver finds it
        var exists = await PathResolver.ResolvePath(created.Path).Should().Emit();
        exists.Should().NotBeNull();

        await DeleteTestNode(created.Path);

        // After delete Ã¢â‚¬â€ cache evicted, resolver should not find it at that exact path
        await Resolves(created.Path,
            r => r == null || r.Prefix != created.Path,
            "deleted node should not resolve to its exact path");
    }

    [Fact]
    public async Task NestedCreate_EvictsParentPartialMatch()
    {
        // Create parent
        var parent = await CreateTestNode("nest-parent-1");

        // Resolve nested path Ã¢â‚¬â€ caches partial match (parent with remainder)
        var partial = await PathResolver.ResolvePath($"{parent.Path}/nest-child-1").Should().Emit();

        // Create child
        await CreateTestNode("nest-child-1", parent.Path);

        // Now nested path should resolve to child (stale cache evicted by Created event)
        await Resolves($"{parent.Path}/nest-child-1",
            r => r is not null && r.Prefix == $"{parent.Path}/nest-child-1" && string.IsNullOrEmpty(r.Remainder),
            "the stale parent partial match must be evicted once the child exists");
    }
}

