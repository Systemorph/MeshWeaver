using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Idle release of <see cref="MeshNodeStreamCache"/> READ entries — the fix for the
/// unbounded read-stream leak where every path EVER read (GUI navigation, per-URL path
/// resolution, routing, NodeType activation, MCP get/search, synced-query grain warming)
/// kept a permanently-connected upstream sync stream whose 45s heartbeat ran for the
/// process lifetime (~1,650 live streams measured on a long-lived portal).
///
/// <para>Contract under test (mirrors the write cache's <c>_updateQueues</c> sliding
/// expiration):</para>
/// <list type="number">
///   <item>An entry with NO live subscriber and NO read/write hit for the idle window
///     is released: hydration subscription disposed, upstream sync stream disposed
///     (owner-side mirror unsubscribes, heartbeat dies), entry dropped.</item>
///   <item>An entry with a LIVE subscriber is NEVER released, regardless of age, and
///     keeps delivering live updates.</item>
///   <item>A read after release transparently re-opens the path and serves fresh
///     state — release is invisible to callers.</item>
///   <item>The release actually disposes the upstream sync-stream client (not merely
///     the Rx subscription) — asserted via <see cref="ReadStreamEviction.UpstreamReleased"/>.</item>
/// </list>
///
/// <para>All waits are condition-based on the cache's internal
/// <c>ReadStreamEvictions</c> seam — no <c>Task.Delay</c> polling. The test injects a
/// short idle window via <see cref="MeshNodeStreamCacheOptions"/>; production defaults
/// stay at 10 min / 1 min.</para>
/// </summary>
public class MeshNodeStreamCacheIdleReleaseTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Short windows so the sweep acts within the test budget. Wide margin between the
    // two: any read/write hit restarts the full window, so framework activity on the
    // path only delays (never breaks) the awaited eviction.
    private static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(200);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(s => s.AddSingleton(new MeshNodeStreamCacheOptions
            {
                ReadStreamIdleExpiration = IdleWindow,
                ReadStreamSweepInterval = SweepInterval,
            }));

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

    /// <summary>Awaits the idle release of <paramref name="path"/> — deterministic
    /// (eviction-event driven), never a sleep.</summary>
    private Task<ReadStreamEviction> AwaitIdleRelease(string path) =>
        Cache.ReadStreamEvictions
            .Where(e => e.Path == path && e.Reason == "idle")
            .FirstAsync()
            .Should().Within(30.Seconds())
            .Emit($"the read entry for {path} must be released after the idle window");

    [Fact(Timeout = 60_000)]
    public async Task IdleEntry_IsReleasedAfterIdleWindow()
    {
        var cache = Cache;
        var path = await CreateNodeAsync("idle");

        // Prime a read whose subscription ENDS after the first emission — the entry
        // then has zero live subscribers and its idle clock runs.
        var read = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Where(n => n is not null)
            .FirstAsync()
            .Should().Within(30.Seconds()).Emit();
        read.Path.Should().Be(path);
        cache.IsReadStreamLive(path).Should().BeTrue(
            "the read just opened the shared entry");

        await AwaitIdleRelease(path);

        cache.IsReadStreamLive(path).Should().BeFalse(
            "an idle-released entry must be gone from the read cache");
    }

    [Fact(Timeout = 60_000)]
    public async Task EntryWithLiveSubscriber_SurvivesIdleWindow_AndKeepsDeliveringUpdates()
    {
        var cache = Cache;
        var control = await CreateNodeAsync("control");
        var subscribed = await CreateNodeAsync("subscribed");

        // Long-lived subscriber on `subscribed`. TCS bridging is the sanctioned
        // test-side way to await emissions of a subscription that must STAY attached.
        var initial = new TaskCompletionSource<MeshNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renamed = new TaskCompletionSource<MeshNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var liveSub = cache.GetStream(subscribed, Mesh.JsonSerializerOptions)
            .Where(n => n is not null)
            .Subscribe(n =>
            {
                initial.TrySetResult(n);
                if (n.Name == "Renamed")
                    renamed.TrySetResult(n);
            });
        try
        {
            // The initial emission proves the subscription (and its refcount pin)
            // is fully registered BEFORE the idle clock starts anywhere.
            await initial.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Prime the control path with a completed read, then await ITS idle
            // release — at that point at least one full idle window has elapsed
            // with sweep passes running, while `subscribed` held a live subscriber.
            await cache.GetStream(control, Mesh.JsonSerializerOptions)
                .Where(n => n is not null)
                .FirstAsync()
                .Should().Within(30.Seconds()).Emit();
            await AwaitIdleRelease(control);

            cache.IsReadStreamLive(subscribed).Should().BeTrue(
                "an entry with a live subscriber must NEVER be idle-released");

            // The surviving stream must still be LIVE end-to-end: a cross-hub write
            // must reach the long-lived subscriber through the same shared entry.
            await Mesh.GetMeshNodeStream(subscribed)
                .Update(n => n with { Name = "Renamed" })
                .Should().Within(15.Seconds()).Emit();
            await renamed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch
        {
            liveSub.Dispose();
            throw;
        }

        // Releasing the last subscriber re-arms the idle clock — the entry must now
        // be released like any other idle path. Attach the eviction listener BEFORE
        // dropping the subscription so the event can never race the wait.
        var subscribedRelease = AwaitIdleRelease(subscribed);
        liveSub.Dispose();
        await subscribedRelease;
        cache.IsReadStreamLive(subscribed).Should().BeFalse(
            "after the last unsubscribe the entry must idle out");
    }

    [Fact(Timeout = 60_000)]
    public async Task ReadAfterRelease_TransparentlyReopensAndServesFreshState()
    {
        var cache = Cache;
        var path = await CreateNodeAsync("reopen");

        await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Where(n => n is not null)
            .FirstAsync()
            .Should().Within(30.Seconds()).Emit();
        await AwaitIdleRelease(path);
        cache.IsReadStreamLive(path).Should().BeFalse();

        // Mutate while no read entry exists — the write path re-creates the entry
        // through the same cache (writes and reads share one handle per path).
        await Mesh.GetMeshNodeStream(path)
            .Update(n => n with { Name = "AfterRelease" })
            .Should().Within(15.Seconds()).Emit();

        // A read AFTER the release must transparently re-open the upstream and see
        // the post-release state — never a stale pre-release Replay buffer, never an
        // error. This is the "invisible to callers" contract.
        var fresh = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Should().Within(30.Seconds())
            .Match(n => n is not null && n.Name == "AfterRelease",
                "a read after idle release must serve the current owner state");
        fresh!.Path.Should().Be(path);
        cache.IsReadStreamLive(path).Should().BeTrue(
            "the transparent re-open must have re-created the shared entry");
    }

    [Fact(Timeout = 60_000)]
    public async Task IdleRelease_DisposesUpstreamSyncStream_EveryGeneration()
    {
        var cache = Cache;
        var path = await CreateNodeAsync("upstream");

        // Generation 1: read, idle out — the release must have disposed the actual
        // upstream sync-stream client (the SubscribeRequest whose 45s heartbeat kept
        // the owner-side mirror alive), not merely the cache's Rx subscription.
        await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Where(n => n is not null)
            .FirstAsync()
            .Should().Within(30.Seconds()).Emit();
        var first = await AwaitIdleRelease(path);
        first.UpstreamReleased.Should().BeTrue(
            "the idle release must dispose the upstream sync stream — otherwise its heartbeat keeps running");

        // Generation 2: the transparent re-open builds a FRESH upstream; idling out
        // again must dispose that one too (no generation may leak its heartbeat).
        await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Where(n => n is not null)
            .FirstAsync()
            .Should().Within(30.Seconds()).Emit();
        var second = await AwaitIdleRelease(path);
        second.UpstreamReleased.Should().BeTrue(
            "every re-opened generation's upstream must be disposed on its own idle release");

        cache.IsReadStreamLive(path).Should().BeFalse();
    }
}
