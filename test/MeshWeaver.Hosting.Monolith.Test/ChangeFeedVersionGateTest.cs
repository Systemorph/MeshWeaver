using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the VERSION-GATED change-feed resubscribe for <c>MeshNode</c> streams (the storm cure):
/// a subscriber that RECEIVES the owner's writes through its own subscription must NOT
/// resubscribe when the change feed announces those same writes — on prod one hot ApiToken node
/// (written per request, version 8939 in a day) made all 85 of its subscriber streams resubscribe
/// on every write, starving the hubs (atioz 2026-07-02). A subscriber that is BEHIND the
/// announced version (orphaned by a recycled owner grain — see
/// <see cref="ResubscribeOnOwnerDisposeTest"/> for the full dispose flow) must still refresh.
/// </summary>
public class ChangeFeedVersionGateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodeId = "version-gate-target";
    private static readonly TimeSpan LongHeartbeat = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

    // Instance (never static — no cross-test bleed). Counts SubscribeRequests at the node's hub.
    private int _subscribeCount;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.Configure<SyncStreamOptions>(o =>
                {
                    o.HeartbeatInterval = LongHeartbeat;
                    o.ChangeFeedResubscribeWindow = Window;
                    o.ChangeFeedStalenessGrace = Grace;
                });
                return services;
            })
            // Passive counter on the target node's hub: increments on every SubscribeRequest and
            // returns the delivery UNPROCESSED so the framework handler still runs (sync hub +
            // SubscribeAck + initial data) — same shape as ChangeFeedResubscribeCoalesceTest.
            .ConfigureDefaultNodeHub(config => config
                .WithHandler<SubscribeRequest>((hubOnNode, delivery) =>
                {
                    if (hubOnNode.Address.ToString()!.EndsWith(NodeId, StringComparison.Ordinal))
                        Interlocked.Increment(ref _subscribeCount);
                    return delivery;
                }));

    [Fact(Timeout = 60_000)]
    public async Task ReceivedOwnerWrites_DoNotResubscribe_ButStaleAnnouncementDoes()
    {
        var path = $"{TestPartition}/{NodeId}";
        await NodeFactory.CreateNode(
                new MeshNode(NodeId, TestPartition) { Name = "v0", NodeType = "Markdown" })
            .Should().Within(30.Seconds()).Emit();

        var workspace = Mesh.GetWorkspace();
        var stream = workspace.GetMeshNodeStream(path);
        await stream.Should().Within(15.Seconds()).Match(n => n is { Name: "v0" });

        // Rapid owner writes through the canonical pipeline: each publishes a change-feed event
        // AND delivers the update to this subscriber — the exact per-request-write storm shape.
        // (The write machinery itself posts SubscribeRequests, so the resubscribe measurement
        // below starts from a baseline taken AFTER the writes fully settle.)
        const int writes = 5;
        MeshNode? last = null;
        for (var i = 1; i <= writes; i++)
        {
            var name = $"v{i}";
            stream.Update(node => node with { Name = name })
                .Subscribe(_ => { }, _ => { });
            last = await stream.Should().Within(15.Seconds()).Match(n => n != null && n.Name == name);
        }

        // Let the writes' own feed pulses fully drain through the coalescing window + staleness
        // grace, then take the baseline. Sanctioned fixed wait: a settle for "everything the
        // writes triggered has resolved" has no single positive signal.
        await Task.Delay(Window + Grace + TimeSpan.FromMilliseconds(800), TestContext.Current.CancellationToken);
        var baseline = Volatile.Read(ref _subscribeCount);
        var receivedVersion = last!.Version;

        // CAUGHT-UP announcement — the healthy-subscriber shape: the feed announces a version
        // this stream has already received through its own subscription. The gate must skip.
        var feed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        feed.Publish(new MeshChangeEvent(
            Namespace: TestPartition,
            Id: NodeId,
            Path: path,
            Kind: MeshChangeKind.Updated,
            NodeType: "Markdown",
            Version: receivedVersion,
            Timestamp: DateTimeOffset.UtcNow));

        // Sanctioned fixed wait: negative "no resubscribe happened" check.
        await Task.Delay(Window + Grace + TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken);
        (Volatile.Read(ref _subscribeCount) - baseline).Should().Be(0,
            "a subscriber that already RECEIVED the announced version is up to date — " +
            "resubscribing on it is the storm this gate exists to kill");

        // STALE announcement — the orphaned-subscriber shape: a version this stream can never
        // catch up to (the write happened on a hub whose emissions don't reach us). The gate
        // must fire a fresh-snapshot resubscribe. This also proves the pulse plumbing end to
        // end — a broken feed subscription would fail HERE, not silently pass above.
        feed.Publish(new MeshChangeEvent(
            Namespace: TestPartition,
            Id: NodeId,
            Path: path,
            Kind: MeshChangeKind.Updated,
            NodeType: "Markdown",
            Version: 999_999,
            Timestamp: DateTimeOffset.UtcNow));

        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Volatile.Read(ref _subscribeCount) - baseline)
            .Should().Within(15.Seconds())
            .Match(r => r >= 1,
                "an announced version the stream never received must trigger a fresh-snapshot resubscribe");
    }
}
