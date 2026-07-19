using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the fix for the 2026-07-19 memex-cloud wedge: a recycled node must get a
/// COMPLETELY FRESH resolution attempt — cleared storm-breaker window, cleared
/// negative cache, cleared failure counters, and no replay of a stale terminal
/// error from a faulted read entry.
///
/// <para><b>The live defect.</b> The per-node hub <c>AgenticEngineering/Install</c>
/// (NodeType <c>Edu/CourseInvite</c>) went through a ~6-minute compile-error era in
/// which every grain activation faulted. Each failure grew the
/// <see cref="MeshNodeStreamCache"/> storm-breaker's exponential backoff
/// (2s&#160;·&#160;2^(n-1), capped at 5&#160;min), and the shared read entry's
/// Replay(1) terminated with the activation error. After the NodeType compiled
/// green again, <c>recycle</c> (DisposeRequest + the "cache invalidation broadcast
/// via MeshChangeFeed", see <c>MeshOperations.RecycleCore</c>) was issued
/// repeatedly — but the cache never consumed that broadcast: the negative entry
/// kept fast-failing every read AND write for the full grown window, and the
/// faulted entry kept replaying the stale error. The hub stayed unreachable for
/// 20+ minutes until a full pod restart cleared the in-process state.</para>
///
/// <para><b>The contract under test.</b> A change-feed event for a path (recycle
/// broadcast, post-commit Created/Updated/Deleted) resets the cache's failure
/// state for exactly that path: the negative entry is dropped (window closed,
/// counters zeroed) and a FAULTED read entry is evicted so the next natural read
/// re-probes the owner — without waiting out the old backoff window. Healthy live
/// entries are untouched (a routine post-commit broadcast must never sever live
/// subscribers). The reset only ever clears state / evicts — it never
/// re-subscribes anything (the 2026-06-08 rule).</para>
/// </summary>
public class MeshNodeStreamCacheRecycleResetTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    private async Task<string> CreateNodeAsync(string prefix)
    {
        var path = $"{TestPartition}/{prefix}-{Guid.NewGuid():N}";
        var node = MeshNode.FromPath(path) with
        {
            Name = "Original",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };
        await NodeFactory.CreateNode(node).Should().Within(60.Seconds()).Emit();
        return path;
    }

    /// <summary>The exact broadcast <c>MeshOperations.RecycleCore</c> publishes for a
    /// recycled path (Updated kind, NodeType-path marker, version 0).</summary>
    private void PublishRecycleBroadcast(string path)
    {
        var segments = path.Split('/');
        Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>().Publish(new MeshChangeEvent(
            Namespace: segments.Length > 1 ? string.Join("/", segments[..^1]) : "",
            Id: segments.Length > 0 ? segments[^1] : path,
            Path: path,
            Kind: MeshChangeKind.Updated,
            NodeType: MeshNode.NodeTypePath,
            Version: 0,
            Timestamp: DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// The deterministic pin of the live repro. The node EXISTS (as
    /// <c>AgenticEngineering/Install</c> did); the compile-era failure history is
    /// seeded through the breaker's own recording seam so the backoff window sits
    /// at the 5-minute cap — impossible to outwait within the test budget, exactly
    /// like the production wedge. Reads AND writes must fast-fail while the window
    /// is open; after the recycle broadcast the very next read must deliver the
    /// node. Without the reset this test can only go green by waiting out the
    /// 5-minute window — i.e. it deterministically fails.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RecycleBroadcast_ResetsBreaker_SoHealthyNodeResolvesWithoutWaitingOutBackoff()
    {
        var cache = Cache;
        var path = await CreateNodeAsync("recycle");

        // The compile-era failure shape: activation faulted because the NodeType
        // couldn't compile. Classified as a missing-node failure (recorded by the
        // breaker), NOT transient — precondition-checked so a classifier change
        // can't silently hollow out this test.
        var activationError = new InvalidOperationException(
            $"Delivery to '{path}' failed: activation failed: Compilation failed for " +
            "'Edu/CourseInvite': CS0246: The type or namespace name 'Foo' could not be found");
        MeshNodeStreamCache.IsMissingNodeFailure(activationError).Should().BeTrue(
            "precondition: the seeded error must be the class the storm-breaker records");

        // ~6 minutes of consecutive activation failures: 9 recordings put the
        // window at 2s·2^8 = 512s ⇒ capped at StormMaxCooldown (5 min).
        for (var i = 0; i < 9; i++)
            cache.RecordNegative(path, activationError);

        // Breaker OPEN ⇒ reads fast-fail by replaying the cached error…
        var readFail = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Materialize()
            .Should().Within(5.Seconds()).Match(
                n => n.Kind == NotificationKind.OnError,
                "an open breaker window must fast-fail reads with the cached error");
        readFail.Exception!.Message.Should().Contain("activation failed",
            "the fast-fail must replay the recorded owner error");

        // …and writes fast-fail too (the write-side breaker on the same negative cache).
        var writeFail = await cache.Update(path, n => n with { Name = "MustNotLand" }, Mesh.JsonSerializerOptions)
            .Materialize()
            .Should().Within(5.Seconds()).Match(
                n => n.Kind == NotificationKind.OnError,
                "an open breaker window must fast-fail writes as well");
        writeFail.Exception!.Message.Should().Contain("activation failed");

        // The recycle: the SAME broadcast RecycleCore publishes.
        PublishRecycleBroadcast(path);

        // Fresh resolution attempt IMMEDIATELY — no waiting out the 5-minute
        // window. This is the exact step that never healed on memex-cloud.
        var node = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Where(n => n is not null)
            .Should().Within(30.Seconds()).Emit(
                "a recycled path must get a completely fresh resolution attempt — " +
                "cleared breaker window, cleared negative cache, cleared failure counters");
        node!.Path.Should().Be(path);

        // And writes work again through the same reset state.
        await Mesh.GetMeshNodeStream(path)
            .Update(n => n with { Name = "AfterRecycle" })
            .Should().Within(15.Seconds()).Emit(
                "the write-side breaker must be reset by the same broadcast");
    }

    /// <summary>
    /// The organic end-to-end variant: a REAL missing-node read opens the breaker
    /// and leaves a FAULTED read entry whose Replay(1) holds the terminal error.
    /// The failure history is then grown to the cap (so the window cannot elapse
    /// naturally within the test), and the node is brought into being by a real
    /// <c>CreateNode</c> — whose post-commit <see cref="MeshChangeKind.Created"/>
    /// publish IS the invalidation signal. The immediate next read must emit the
    /// node: this pins BOTH halves of the reset — the negative-cache clear AND the
    /// faulted-entry eviction (with the entry left in place, the read would replay
    /// the stale pre-create error even with the breaker cleared).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task CreateAfterFailureEra_ClearsBreakerAndFaultedEntry_ViaChangeFeed()
    {
        var cache = Cache;
        var path = $"{TestPartition}/breaker-organic-{Guid.NewGuid():N}";

        // 1) Organic failure: the read reaches the owner, resolution fails with a
        //    genuine missing-node error; the breaker records it and the shared
        //    entry's replay terminates with the error.
        var firstFail = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Where(n => n?.Content is not null)
            .Materialize()
            .Should().Within(15.Seconds()).Match(
                n => n.Kind == NotificationKind.OnError,
                "reading a nonexistent path must surface the owner's NotFound as OnError");
        MeshNodeStreamCache.IsMissingNodeFailure(firstFail.Exception!).Should().BeTrue(
            "precondition: the organic failure must be the class the breaker records");

        // 2) Grow the failure history to the cap — the window can no longer elapse
        //    within the test budget, so a green run can only come from the reset.
        for (var i = 0; i < 8; i++)
            cache.RecordNegative(path, firstFail.Exception!);

        // 3) While the window is open, a re-read fast-fails by replaying the cached
        //    error without re-probing the owner.
        await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Materialize()
            .Should().Within(5.Seconds()).Match(
                n => n.Kind == NotificationKind.OnError,
                "the open window must fast-fail the re-read");

        // 4) Bring the node into being through the REAL pipeline. The post-commit
        //    Created publish on IMeshChangeFeed is the invalidation broadcast.
        var node = MeshNode.FromPath(path) with
        {
            Name = "NowExists",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };
        await NodeFactory.CreateNode(node).Should().Within(60.Seconds()).Emit();

        // 5) Immediate read — no waiting out the old backoff window, no replay of
        //    the stale pre-create error from the faulted entry.
        var fresh = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Where(n => n is not null)
            .Should().Within(30.Seconds()).Emit(
                "after the Created broadcast the path must resolve fresh — the faulted " +
                "entry must have been evicted and the negative entry dropped");
        fresh!.Path.Should().Be(path);
        fresh.Name.Should().Be("NowExists");
    }
}
