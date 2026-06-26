using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the framework guarantee behind "a burst of owner change-feed events must produce
/// ONE fresh-snapshot resubscribe, not N".
///
/// <para>A remote sync subscription (<c>JsonSynchronizationStream.CreateExternalClient</c>)
/// attaches an <see cref="IMeshChangeFeed"/> listener so it can refresh its cached snapshot
/// after a genuine owner restart/recreate. Before the fix, that listener invoked
/// <c>Resubscribe</c> on EVERY owner-path change event. The in-flight guard only collapses
/// events that arrive WHILE a resubscribe is mid-flight; the moment the owner acks one
/// SubscribeRequest the guard clears, so a stream of owner writes that each settle (ack)
/// before the next produces one fresh <c>SubscribeRequest</c> PER event.</para>
///
/// <para>Each <c>SubscribeRequest</c> the owner receives synchronously creates a
/// <c>sync/{ClientId}</c> hub on the owner's single-threaded action block
/// (<c>SynchronizationStream</c> ctor → <c>Host.GetHostedHub</c>). High-frequency owner
/// writes (e.g. <c>{userId}/_UserActivity/{userId}</c>, written per HTTP request by
/// UserContextMiddleware) therefore drove a BURST of synchronous sync-hub creations, all
/// serialized on the shared cache/owner hub's one action block — which then could not ack
/// OTHER subscribers' SubscribeRequests within the callback timeout → wedge
/// ("owner hub not reachable").</para>
///
/// <para>One resubscribe already fetches the LATEST owner snapshot regardless of how many
/// writes fired, so per-event resubscribe is redundant AND harmful. The fix throttles the
/// change-feed-triggered resubscribe so a burst collapses to a single resubscribe; a genuine
/// owner restart still triggers one (just debounced).</para>
///
/// <para>This test uses a REAL host data source so the client's data-context init gate opens
/// (resubscribe acks then flow promptly and the in-flight guard clears between events,
/// exactly as in prod). The owner COUNTS every SubscribeRequest it receives. The client opens
/// one remote stream (initial subscribe = 1) and then drives a burst of owner-path change-feed
/// events. After the burst the owner must have seen at most ONE extra resubscribe (≈2 total),
/// not one-per-event (≈N+1). Without the fix the count climbs to N+1 — a deterministic RED.</para>
/// </summary>
public class ChangeFeedResubscribeCoalesceTest(ITestOutputHelper output) : HubTestBase(output)
{
    // Long heartbeat — we are NOT exercising the heartbeat-driven path here; only the
    // change feed. A 5-minute interval guarantees the heartbeat timer never fires during the
    // test, so every SubscribeRequest the owner counts is either the initial subscribe or a
    // change-feed-triggered resubscribe.
    private static readonly TimeSpan LongHeartbeat = TimeSpan.FromMinutes(5);

    // Coalescing window for the change-feed resubscribe. Shortened from the production
    // default so the test settles fast; comfortably LONGER than the whole burst below so a
    // correct (throttled) implementation collapses the burst into a single resubscribe.
    private static readonly TimeSpan ResubscribeWindow = TimeSpan.FromMilliseconds(600);

    // Number of change-feed events in the burst (mirrors several rapid owner writes).
    private const int BurstSize = 6;

    // Spacing between burst events. Comfortably ABOVE any plausible in-process
    // SubscribeRequest→SubscribeAck round trip (sub-ms to low-ms — see
    // NonExistentOwnerNoHeartbeatStormTest) so that, on the UN-throttled code, the in-flight
    // guard clears between events and each event fires its own resubscribe (the storm). The
    // whole burst (BurstSize * EventSpacing = 6 * 60ms = 360ms) stays UNDER ResubscribeWindow
    // (600ms), so the throttled code collapses the entire burst into a single resubscribe.
    private static readonly TimeSpan EventSpacing = TimeSpan.FromMilliseconds(60);

    // Instance (never static — no cross-test bleed). Counts SubscribeRequests at the owner.
    private int _subscribeCount;

    /// <summary>
    /// Minimal in-process <see cref="IMeshChangeFeed"/> for the test — a plain Rx Subject,
    /// matching the production InProcessMeshChangeFeed contract. Lives here (not pulled from
    /// MeshWeaver.Hosting) to keep the test project light; the production code resolves the
    /// REAL MeshWeaver.Mesh.Contract interface by name, so this concrete type registered
    /// against that interface is exactly what the change-feed listener attaches to.
    /// </summary>
    private sealed class TestMeshChangeFeed : IMeshChangeFeed, IDisposable
    {
        private readonly Subject<MeshChangeEvent> _subject = new();

        public void Publish(MeshChangeEvent change) => _subject.OnNext(change);

        public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
            => filter is null
                ? _subject.Subscribe(handler)
                : _subject.Subscribe(e => { if (e.Kind == filter.Value) handler(e); });

        public void Dispose() => _subject.Dispose();
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            // Passive counter: increments on every SubscribeRequest and returns the delivery
            // UNPROCESSED so the framework's AddData handler still runs (creates the sync hub
            // + sends SubscribeAck + initial data). Registered before AddData so it sees the
            // request first in the rule chain.
            .WithHandler<SubscribeRequest>((_, delivery) =>
            {
                Interlocked.Increment(ref _subscribeCount);
                return delivery; // not Processed() — let AddData's handler run too
            })
            // A REAL data source: the owner serves BusinessUnit/LineOfBusiness with initial
            // data, so subscribe → SubscribeAck + an initial DataChangedEvent. That opens the
            // client's data-context init gate, so the resubscribe SubscribeAck is delivered
            // promptly and the in-flight guard clears between change-feed events.
            .AddData(data => data.AddSource(src => src
                .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))
                .WithType<LineOfBusiness>(t => t.WithInitialData(TestData.LinesOfBusiness))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithServices(services =>
                // The change-feed listener resolves IMeshChangeFeed from the client hub's
                // service provider; the bare Data-layer HubTestBase does not register one, so
                // wire the in-process feed here to drive owner-path change events.
                services
                    .AddSingleton<IMeshChangeFeed, TestMeshChangeFeed>()
                    .Configure<SyncStreamOptions>(o =>
                    {
                        o.HeartbeatInterval = LongHeartbeat;
                        o.ChangeFeedResubscribeWindow = ResubscribeWindow;
                    }))
            .AddData(data => data.AddHubSource(CreateHostAddress(),
                ds => ds.WithType<BusinessUnit>().WithType<LineOfBusiness>()));

    [HubFact]
    public async Task BurstOfOwnerChangeFeedEvents_CoalescesToOneResubscribe()
    {
        var host = GetHost();      // activate the owner hub
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        var changeFeed = client.ServiceProvider.GetRequiredService<IMeshChangeFeed>();

        // Open the remote stream and wait for the FIRST real snapshot. This proves the
        // initial SubscribeRequest landed (count ≥ 1), the owner replied with data, and the
        // client's data-context init gate has OPENED — so subsequent resubscribe SubscribeAcks
        // are delivered promptly (no init-gate deferral that would falsely latch the guard).
        await workspace.GetObservable<BusinessUnit>()
            .Should().Within(10.Seconds())
            .Match(x => x.Count > 0, "the owner must serve the initial snapshot");

        var afterInitial = Volatile.Read(ref _subscribeCount);

        // Drive a BURST of owner-path change-feed events with a fixed spacing that is ABOVE
        // the in-process ack round-trip but well WITHIN the throttle window. owner.Path for
        // CreateHostAddress() is "host/1" (Address.Path = segments joined by '/'); the listener
        // matches the event Path against the owner's bare path (case-insensitive). On the
        // UN-throttled code, the in-flight guard clears between these settled events so each
        // fires its own resubscribe (the storm). On the throttled code the whole burst lands
        // within one window and collapses to a single resubscribe.
        const string ownerPath = HostType + "/1";
        for (var i = 0; i < BurstSize; i++)
        {
            changeFeed.Publish(new MeshChangeEvent(
                Namespace: HostType,
                Id: "1",
                Path: ownerPath,
                Kind: MeshChangeKind.Updated,
                NodeType: null,
                Version: i + 1,
                Timestamp: DateTimeOffset.UtcNow));
            Thread.Sleep(EventSpacing);
        }

        // Let the resubscribe window fully elapse plus margin so any coalesced resubscribe has
        // fired and been counted. Sanctioned fixed wait: we are bounding "how many resubscribes
        // happened", which has no single positive signal to await.
        Thread.Sleep(ResubscribeWindow + TimeSpan.FromMilliseconds(500));

        var resubscribes = Volatile.Read(ref _subscribeCount) - afterInitial;
        Output.WriteLine($"DIAG afterInitial={afterInitial} total={Volatile.Read(ref _subscribeCount)} resubscribes={resubscribes}");

        // A burst of N owner change events must collapse to ONE fresh-snapshot resubscribe,
        // not N. Allow ≤1 (a single coalesced resubscribe). Without the fix this climbs toward
        // BurstSize (each settled event clears the in-flight guard before the next, firing its
        // own SubscribeRequest → a synchronous sync-hub creation on the owner's action block).
        resubscribes.Should().BeLessThanOrEqualTo(1,
            $"a burst of {BurstSize} owner change-feed events must coalesce into at most one " +
            "fresh-snapshot resubscribe — per-event resubscribe storms the owner/cache hub's action block");
    }

    /// <summary>
    /// Pins the OTHER half of the contract: coalescing must NOT swallow legitimate
    /// recreate detection. A SINGLE owner-path change-feed event (a genuine owner
    /// restart/recreate) must still trigger exactly one fresh-snapshot resubscribe after
    /// the throttle window elapses. Guards against a fix that debounces so aggressively (or
    /// drops events) that a real recreate is never picked up.
    /// </summary>
    [HubFact]
    public async Task SingleOwnerChangeFeedEvent_StillTriggersResubscribe()
    {
        var host = GetHost();
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        var changeFeed = client.ServiceProvider.GetRequiredService<IMeshChangeFeed>();

        await workspace.GetObservable<BusinessUnit>()
            .Should().Within(10.Seconds())
            .Match(x => x.Count > 0, "the owner must serve the initial snapshot");

        var afterInitial = Volatile.Read(ref _subscribeCount);

        // One isolated change-feed event on the owner's path — the recreate signal.
        changeFeed.Publish(new MeshChangeEvent(
            Namespace: HostType,
            Id: "1",
            Path: HostType + "/1",
            Kind: MeshChangeKind.Created,
            NodeType: null,
            Version: 1,
            Timestamp: DateTimeOffset.UtcNow));

        // The throttle emits the single pulse one window later → exactly one resubscribe
        // SubscribeRequest reaches the owner. Wait on that condition (not a fixed sleep).
        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Volatile.Read(ref _subscribeCount) - afterInitial)
            .Should().Within(10.Seconds())
            .Match(r => r >= 1,
                "a genuine owner recreate must still trigger a fresh-snapshot resubscribe");
    }
}
