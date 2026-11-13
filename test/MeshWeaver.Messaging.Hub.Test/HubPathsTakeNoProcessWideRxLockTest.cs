using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// A hub's request/response round trip and its teardown must not take Rx's process-wide
/// <c>SystemClock</c> registration lock (MeshWeaver#5799, MeshWeaver#5388).
///
/// <para>The 2026-11-11 09:27:09Z <c>[POOLSTALL]</c> capture on memex-cloud <c>…px94w</c> had 14 of 41 pool
/// workers in <c>Monitor.Enter_Slowpath</c> inside <c>SystemClock.Register</c>. They were called from
/// <c>ImmediateScheduler.Schedule</c> ← <c>ToObservableRecursive&lt;Int64&gt;</c>, which is the
/// <c>StartWith(-1L)</c> of <c>MessageHub.StartQuiesceWait</c>, while a 47-hub teardown burst ran. Rx's
/// <c>StartWith</c> creates an <c>AsyncLockScheduler</c> per subscription, and every one of them registers
/// under that one lock; see <see cref="InlineRxOperators"/>.</para>
///
/// <para><b>How the test pins it.</b> A dedicated thread HOLDS that lock for the duration (the park is the
/// subject; it is released in a <c>finally</c>). Anything that still needs it cannot complete until the
/// lock is released. The negative control is Rx's own <c>StartWith</c>, which must NOT complete while the
/// lock is held, so a green run cannot come from a hold that did not take effect.</para>
/// </summary>
public class HubPathsTakeNoProcessWideRxLockTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private record PingRequest : IRequest<PongEvent>;

    private record PongEvent;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) =>
        configuration.WithHandler<PingRequest>((hub, request) =>
        {
            hub.Post(new PongEvent(), options => options.ResponseFor(request));
            return request.Processed();
        });

    /// <summary>The lock <c>SystemClock.Register</c> takes: its static set of registered schedulers.</summary>
    private static object SystemClockRegistrationLock()
    {
        var systemClock = typeof(Observable).Assembly.GetType("System.Reactive.PlatformServices.SystemClock", throwOnError: true)!;
        var set = systemClock.GetField("SystemClockChanged", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?.GetValue(null);
        set.Should().NotBeNull("System.Reactive's SystemClock keeps its registrations in a static SystemClockChanged set");
        return set!;
    }

    [Fact]
    public async Task ARoundTripAndATeardown_CompleteWhileRxsSystemClockLockIsHeld()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var host = GetHost();
        var client = GetClient();
        // Warm the route first: one-time initialisation is not the subject.
        await client.Observe(new PingRequest(), o => o.WithTarget(host.Address)).Should().Within(Budget).Emit();

        var gate = SystemClockRegistrationLock();
        var holding = 0;
        var release = 0;
        var holder = new Thread(() =>
        {
            lock (gate)
            {
                Volatile.Write(ref holding, 1);
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TimeSpan.FromSeconds(60));
            }
        }) { IsBackground = true, Name = "rx-systemclock-holder" };
        holder.Start();
        try
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref holding) == 1, Budget).Should().BeTrue("the holder took the lock");

            Output.WriteLine($"[{clock.Elapsed}] holding");
            // Negative control: Rx's own StartWith needs the held lock, so it cannot complete now.
            var controlDone = 0;
            new Thread(() =>
            {
                Observable.Return(1).StartWith(0).Subscribe(_ => { }, () => Volatile.Write(ref controlDone, 1));
            }) { IsBackground = true, Name = "rx-startwith-control" }.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref controlDone) == 1, TimeSpan.FromSeconds(1)).Should().BeFalse(
                "StartWith registers an AsyncLockScheduler under SystemClock's lock, so the hold must stop it — otherwise this test proves nothing");

            Output.WriteLine($"[{clock.Elapsed}] control blocked as expected");
            // The subject. ApplyTimeout runs on every request, and it used to StartWith.
            await client.Observe(new PingRequest(), o => o.WithTarget(host.Address)).Should().Within(Budget)
                .Emit("a request/response round trip must not need Rx's process-wide SystemClock lock");

            Output.WriteLine($"[{clock.Elapsed}] round trip done");
            // Teardown: the disposal watchdog and the Quiescing wait (StartQuiesceWait) used to StartWith.
            client.Dispose();
            await client.DisposalCompleted.FirstOrDefaultAsync().Should().Within(Budget)
                .Emit("a hub's teardown must not need Rx's process-wide SystemClock lock");
        }
        finally
        {
            Output.WriteLine($"[{clock.Elapsed}] releasing");
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// <c>MergeInline</c> subscribes its sources synchronously and in order, as Rx's <c>Merge</c> does. The control
    /// for the ordering is Rx's own <c>Merge</c> over the same sources, which must produce the same log.
    /// </summary>
    [Fact]
    public void MergeInline_SubscribesItsSourcesInOrderDuringSubscribe_AsMergeDoes()
    {
        System.Collections.Immutable.ImmutableList<string> Run(Func<IObservable<int>, IObservable<int>, IObservable<int>> merge)
        {
            var log = System.Collections.Immutable.ImmutableList<string>.Empty;
            var first = Observable.Defer(() => { log = log.Add("subscribe first"); return Observable.Never<int>(); });
            var second = Observable.Defer(() => { log = log.Add("subscribe second"); return Observable.Return(2); });
            using (merge(first, second).Subscribe(value => log = log.Add($"value {value}")))
                log = log.Add("Subscribe returned");
            return log;
        }

        var inline = Run((a, b) => a.MergeInline(b));
        inline.Should().Equal("subscribe first", "subscribe second", "value 2", "Subscribe returned");
        inline.Should().Equal(Run((a, b) => a.Merge(b)));
        Run((a, b) => new[] { a, b }.MergeInline()).Should().Equal(inline);
    }

    [Fact]
    public void StartWithInline_EmitsTheValueSynchronouslyThenTheSource()
    {
        var seen = System.Collections.Immutable.ImmutableList<int>.Empty;
        var completed = false;
        using (Observable.Range(1, 2, System.Reactive.Concurrency.ImmediateScheduler.Instance).StartWithInline(0)
                   .Subscribe(value => seen = seen.Add(value), () => completed = true))
        {
            completed.Should().BeTrue("every element here is produced inline");
            seen.Should().Equal(0, 1, 2);
        }

        var emittedDuringSubscribe = false;
        using (Observable.Never<int>().StartWithInline(7).Subscribe(_ => emittedDuringSubscribe = true))
            emittedDuringSubscribe.Should().BeTrue("the first value is emitted inside Subscribe, as StartWith does");
    }
}
