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
/// Pins the TRANSIENT-FAULT BREAKER — the fix for the 2026-07-21 memex-cloud poisoned-activation
/// loop that one broken plugin hub turned into a whole-portal outage.
///
/// <para><b>The live defect.</b> The <c>AgenticEngineering</c> root hub's activation faulted
/// persistently (a poisoned init replayed the same cached <c>SubscribeRequest</c> timeout into
/// every fresh activation). That failure class is <see cref="MeshNodeStreamCache.IsTransientOwnerFailure"/>
/// — deliberately NEVER negative-cached, so the just-idle-collected grain keeps its instant
/// re-probe. But with the fault PERSISTENT, the clean-cache policy meant every read re-opened an
/// upstream <c>SubscribeRequest</c> ~3/second, forever: the silo leaked 4→22&#160;GiB in ~12
/// minutes, its action blocks starved, probes failed, and the pod died — taking every other
/// plugin's compiled assembly with it.</para>
///
/// <para><b>The contract under test.</b> The first <c>TransientGraceFailures</c> (3) consecutive
/// transient faults change NOTHING (the ordinary idle-page reactivation miss keeps its instant
/// re-probe). A streak beyond the grace opens an exponential-backoff window that fast-fails
/// reads (base 1&#160;s, cap 60&#160;s). The streak clears instantly on a successful resolution,
/// on a change-feed invalidation (recycle broadcast / post-commit write), and a streak whose
/// last fault is older than <c>TransientStreakExpiry</c> (5&#160;min) restarts from one. The
/// breaker only ever records state and evicts — it never re-subscribes on its own
/// (the 2026-06-08 rule).</para>
/// </summary>
public class MeshNodeStreamCacheTransientBreakerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
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

    /// <summary>The poisoned-activation failure shape as it reached the cache on memex-cloud:
    /// the hub-request timeout, classified transient (and therefore never negative-cached) —
    /// precondition-checked so a classifier change can't silently hollow out these tests.</summary>
    private static TimeoutException TransientFault(string path) => new(
        $"No response received in hub cache/mesh-node-cache within 00:01:00 for request " +
        $"SubscribeRequest (id=16Rmeava) → target {path}. The request may have been " +
        "undeliverable or the target hub was not found.");

    /// <summary>
    /// Within the grace, the breaker must be invisible: no window opens, reads resolve
    /// immediately. This IS the "navigating to a just-idle page" guarantee that made
    /// transient faults exempt from the storm-breaker in the first place.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GraceFaults_DoNotSuppressReads()
    {
        var cache = Cache;
        var path = await CreateNodeAsync("grace");
        var fault = TransientFault(path);
        MeshNodeStreamCache.IsTransientOwnerFailure(fault).Should().BeTrue(
            "precondition: the seeded error must be the transient class");
        MeshNodeStreamCache.IsMissingNodeFailure(fault).Should().BeFalse(
            "precondition: a transient fault must never look like a missing node");

        for (var i = 0; i < 3; i++)
            cache.RecordTransient(path, fault);

        var node = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Where(n => n is not null)
            .Should().Within(30.Seconds()).Emit(
                "up to the grace, transient faults must not delay the re-probe at all");
        node!.Path.Should().Be(path);
    }

    /// <summary>
    /// The poisoned-activation loop: a streak past the grace must fast-fail reads by
    /// replaying the recorded error WITHOUT opening an upstream SubscribeRequest. Ten
    /// recordings put the window at 1s·2^6 = 64s ⇒ capped at 60s — impossible to outwait
    /// within the test budget, exactly like the production loop's unbounded lifetime.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task StreakPastGrace_FastFailsReads()
    {
        var cache = Cache;
        var path = await CreateNodeAsync("streak");
        var fault = TransientFault(path);

        for (var i = 0; i < 10; i++)
            cache.RecordTransient(path, fault);

        var readFail = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Materialize()
            .Should().Within(5.Seconds()).Match(
                n => n.Kind == NotificationKind.OnError,
                "an open transient-breaker window must fast-fail reads with the recorded error");
        readFail.Exception!.Message.Should().Contain("No response received",
            "the fast-fail must replay the recorded owner fault");
    }

    /// <summary>
    /// A recycle broadcast — the operator's remedy for a poisoned activation — must clear
    /// the streak so the very next read re-probes fresh, without waiting out the window.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RecycleBroadcast_ClearsStreak_SoNextReadResolves()
    {
        var cache = Cache;
        var path = await CreateNodeAsync("recycle-streak");
        var fault = TransientFault(path);

        for (var i = 0; i < 10; i++)
            cache.RecordTransient(path, fault);

        await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Materialize()
            .Should().Within(5.Seconds()).Match(
                n => n.Kind == NotificationKind.OnError,
                "precondition: the window must be open before the recycle");

        PublishRecycleBroadcast(path);

        var node = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Where(n => n is not null)
            .Should().Within(30.Seconds()).Emit(
                "a recycle broadcast must clear the transient streak immediately — " +
                "the operator's recycle is the sanctioned way out of a poisoned activation");
        node!.Path.Should().Be(path);
    }

    /// <summary>
    /// A successful resolution is authoritative proof the fault healed: it must zero the
    /// streak, so a later blip starts a fresh grace instead of inheriting the old count.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task SuccessfulResolution_ClearsStreak()
    {
        var cache = Cache;
        var path = await CreateNodeAsync("heal");
        var fault = TransientFault(path);

        // Grace-level streak (no window) …
        for (var i = 0; i < 3; i++)
            cache.RecordTransient(path, fault);

        // … a successful read clears it (bookkeeping observer on the shared subject) …
        await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Where(n => n is not null)
            .Should().Within(30.Seconds()).Emit();

        // … so one MORE fault is fault #1 of a NEW streak (≤ grace ⇒ no window), not #4 of
        // the old one (which would have opened a window and fast-failed this read).
        cache.RecordTransient(path, fault);
        var node = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Where(n => n is not null)
            .Should().Within(30.Seconds()).Emit(
                "after a successful resolution the streak must restart from zero");
        node!.Path.Should().Be(path);
    }

    /// <summary>
    /// A streak whose last fault is older than the expiry is stale evidence: the next fault
    /// restarts counting from one. Uses the clock-injectable seam — no real waits.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task StaleStreak_RestartsCounting()
    {
        var cache = Cache;
        var path = await CreateNodeAsync("stale");
        var fault = TransientFault(path);

        // Five faults, all ten minutes ago — well past TransientStreakExpiry (5 min).
        var longAgo = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
        for (var i = 0; i < 5; i++)
            cache.RecordTransient(path, fault, longAgo);

        // Today's fault is #1 of a fresh streak (≤ grace ⇒ no window) — reads stay instant.
        cache.RecordTransient(path, fault, DateTimeOffset.UtcNow);
        var node = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Where(n => n is not null)
            .Should().Within(30.Seconds()).Emit(
                "a blip ten minutes ago is not evidence about now — the streak must restart");
        node!.Path.Should().Be(path);
    }
}
