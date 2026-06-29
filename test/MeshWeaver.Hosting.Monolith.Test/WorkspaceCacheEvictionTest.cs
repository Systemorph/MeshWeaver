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
        var path = $"{TestPartition}/cache-evict";
        await NodeFactory.CreateNode(
            new MeshNode("cache-evict", TestPartition) { Name = "Original", NodeType = "Markdown" }).Should().Emit();

        // First subscription warms up the singleton workspace's _remoteStreamCache.
        var client1 = GetClient(c => c.AddData());
        var workspace1 = client1.GetWorkspace();
        var stream1 = workspace1.GetMeshNodeStream(path);

        await stream1
            .Select(ci => ci?.Name)
            .Should().Match(n => n == "Original");

        // Subscribe to the change feed BEFORE the update so we never race the
        // event. The Workspace's own subscription to the feed evicts the cache
        // entry; once we see the Updated event on the feed, the eviction has
        // happened by the time .OnNext returns (handlers run synchronously).
        var feed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var updateObserved = new ReplaySubject<bool>();
        using var feedSub = feed.Subscribe(ev =>
        {
            if (ev.Path == path && ev.Kind == MeshChangeKind.Updated)
                updateObserved.OnNext(true);
        });

        // Update the node — handler publishes MeshChangeEvent.Updated to IMeshChangeFeed.
        var current = await ReadNode(path).Should().Match(n => n is not null);
        await NodeFactory.UpdateNode(current! with { Name = "Updated" }).Should().Emit();

        // Stream-wait for the eviction to have happened — replaces a fixed
        // Task.Delay(150). The feed handler runs synchronously off Publish,
        // so by the time the ReplaySubject emits, Workspace's subscriber has also
        // run and evicted the cache.
        await updateObserved.Should().Within(5.Seconds()).Emit();

        // A SECOND, completely fresh subscription must observe "Updated". Under the emit-onstart
        // contract a fresh mirror may replay a stale Initial ("Original") before converging, so we
        // assert eventual convergence — NOT a strict first emission. (That live-propagation +
        // convergence is independently proven by ExistingSubscriber_AfterCrossHubUpdate_GetsLiveValue
        // and NewClient_AfterCrossHubUpdate_EventuallyConverges; in real single-process prod a new
        // circuit shares the already-live cache handle and sees "Updated" immediately.)
        var client2 = GetClient(c => c.AddData());
        var workspace2 = client2.GetWorkspace();
        var stream2 = workspace2.GetMeshNodeStream(path);

        await stream2
            .Select(ci => ci?.Name)
            .Should().Within(15.Seconds())
            .Match(n => n == "Updated");
    }

    /// <summary>
    /// ISOLATION REPRO for claim (1): an EXISTING subscriber to GetMeshNodeStream(path) must
    /// receive the post-update value live. This isolates WRITE-PATH propagation (owner's cross-hub
    /// atomic apply fanning out to the mirror's sync stream) from the new-subscriber cache reuse
    /// that NewSubscriber_AfterUpdate tests. One live subscription is held across the update; if the
    /// owner's apply doesn't fan out, the handle stays frozen at "Original". No load → a non-emit is
    /// a propagation break, not lag.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ExistingSubscriber_AfterCrossHubUpdate_GetsLiveValue()
    {
        var path = $"{TestPartition}/cache-live";
        await NodeFactory.CreateNode(
            new MeshNode("cache-live", TestPartition) { Name = "Original", NodeType = "Markdown" }).Should().Emit();

        var workspace = GetClient(c => c.AddData()).GetWorkspace();

        // Hold ONE live subscription across the update — a genuine existing subscriber.
        var seen = new ReplaySubject<string?>();
        using var sub = workspace.GetMeshNodeStream(path).Select(ci => ci?.Name).Subscribe(seen);
        await seen.Should().Within(10.Seconds()).Match(n => n == "Original");

        var current = await ReadNode(path).Should().Match(n => n is not null);
        await NodeFactory.UpdateNode(current! with { Name = "Updated" }).Should().Emit();

        // The SAME live handle must observe the cross-hub update.
        await seen.Should().Within(10.Seconds()).Match(n => n == "Updated");
    }

    /// <summary>
    /// DIAGNOSTIC: a NEW separate client subscribing after a cross-hub update — does it EVENTUALLY
    /// converge to "Updated" (eventual-consistency: a stale Initial replay then the live value), or
    /// stay permanently frozen at "Original" (a real staleness bug)? NewSubscriber_AfterUpdate
    /// asserts the FIRST emission is fresh; this asserts only eventual convergence.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task NewClient_AfterCrossHubUpdate_EventuallyConverges()
    {
        var path = $"{TestPartition}/cache-converge";
        await NodeFactory.CreateNode(
            new MeshNode("cache-converge", TestPartition) { Name = "Original", NodeType = "Markdown" }).Should().Emit();

        var workspace1 = GetClient(c => c.AddData()).GetWorkspace();
        await workspace1.GetMeshNodeStream(path).Select(ci => ci?.Name).Should().Match(n => n == "Original");

        var current = await ReadNode(path).Should().Match(n => n is not null);
        await NodeFactory.UpdateNode(current! with { Name = "Updated" }).Should().Emit();

        // New, separate client. Wait for eventual "Updated" (not just the first emission).
        var workspace2 = GetClient(c => c.AddData()).GetWorkspace();
        await workspace2.GetMeshNodeStream(path).Select(ci => ci?.Name)
            .Should().Within(15.Seconds()).Match(n => n == "Updated");
    }

    /// <summary>
    /// ROOT-CAUSE REPRO for the intermittent <see cref="NewClient_AfterCrossHubUpdate_EventuallyConverges"/>
    /// (and siblings) line-133 read timeout. The intermediate <c>ReadNode</c> is
    /// <c>Mesh.GetMeshNode</c>, which posts a <see cref="GetDataRequest"/> and only THEN registers the
    /// response subject (<c>hub.Observe(delivery)</c>). The hub DROPS any response whose requestId has
    /// no registered subject yet ("No subject found for response … treating as processed",
    /// <c>MessageHub.HandleCallbacks</c>). When the owning per-node hub is WARM it answers in
    /// sub-millisecond time; if the posting thread is preempted between Post and Observe — routine
    /// under bulk thread-pool contention — the warm reply lands before the subject exists, is dropped,
    /// and the read hangs to its timeout. Here the preemption is made explicit (an awaited delay) so
    /// the race is DETERMINISTIC:
    ///   • register-AFTER-post (the old GetMeshNode shape) → reply dropped → the read never emits.
    ///   • register-BEFORE-post (<c>Observe&lt;TResponse&gt;</c>, the fix) → the AsyncSubject buffers the
    ///     reply and replays it to the late subscriber → the read emits.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task GetMeshNode_WarmOwner_DropsResponse_WhenSubjectRegisteredAfterPost()
    {
        var path = $"{TestPartition}/getnode-race";
        await NodeFactory.CreateNode(
            new MeshNode("getnode-race", TestPartition) { Name = "Warm", NodeType = "Markdown" }).Should().Emit();

        // WARM the owning per-node hub so a GetDataRequest is answered in sub-ms — the precondition
        // under which the post-then-observe window can lose the reply.
        await Mesh.GetMeshNode(path).Should().Match(n => n is not null);

        var addr = new Address(path);

        // (1) OLD shape — register the response subject AFTER posting, with a deliberate preemption
        //     between Post and Observe. The warm owner replies during the delay; the hub has no
        //     subject for that requestId yet, so it drops the reply. Observing afterwards never emits.
        var droppedDelivery = Mesh.Post(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(addr));
        droppedDelivery.Should().NotBeNull();
        await Task.Delay(300); // force the warm round-trip to complete before we register the subject
        await Mesh.Observe(droppedDelivery!).Select(d => (object?)d.Message)
            .Should().NotEmit(within: 2.Seconds());

        // (2) FIX shape — Observe<TResponse> registers the subject BEFORE posting, so the same
        //     preemption is harmless: the reply is buffered in the AsyncSubject and replayed to the
        //     late subscriber.
        var safe = Mesh.Observe<GetDataResponse>(
            new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(addr));
        await Task.Delay(300); // same preemption — but the subject was registered before the post
        await safe.Select(d => d.Message.Data as MeshNode)
            .Should().Within(5.Seconds()).Match(n => n is not null);
    }

    [Fact(Timeout = 30000)]
    public async Task NewSubscriber_AfterRecreate_GetsFreshSnapshot()
    {
        var path = $"{TestPartition}/cache-recreate";
        await NodeFactory.CreateNode(
            new MeshNode("cache-recreate", TestPartition) { Name = "First", NodeType = "Markdown" }).Should().Emit();

        // Warm cache with a subscription.
        var client1 = GetClient(c => c.AddData());
        var stream1 = client1.GetWorkspace().GetMeshNodeStream(path);
        await stream1
            .Select(ci => ci?.Name)
            .Should().Match(n => n == "First");

        // Subscribe to the change feed BEFORE delete/recreate. The workspace
        // subscriber to the feed runs first (registered at startup) so by the
        // time the ReplaySubject emits the Created event, the cache eviction is
        // already done.
        var feed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var deleteObserved = new ReplaySubject<bool>();
        var createObserved = new ReplaySubject<bool>();
        using var feedSub = feed.Subscribe(ev =>
        {
            if (ev.Path != path) return;
            if (ev.Kind == MeshChangeKind.Deleted) deleteObserved.OnNext(true);
            if (ev.Kind == MeshChangeKind.Created) createObserved.OnNext(true);
        });

        // Delete + recreate — emits Deleted then Created on the change feed.
        await NodeFactory.DeleteNode(path).Should().Emit();
        // Stream-wait for the Deleted event to have fanned out (workspace's
        // cache evicted) — replaces a fixed Task.Delay(50).
        await deleteObserved.Should().Within(5.Seconds()).Emit();

        await NodeFactory.CreateNode(
            new MeshNode("cache-recreate", TestPartition) { Name = "Second", NodeType = "Markdown" }).Should().Emit();
        // Stream-wait for the Created event — replaces a fixed Task.Delay(150).
        await createObserved.Should().Within(5.Seconds()).Emit();

        var client2 = GetClient(c => c.AddData());
        var stream2 = client2.GetWorkspace().GetMeshNodeStream(path);

        var freshFirst = await stream2
            .Select(ci => ci?.Name)
            .Where(n => n != null)
            .Should().Emit();
        freshFirst.Should().Be("Second",
            "a fresh subscriber after delete+recreate must see the new node, not the original cached one");
    }
}
