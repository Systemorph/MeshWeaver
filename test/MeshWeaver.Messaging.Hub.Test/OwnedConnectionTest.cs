using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>A multicast chain's connection belongs to its owner, and the owner's disposal releases it —
/// including a connect that is still QUEUED.</b>
///
/// <para><b>The live defect.</b> <c>Defer(…).SubscribeOn(TaskPoolScheduler).Replay(1).AutoConnect(1)</c>:
/// the first subscriber's <c>Connect()</c> only queues the upstream subscribe on the pool, and the
/// handle <c>Connect()</c> returns stays inside <c>AutoConnect</c>. Nothing in a teardown joins a
/// pool-queued Rx subscribe, so the item ran whenever the pool reached it — after the owner's scope
/// had closed, resolving services from a disposed container. Measured on MeshWeaver.Plugins run
/// 34222933802 (2026-09-08): 11 disposed-scope stragglers across three suites, all this shape
/// (core #3737 fixed the one site; <c>OwnedConnectionExtensions</c> is the shape for every site).</para>
///
/// <para><b>The contract, each arm with its control.</b> A connect still queued when the owner is
/// released never runs (the control arm proves the connection WAS registered — a helper that
/// registered nothing would read zero before and after); a live upstream is unsubscribed with the
/// owner; a subscriber arriving after the release is refused, naming the owner; a chain that
/// terminates drops its handle so the owner tracks live connections only; and a hub-owned
/// connection is released in the hub's ShutDown phase.</para>
///
/// <para><b>Negative control</b> (run once, by hand, when this test was written): replacing the
/// helper's body with a bare <c>source.AutoConnect(minObservers)</c> fails
/// <see cref="AConnectStillQueuedWhenTheOwnerIsReleased_NeverRuns"/> at the control arm
/// (<c>owner.Count</c> reads 0, not 1) and, with the control removed, at the assertion that the
/// factory never ran — the queued connect goes ahead against the disposed owner.</para>
/// </summary>
public class OwnedConnectionTest(ITestOutputHelper output) : HubTestBase(output)
{
    [Fact]
    public void AConnectStillQueuedWhenTheOwnerIsReleased_NeverRuns()
    {
        var scheduler = new TestScheduler();
        var owner = new CompositeDisposable();
        var factoryRuns = 0;

        // The #3737 shape, verbatim: the factory is the Defer that resolved a workspace from a
        // disposed scope, SubscribeOn is the pool hand-off, Replay(1) the shared buffer.
        var shared = Observable
            .Defer(() =>
            {
                factoryRuns++;
                return Observable.Return(42);
            })
            .SubscribeOn(scheduler)
            .Replay(1)
            .AutoConnectOwnedBy(owner, "test-owner");

        var received = new List<Notification<int>>();
        using var subscription = shared.Materialize().Subscribe(received.Add);

        factoryRuns.Should().Be(0, "precondition: SubscribeOn queued the connect; the pool has not run it");
        owner.Count.Should().Be(1,
            "CONTROL ARM: the first subscriber's Connect() must be registered with the owner the instant "
            + "it runs — a zero here means the helper registered nothing, and the assertion below would be "
            + "vacuous");

        owner.Dispose();
        scheduler.Start(); // the pool gets to the queued item — AFTER the owner is gone

        factoryRuns.Should().Be(0,
            "the queued connect must be CANCELLED by the owner's release, never run against a disposed "
            + "owner — this is the GetQueryRaw → GetWorkspace disposed-scope straggler");
        received.Should().BeEmpty("a cancelled connect feeds nothing to the subscriber that queued it");
    }

    [Fact]
    public void ALiveConnection_IsReleasedWithItsOwner()
    {
        var upstream = new Subject<int>();
        var owner = new CompositeDisposable();
        var shared = upstream.Replay(1).AutoConnectOwnedBy(owner, "test-owner");

        using var subscription = shared.Subscribe(_ => { });
        upstream.HasObservers.Should().BeTrue("CONTROL ARM: the first subscriber connected the upstream");
        owner.Count.Should().Be(1, "CONTROL ARM: the connection is registered");

        owner.Dispose();

        upstream.HasObservers.Should().BeFalse(
            "the owner's disposal unsubscribes the upstream — the connection is no longer rooted by the chain");
    }

    [Fact]
    public void ASubscriptionAfterTheRelease_IsRefusedNamingTheOwner()
    {
        var upstream = new Subject<int>();
        var owner = new CompositeDisposable();
        var shared = upstream.Replay(1).AutoConnectOwnedBy(owner, "test-owner");

        using (shared.Subscribe(_ => { }))
            upstream.OnNext(7);
        owner.Dispose();

        // The refusal is emitted synchronously on subscribe (a Defer that throws), so a plain
        // Subscribe captures it — no blocking bridge, nothing to wait on.
        Notification<int>? terminal = null;
        using (shared.Materialize().Take(1).Subscribe(n => terminal = n))
        {
        }

        terminal.Should().NotBeNull("the refusal is synchronous — a subscriber is told at once, never parked");
        terminal!.Kind.Should().Be(NotificationKind.OnError,
            "a subscriber arriving after the release must TERMINATE — a bare Replay(1) would hand it the "
            + "buffered 7 and then go silent forever, the burst-then-silence hang");
        terminal.Exception.Should().BeOfType<ObjectDisposedException>()
            .Which.ObjectName.Should().Be("test-owner", "the refusal names the owner so the straggler is attributable");
        upstream.HasObservers.Should().BeFalse("a refused subscription opens no upstream");
    }

    [Fact]
    public void ATerminatedChain_DropsItsHandleFromTheOwner_AndStillReplays()
    {
        var upstream = new Subject<int>();
        var owner = new CompositeDisposable();
        var shared = upstream.Replay(1).AutoConnectOwnedBy(owner, "test-owner");

        using var subscription = shared.Subscribe(_ => { });
        owner.Count.Should().Be(1, "CONTROL ARM: registered while live");

        upstream.OnNext(1);
        upstream.OnCompleted();

        owner.Count.Should().Be(0,
            "a terminated chain's connection holds nothing — it leaves the owner so a faulted-then-rebuilt "
            + "promise cannot accumulate one dead handle per attempt for the life of the process");

        var late = new List<Notification<int>>();
        shared.Materialize().Subscribe(late.Add);
        late.Should().HaveCount(2, "the owner is NOT disposed, so the settled promise still replays to a late subscriber");
        late[0].Value.Should().Be(1);
        late[1].Kind.Should().Be(NotificationKind.OnCompleted);
    }

    [Fact]
    public void AFaultedChain_DropsItsHandleFromTheOwner()
    {
        var upstream = new Subject<int>();
        var owner = new CompositeDisposable();
        var shared = upstream.Replay(1).AutoConnectOwnedBy(owner, "test-owner");

        using var subscription = shared.Subscribe(_ => { }, _ => { });
        owner.Count.Should().Be(1, "CONTROL ARM: registered while live");

        upstream.OnError(new InvalidOperationException("upstream fault"));

        owner.Count.Should().Be(0, "a faulted chain's connection is released with its eviction");
    }

    [Fact]
    public void ConnectOwnedBy_ConnectsNow_AndReleasesWithTheOwner()
    {
        var upstream = new Subject<int>();
        var owner = new CompositeDisposable();
        var published = upstream.Publish();

        upstream.HasObservers.Should().BeFalse("precondition: nothing is connected before ConnectOwnedBy");
        published.ConnectOwnedBy(owner);
        upstream.HasObservers.Should().BeTrue("CONTROL ARM: ConnectOwnedBy connects eagerly — a feed fills without subscribers");
        owner.Count.Should().Be(1);

        owner.Dispose();
        upstream.HasObservers.Should().BeFalse("the owner's disposal ends the feed");
    }

    [Fact]
    public void AConnectionRegisteredOnADisposedOwner_IsReleasedOnTheSpot()
    {
        var upstream = new Subject<int>();
        var owner = new CompositeDisposable();
        owner.Dispose();

        // The sliver the Defer guard cannot see: a Connect() that ran between the guard's read and
        // the owner's disposal. CompositeDisposable's contract — Add on a disposed composite
        // disposes the item — is what closes it; ConnectOwnedBy exercises that path directly.
        upstream.Publish().ConnectOwnedBy(owner);

        upstream.HasObservers.Should().BeFalse(
            "a registration arriving after the owner's disposal is disposed as it is added — the "
            + "late-connect race resolved by construction");
    }

    [Fact(Timeout = 120_000)]
    public async Task AHubOwnedConnection_IsReleasedInTheHubsShutDown()
    {
        var host = GetHost();
        var hub = host.GetHostedHub(new Address("owned-connection", "1"), c => c);

        var upstream = new Subject<int>();
        var shared = upstream.Replay(1).AutoConnectOwnedBy(hub, "hosted-owner");
        using var subscription = shared.Subscribe(_ => { }, _ => { });
        upstream.HasObservers.Should().BeTrue("CONTROL ARM: connected on the live hub");

        hub.Dispose();
        await hub.DisposalCompleted.FirstOrDefaultAsync().Await().WaitAsync(TimeSpan.FromSeconds(120));

        upstream.HasObservers.Should().BeFalse(
            "RegisterForDisposal runs the release in the hub's ShutDown phase — strictly inside its "
            + "DisposalCompleted, before any scope closes");

        Notification<int>? terminal = null;
        using (shared.Materialize().Take(1).Subscribe(n => terminal = n))
        {
        }

        terminal.Should().NotBeNull("the refusal is synchronous");
        terminal!.Kind.Should().Be(NotificationKind.OnError, "a subscriber after the hub's teardown is refused");
        terminal.Exception.Should().BeOfType<ObjectDisposedException>().Which.ObjectName.Should().Be("hosted-owner");
    }
}
