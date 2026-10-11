using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>Issue #6429 on a real cluster: two silos, a third one joining, and the real memory
/// stream.</b>
///
/// <para><see cref="StreamReattachHoldsAChangeMidAttachTest"/> pins the ORDERING with a feed the
/// test pushes. This is the outcome on the real machinery: a hub on silo 0 whose stream attach was
/// in flight when a silo joined, and which then gave up, must be reachable from silo 1 over the
/// stream afterwards.</para>
///
/// <para><b>The switches.</b></para>
/// <list type="bullet">
///   <item><b>In flight</b>: the attach waits on a readiness gate the test owns, exactly where the
///     production attach waits on Orleans' own. Nothing blocks; the attach is a pending task.</item>
///   <item><b>Give up</b>: the first attach of each address fails with a fault the classifier calls
///     permanent. Every later one reaches the silo's real stream provider.</item>
///   <item><b>The change</b>: a real one. A third silo joins, and silo 0's own
///     <see cref="IClusterMembershipFeed"/> reports it. The test relays that feed and CLOSES the
///     relay once the change has been handed to the hub, so no later change can do the repairing.
///     Without that, the unfixed code passes whenever a trailing notification of the same join
///     happens to land after the give-up.</item>
/// </list>
///
/// <para><b>Negative control</b>, in the same run and through the same instrument: a second hub is
/// registered after the relay is closed. Its attach fails identically, it has seen no change, and a
/// publish from silo 1 does NOT reach it. So the stream leg reads "unattached" when it is, and
/// nothing but the membership change repairs it.</para>
///
/// <para><b>Fails on the unfixed code</b> at the first assertion: the change was dropped while the
/// attach was pending, and the relay is closed, so nothing re-attaches.</para>
/// </summary>
public class AGivenUpStreamAttachIsRepairedByTheChangeItMissedTest(
    AGivenUpStreamAttachIsRepairedByTheChangeItMissedTest.Cluster mesh)
    : IClassFixture<AGivenUpStreamAttachIsRepairedByTheChangeItMissedTest.Cluster>
{
    /// <summary>Its own cluster: the case adds a silo.</summary>
    public class Cluster : FaultInjectionCluster;

    private static readonly Address Sender = new("client", "reattach-probe-sender");

    [Fact(Timeout = 300_000)]
    public async Task TheHubThatMissedTheChangeIsReachableAgain_AndTheOneThatSawNoneIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var silo0 = mesh.Silo(0);

        var realFeed = silo0.GetService<IClusterMembershipFeed>();
        realFeed.Should().NotBeNull("a silo registers the membership feed; without it nothing can re-attach");
        using var relay = new ClosableFeed(realFeed!);
        var readiness = new OrleansStreamingReadiness();
        var streams = new FirstAttachFails(silo0.GetRequiredKeyedService<IStreamProvider>(StreamProviders.Memory));
        var overlay = new Overlay(silo0, relay, readiness, streams);

        using var routing = new OrleansRoutingService(null!, overlay, NullLogger<OrleansRoutingService>.Instance)
        {
            AttachBackoff = _ => TimeSpan.Zero
        };

        // The hub at risk: registered BEFORE the membership change, its attach pending at the gate.
        var missed = new Address("client", $"missed-{Guid.NewGuid():N}");
        var missedInbox = new ConcurrentQueue<IMessageDelivery>();
        using var missedRegistration = routing.RegisterStream(missed, Into(missedInbox));

        // A REAL membership change, and proof that this hub's trigger has been handed it.
        var handed = relay.Handed.Replay();
        using (handed.Connect())
        {
            await mesh.Cluster.StartAdditionalSiloAsync();
            await handed.Take(1).Should().Within(TestTimeouts.Convergence).Emit(
                "a silo joining must reach silo 0's membership feed and, through it, the hub's "
                + "re-attach trigger", ct);
        }

        // From here on NO membership change reaches either hub.
        relay.Close();

        // The control: identical, except that it has seen no change at all.
        var unseen = new Address("client", $"unseen-{Guid.NewGuid():N}");
        var unseenInbox = new ConcurrentQueue<IMessageDelivery>();
        using var unseenRegistration = routing.RegisterStream(unseen, Into(unseenInbox));

        // Both attaches now run, and both give up on their first attempt.
        await ((ILifecycleObserver)readiness).OnStart(CancellationToken.None);
        await routing.AttachSettled(missed)!.WaitAsync(TestTimeouts.Convergence, ct);
        await routing.AttachSettled(unseen)!.WaitAsync(TestTimeouts.Convergence, ct);
        streams.Failed(missed).Should().BeTrue("the injected give-up must have been met, or nothing was injected");
        streams.Failed(unseen).Should().BeTrue("the control's attach must have failed the same way");

        await PublishFromSilo1Until(missed, () => !missedInbox.IsEmpty)
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the cluster's membership moved while this hub's attach was in flight. Its give-up "
                + "must be repaired by that change, not left until an unrelated later one (#6429)",
                ct);

        await PublishFromSilo1Until(unseen, () => !unseenInbox.IsEmpty)
            .Should().NotEmit(TimeSpan.FromSeconds(5),
                "NEGATIVE CONTROL: this hub's attach gave up and no membership change has reached "
                + "it, so nothing may have re-attached it. If this delivers, the repair is a timer or "
                + "an unconditional retry and the assertion above proves nothing about the change",
                ct);
    }

    private static AsyncDelivery Into(ConcurrentQueue<IMessageDelivery> inbox) => (d, _) =>
    {
        inbox.Enqueue(d);
        return Observable.Return(d);
    };

    /// <summary>
    /// Publishes to <paramref name="target"/>'s stream from silo 1 on an interval until
    /// <paramref name="delivered"/> holds. A publish to a stream nobody is subscribed to succeeds
    /// and is discarded, and a subscription only receives what is published after it attached, so
    /// one publish cannot be the measurement.
    /// </summary>
    private IObservable<Unit> PublishFromSilo1Until(Address target, Func<bool> delivered)
    {
        var stream = mesh.Silo(1).GetRequiredKeyedService<IStreamProvider>(StreamProviders.Memory)
            .GetStream<IMessageDelivery>(target.ToString());
        return Observable.Interval(TimeSpan.FromMilliseconds(250), Scheduler.Default)
            .StartWith(0L)
            .Select(n => Observable.Defer(() => stream.OnNextAsync(Delivery(target, n)).ToObservable()))
            .Concat()
            .Where(_ => delivered())
            .Take(1);
    }

    private static IMessageDelivery Delivery(Address target, long n) =>
        new MessageDelivery<RawJson>(
            Sender, target, new RawJson("{\"$type\":\"PingRequest\"}"), JsonSerializerOptions.Default)
            with { Id = $"reattach-probe-{n}" };

    /// <summary>
    /// The silo's feed, relayed until <see cref="Close"/>. <see cref="Handed"/> emits AFTER a change
    /// has been handed to the subscriber, so "the hub's trigger has it" is observed, not assumed.
    /// </summary>
    private sealed class ClosableFeed(IClusterMembershipFeed inner) : IClusterMembershipFeed, IDisposable
    {
        // A BehaviorSubject, so a subscription taken AFTER the close is closed too.
        private readonly BehaviorSubject<bool> closed = new(false);
        private readonly Subject<long> handed = new();

        public IObservable<long> Handed => handed;

        public IObservable<long> Changes => Observable.Create<long>(observer =>
            inner.Changes.TakeUntil(closed.Where(isClosed => isClosed)).Subscribe(
                change =>
                {
                    observer.OnNext(change);
                    handed.OnNext(change);
                },
                observer.OnError,
                observer.OnCompleted));

        public void Close() => closed.OnNext(true);

        public void Dispose()
        {
            closed.Dispose();
            handed.Dispose();
        }
    }

    /// <summary>Fails the first attach of each stream with a permanent fault; later ones are the silo's own.</summary>
    private sealed class FirstAttachFails(IStreamProvider inner) : IStreamProvider
    {
        private readonly ConcurrentDictionary<string, int> attempts = new(StringComparer.Ordinal);

        public string Name => inner.Name;
        public bool IsRewindable => inner.IsRewindable;

        public bool Failed(Address address) =>
            attempts.Keys.Any(key => key.Contains(address.ToString(), StringComparison.Ordinal));

        public IAsyncStream<T> GetStream<T>(StreamId streamId)
        {
            if (attempts.AddOrUpdate(streamId.ToString(), 1, (_, n) => n + 1) == 1)
                throw new NotSupportedException(
                    "injected: the first attach of this stream fails permanently, so the attach gives up");
            return inner.GetStream<T>(streamId);
        }
    }

    /// <summary>
    /// Silo 0's services with three substitutions: the relayed feed, the test's readiness gate and
    /// the faulting stream provider. <c>ILocalSiloDetails</c> is withheld so the routing service
    /// built on it makes no pod-hub claim of its own; the stream leg is the subject.
    /// </summary>
    private sealed class Overlay(
        IServiceProvider silo,
        IClusterMembershipFeed feed,
        OrleansStreamingReadiness readiness,
        IStreamProvider streams) : IServiceProvider, IKeyedServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IClusterMembershipFeed) ? feed
            : serviceType == typeof(OrleansStreamingReadiness) ? readiness
            : serviceType == typeof(ILocalSiloDetails) ? null
            : silo.GetService(serviceType);

        public object? GetKeyedService(Type serviceType, object? serviceKey) =>
            serviceType == typeof(IStreamProvider) && Equals(serviceKey, StreamProviders.Memory)
                ? streams
                : ((IKeyedServiceProvider)silo).GetKeyedService(serviceType, serviceKey);

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey) =>
            GetKeyedService(serviceType, serviceKey)
            ?? throw new InvalidOperationException($"No keyed service {serviceType} / {serviceKey}.");
    }
}
