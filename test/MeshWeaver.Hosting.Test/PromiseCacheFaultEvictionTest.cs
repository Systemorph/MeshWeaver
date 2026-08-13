using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Issue #1369 — the promise-cache must cache a SUCCESS and evict a FAILURE.
///
/// <para><b>The defect these pin.</b> <see cref="IoPoolExtensions.Run{T}"/> is
/// <see cref="ReplaySubject{T}"/>-backed, and a <c>ReplaySubject</c> latches <i>terminals</i> —
/// <c>OnError</c> included. Held in a bare <c>ConcurrentDictionary&lt;key, IObservable&gt;</c> (the
/// recipe before this change) a single transient fault therefore became PERMANENT: every later
/// subscriber was replayed that same exception for the life of the process, and nothing
/// re-attempted. In production that meant a partition whose <c>CREATE SCHEMA</c> failed once could
/// never be provisioned again — every write to it <c>42P01</c>-ing until the pod restarted.</para>
///
/// <para><b>The load-bearing assertion is the ATTEMPT COUNT</b>, not "the second call worked". A
/// second call that succeeds proves nothing on its own — a cache that happened to hold a healthy
/// value would look identical. What distinguishes eviction from luck is that the upstream ran a
/// SECOND time: a genuinely fresh attempt rather than a replay. Same technique as #1367's
/// <c>SyncedQueryFaultRecoveryTest</c>.</para>
///
/// <para>No mocking of framework types: the "flaky upstream" is a real observable that faults only
/// its first subscription — the shape of a transient connect timeout — and the promise around it is
/// built with the exact shape <see cref="IoPoolExtensions.Run{T}"/> uses (eager subscribe into a
/// <see cref="ReplaySubject{T}"/>), minus the thread hop, so the tests stay deterministic.</para>
/// </summary>
public class PromiseCacheFaultEvictionTest
{
    private static readonly TimeSpan Timeout10 = TimeSpan.FromSeconds(10);

    /// <summary>
    /// An upstream that faults its first <paramref name="failFirst"/> subscriptions and is healthy
    /// afterwards, counting every attempt. Each subscription is independent — exactly what a
    /// re-attempt looks like against a database that has recovered.
    /// </summary>
    private sealed class FlakyUpstream(int failFirst, string value)
    {
        private int attempts;

        /// <summary>How many times the leaf actually ran. The eviction proof.</summary>
        public int Attempts => Volatile.Read(ref attempts);

        public InvalidOperationException Fault { get; } = new("transient upstream failure");

        public IObservable<string> Build() => Observable.Create<string>(observer =>
        {
            if (Interlocked.Increment(ref attempts) <= failFirst)
                observer.OnError(Fault);
            else
            {
                observer.OnNext(value);
                observer.OnCompleted();
            }

            return System.Reactive.Disposables.Disposable.Empty;
        });

        /// <summary>
        /// The promise shape <c>IIoPool.Run</c> produces: the leaf is subscribed EAGERLY and its
        /// notifications — value or terminal — are replayed to everyone who attaches later. This is
        /// the thing that latches, and therefore the thing the cache must evict.
        /// </summary>
        public IObservable<string> RunEagerly()
        {
            var subject = new ReplaySubject<string>();
            Build().Subscribe(subject);
            return subject.AsObservable();
        }
    }

    // ── The core contract ────────────────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task TransientFault_IsNotReplayedToTheNextCaller()
    {
        var upstream = new FlakyUpstream(failFirst: 1, value: "provisioned");
        var cache = new PromiseCache<string, string>();
        var ct = TestContext.Current.CancellationToken;

        IObservable<string> Get() => cache.GetOrAdd("schema", _ => upstream.RunEagerly());

        // 1. The first caller hits the fault — and SEES it. Eviction must never swallow.
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Get().Timeout(Timeout10).FirstAsync().ToTask(ct));
        Assert.Same(upstream.Fault, observed);

        // 2. The next caller gets a genuinely NEW attempt, not the latched terminal.
        Assert.Equal("provisioned", await Get().Timeout(Timeout10).FirstAsync().ToTask(ct));

        // 3. 🚨 The assertion that distinguishes eviction from luck: the leaf ran twice. A replayed
        //    terminal would leave this at 1 — which is exactly what the bare dictionary did.
        Assert.Equal(2, upstream.Attempts);
    }

    [Fact(Timeout = 30000)]
    public async Task Success_IsCached_AndTheUpstreamRunsExactlyOnce()
    {
        var upstream = new FlakyUpstream(failFirst: 0, value: "provisioned");
        var cache = new PromiseCache<string, string>();
        var ct = TestContext.Current.CancellationToken;

        IObservable<string> Get() => cache.GetOrAdd("schema", _ => upstream.RunEagerly());

        for (var i = 0; i < 5; i++)
            Assert.Equal("provisioned", await Get().Timeout(Timeout10).FirstAsync().ToTask(ct));

        // The whole point of a promise-cache: the work happened once, not five times.
        Assert.Equal(1, upstream.Attempts);
    }

    [Fact(Timeout = 30000)]
    public async Task RepeatedFaults_ReAttemptOncePerAsk_AndNeverOnTheirOwn()
    {
        var upstream = new FlakyUpstream(failFirst: 3, value: "provisioned");
        var cache = new PromiseCache<string, string>();
        var ct = TestContext.Current.CancellationToken;

        IObservable<string> Get() => cache.GetOrAdd("schema", _ => upstream.RunEagerly());

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Get().Timeout(Timeout10).FirstAsync().ToTask(ct));

        // 🚨 Exactly one attempt per ASK — the cache is not a retry loop, a timer or a poller. If
        // it re-attempted on its own this count would outrun the number of calls, which is the
        // resubscribe-storm shape that took production down on 2026-06-08.
        Assert.Equal(3, upstream.Attempts);

        Assert.Equal("provisioned", await Get().Timeout(Timeout10).FirstAsync().ToTask(ct));
        Assert.Equal(4, upstream.Attempts);
    }

    [Fact(Timeout = 30000)]
    public async Task ALiveConnectableEntry_IsEvictedToo_NotJustAnEagerOneShot()
    {
        // DeckSlidesCache's shape: Replay(1).AutoConnect(1) over a live query. The connectable is
        // backed by ONE ReplaySubject and AutoConnect(1) never reconnects, so it latches exactly
        // like the eager one-shot — a deck that faulted once would never render again.
        var upstream = new FlakyUpstream(failFirst: 1, value: "slides");
        var cache = new PromiseCache<string, string>();
        var ct = TestContext.Current.CancellationToken;

        IObservable<string> Get() => cache.GetOrAdd(
            "deck", _ => upstream.Build().Replay(1).AutoConnect(1));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Get().Timeout(Timeout10).FirstAsync().ToTask(ct));
        Assert.Equal("slides", await Get().Timeout(Timeout10).FirstAsync().ToTask(ct));
        Assert.Equal(2, upstream.Attempts);
    }

    // ── Concurrency ──────────────────────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public void EvictionIsPairExact_AndNeverDropsAHealthyReplacement()
    {
        var cache = new PromiseCache<string, string>();
        var first = new ReplaySubject<string>();
        var second = new ReplaySubject<string>();
        var sources = new Queue<IObservable<string>>();
        sources.Enqueue(first.AsObservable());
        sources.Enqueue(second.AsObservable());
        var builds = 0;

        IObservable<string> Get() => cache.GetOrAdd("schema", _ =>
        {
            builds++;
            return sources.Dequeue();
        });

        var firstEntry = Get();
        var firstErrors = new List<Exception>();
        firstEntry.Subscribe(_ => { }, firstErrors.Add);

        // The first attempt faults; its subscriber sees it and the entry is evicted.
        first.OnError(new InvalidOperationException("transient"));
        Assert.Single(firstErrors);
        Assert.False(cache.Contains("schema"));

        // A later caller installs a HEALTHY replacement under the same key.
        var secondEntry = Get();
        Assert.Equal(2, builds);
        Assert.True(cache.Contains("schema"));
        Assert.NotSame(firstEntry, secondEntry);

        // 🚨 Now a straggler subscribes to the DEAD chain — a subscriber handed the old observable
        // before the fault landed. Its ReplaySubject replays the terminal, so eviction fires a
        // SECOND time for the same key. Removing by key alone would drop the healthy replacement
        // here, trading a permanent fault for permanent duplicate work.
        firstEntry.Subscribe(_ => { }, _ => { });

        Assert.True(cache.Contains("schema"));
        Assert.Same(secondEntry, Get());
        Assert.Equal(2, builds);

        second.OnNext("provisioned");
        second.OnCompleted();
    }

    [Fact(Timeout = 30000)]
    public void AnInFlightPromise_IsSharedByConcurrentCallers_AndNotEvicted()
    {
        var cache = new PromiseCache<string, string>();
        var gate = new ReplaySubject<string>();
        var builds = 0;

        IObservable<string> Get() => cache.GetOrAdd("schema", _ =>
        {
            Interlocked.Increment(ref builds);
            return gate.AsObservable();
        });

        var results = new List<string>();
        Get().Subscribe(results.Add);
        Get().Subscribe(results.Add);
        Get().Subscribe(results.Add);

        // Still in flight: one build, one shared promise, entry intact — the promise's whole point.
        Assert.Equal(1, builds);
        Assert.True(cache.Contains("schema"));
        Assert.Empty(results);

        gate.OnNext("provisioned");
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal("provisioned", r));
        Assert.True(cache.Contains("schema"));
    }

    [Fact(Timeout = 30000)]
    public void ConcurrentFirstCallers_BuildTheEagerFactoryExactlyOnce()
    {
        var cache = new PromiseCache<string, string>();
        var builds = 0;
        const int callers = 16;
        // Dedicated threads, not Parallel.For: the barrier must actually fill, and a constrained
        // runner's ThreadPool may not hand out 16 workers at once.
        using var start = new Barrier(callers);
        var threads = new Thread[callers];

        for (var i = 0; i < callers; i++)
        {
            threads[i] = new Thread(() =>
            {
                start.SignalAndWait();
                cache.GetOrAdd("schema", _ =>
                {
                    // Stands in for pool.Run, which is EAGER — it schedules its round-trip at
                    // construction. A factory invoked twice and discarded once would therefore
                    // have fired a real, unobserved second round-trip (a duplicate CREATE SCHEMA,
                    // a spawned-then-orphaned Copilot CLI). ConcurrentDictionary.GetOrAdd alone
                    // does not guarantee single invocation under contention; the entry's Lazy does.
                    Interlocked.Increment(ref builds);
                    return Observable.Return("provisioned");
                });
            }) { IsBackground = true };
            threads[i].Start();
        }

        foreach (var thread in threads)
            Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "a contending caller never finished");

        Assert.Equal(1, builds);
    }

    // ── The single-slot variant ──────────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task PromiseSlot_EvictsAFaultedHandshake_SoTheNextCallDialsAgain()
    {
        var upstream = new FlakyUpstream(failFirst: 1, value: "connected");
        var slot = new PromiseSlot<string>();
        var ct = TestContext.Current.CancellationToken;

        IObservable<string> Connect() => slot.GetOrCreate(upstream.RunEagerly);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Connect().Timeout(Timeout10).FirstAsync().ToTask(ct));

        Assert.Equal("connected", await Connect().Timeout(Timeout10).FirstAsync().ToTask(ct));

        // A fresh handshake, not a replayed dead one.
        Assert.Equal(2, upstream.Attempts);

        // And once connected the slot hands the SAME promise to teardown — TryGet must never open
        // a new connection just to close one.
        Assert.True(slot.TryGet(out var held));
        Assert.Equal("connected", await held!.Timeout(Timeout10).FirstAsync().ToTask(ct));
        Assert.Equal(2, upstream.Attempts);

        // TryTake is the teardown claim: atomic, exactly one winner.
        Assert.True(slot.TryTake(out var taken));
        Assert.Same(held, taken);
        Assert.False(slot.TryTake(out _));
        Assert.False(slot.TryGet(out _));
    }
}
