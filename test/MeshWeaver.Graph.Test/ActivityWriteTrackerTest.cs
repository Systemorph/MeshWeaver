using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The in-flight register that makes detached activity writes visible to shutdown.
///
/// <para>What is actually being pinned: a write that is still running when the hub disposes must
/// DELAY that disposal rather than be killed by it — and a write that will never finish must not
/// hold shutdown open. Both halves matter; a drain with no timeout trades a lost write for a hung
/// silo, which is worse.</para>
/// </summary>
public class ActivityWriteTrackerTest
{
    /// <summary>An idle hub must not pay for this — the overwhelmingly common shutdown.</summary>
    [Fact]
    public async Task Drain_WithNothingInFlight_CompletesImmediately()
    {
        var tracker = new ActivityWriteTracker();

        var completed = await tracker.Drain().Timeout(TimeSpan.FromSeconds(2)).FirstAsync();

        completed.Should().Be(System.Reactive.Unit.Default);
        tracker.Count.Should().Be(0);
    }

    /// <summary>
    /// 🚨 THE POINT. While a write is outstanding the drain must NOT complete — that is the whole
    /// mechanism by which hub disposal, and therefore the grain's bounded wait, accounts for it.
    /// </summary>
    [Fact]
    public async Task Drain_WaitsWhileAWriteIsInFlight_AndCompletesWhenItEnds()
    {
        var tracker = new ActivityWriteTracker();
        var write = tracker.Begin("alice/_UserActivity/Doc_Page");
        tracker.Count.Should().Be(1);

        var drain = tracker.Drain().FirstAsync().ToTask();

        // Still running: the drain must be pending, not completed.
        var early = await Task.WhenAny(drain, Task.Delay(300));
        early.Should().NotBe(drain, "the drain must wait while a write is still in flight");

        write.Dispose();                        // the write finishes

        await drain.WaitAsync(TimeSpan.FromSeconds(3));
        tracker.Count.Should().Be(0);
    }

    /// <summary>
    /// A write that never ends must NOT hold shutdown open forever. The grain gives hub disposal
    /// 5 s before "moving on", so overrunning here would only rob the other dispose actions of
    /// their share of that window and surface as a disposal hang instead of a lost write.
    /// </summary>
    [Fact]
    public async Task Drain_TimesOutRatherThanBlockingShutdownForever()
    {
        var tracker = new ActivityWriteTracker();
        _ = tracker.Begin("alice/_UserActivity/Stuck");   // never released

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await tracker.Drain()
            .Timeout(ActivityWriteTracker.DrainTimeout + TimeSpan.FromSeconds(5))
            .FirstAsync();
        sw.Stop();

        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1),
            "it must actually have waited for the write, not returned instantly");
        sw.Elapsed.Should().BeLessThan(ActivityWriteTracker.DrainTimeout + TimeSpan.FromSeconds(3),
            "…and it must give up on its own rather than blocking the silo");
    }

    /// <summary>
    /// The budget must sit UNDER the grain's 5 s hub-disposal window
    /// (<c>MessageHubGrain.OnDeactivateAsync</c>), or the grain cuts the drain off anyway and the
    /// other registered dispose actions lose their share of it.
    /// </summary>
    [Fact]
    public void DrainTimeout_IsUnderTheGrainsDisposalBudget() =>
        ActivityWriteTracker.DrainTimeout.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "the grain gives hub disposal 5 s total before moving on");

    /// <summary>
    /// Concurrent tracks for the SAME path are the documented create-vs-update race. Releasing one
    /// must not report the path drained while the other is still writing.
    /// </summary>
    [Fact]
    public async Task ConcurrentWritesToTheSamePath_BothMustFinishBeforeDraining()
    {
        var tracker = new ActivityWriteTracker();
        var first = tracker.Begin("alice/_UserActivity/Same");
        var second = tracker.Begin("alice/_UserActivity/Same");

        var drain = tracker.Drain().FirstAsync().ToTask();
        first.Dispose();

        var early = await Task.WhenAny(drain, Task.Delay(300));
        early.Should().NotBe(drain,
            "one of two concurrent writes to the same path finished — the other is still running");

        second.Dispose();
        await drain.WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Rx may dispose a subscription more than once. A double release would under-count and let a
    /// drain report clear while a write is still running.
    /// </summary>
    [Fact]
    public async Task ReleasingTwice_IsIdempotent()
    {
        var tracker = new ActivityWriteTracker();
        var a = tracker.Begin("alice/_UserActivity/A");
        var b = tracker.Begin("alice/_UserActivity/B");

        a.Dispose();
        a.Dispose();                               // the double release

        tracker.Count.Should().Be(1, "B is still in flight — the repeat release must not remove it");

        b.Dispose();
        await tracker.Drain().Timeout(TimeSpan.FromSeconds(3)).FirstAsync();
    }

    /// <summary>
    /// A drain overrun must NAME what it abandoned. A lost activity write is otherwise invisible:
    /// the request it belonged to completed successfully long before.
    /// </summary>
    [Fact]
    public void InFlight_NamesThePathsSoAnOverrunIsDiagnosable()
    {
        var tracker = new ActivityWriteTracker();
        _ = tracker.Begin("alice/_UserActivity/Doc_One");
        _ = tracker.Begin("bob/_UserActivity/Doc_Two");

        tracker.InFlight.Should().HaveCount(2);
        tracker.InFlight.Should().Contain("alice/_UserActivity/Doc_One");
        tracker.InFlight.Should().Contain("bob/_UserActivity/Doc_Two");
    }

    /// <summary>An empty or whitespace path is a caller bug, not something to silently register.</summary>
    [Fact]
    public void Begin_RejectsAnEmptyPath()
    {
        var tracker = new ActivityWriteTracker();
        Assert.Throws<ArgumentException>(() => tracker.Begin("  "));
    }

    /// <summary>
    /// Begin and Release are concurrent by construction — Begin runs on whichever hub handled the
    /// request, Release on the detached pipeline's thread — and an Rx subject is not thread-safe
    /// across concurrent OnNext. Hammer both sides and require the count to land exactly at zero:
    /// a torn or reordered notification stream shows up here as a non-zero residue or a throw.
    /// </summary>
    [Fact]
    public async Task BeginAndRelease_AreSafeUnderConcurrency()
    {
        var tracker = new ActivityWriteTracker();
        const int writers = 16, perWriter = 60;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                // Deliberately overlapping paths: same-path concurrency is the real-world race.
                var token = tracker.Begin($"user{w % 4}/_UserActivity/Doc_{i % 3}");
                token.Dispose();
            }
        })));

        tracker.Count.Should().Be(0, "every write released — a torn subject would leave a residue");
        await tracker.Drain().Timeout(TimeSpan.FromSeconds(3)).FirstAsync();
    }

    /// <summary>
    /// 🚨 The #993 signal. A caller that has just triggered a detached write needs "THIS write
    /// finished" — and must get it without polling an eventually-consistent query for the node the
    /// write produces. WhenSettled completes on the ENTER→LEAVE transition of that one path.
    /// </summary>
    [Fact]
    public async Task WhenSettled_CompletesWhenThatPathsWriteEndsAndNotBefore()
    {
        var tracker = new ActivityWriteTracker();
        // Subscribed BEFORE the write starts — the documented usage.
        var settled = tracker.WhenSettled("alice/_UserActivity/Doc_Page").FirstAsync().ToTask();

        var early = await Task.WhenAny(settled, Task.Delay(200));
        early.Should().NotBe(settled, "the write has not even started — completing here is the false pass");

        var write = tracker.Begin("alice/_UserActivity/Doc_Page");

        var stillRunning = await Task.WhenAny(settled, Task.Delay(200));
        stillRunning.Should().NotBe(settled, "the write is in flight — settling now would be a lie");

        write.Dispose();

        await settled.WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// The exact trap that makes <see cref="ActivityWriteTracker.Drain"/> unusable as a
    /// "did my write finish" signal: Drain completes IMMEDIATELY when nothing is in flight, so a
    /// caller who subscribes right after Post wins the race against the handler's Begin and gets a
    /// deterministic FALSE PASS. WhenSettled must not have that property.
    /// </summary>
    [Fact]
    public async Task WhenSettled_DoesNotCompleteOnAnIdleTracker_WhereDrainWould()
    {
        var tracker = new ActivityWriteTracker();

        // Drain: completes at once on an idle tracker — correct for shutdown, wrong as a write signal.
        await tracker.Drain().Timeout(TimeSpan.FromSeconds(2)).FirstAsync();

        var settled = tracker.WhenSettled("alice/_UserActivity/NeverStarted").FirstAsync().ToTask();
        var raced = await Task.WhenAny(settled, Task.Delay(300));
        raced.Should().NotBe(settled,
            "an unstarted write must never report settled — that is the whole difference from Drain");
    }

    /// <summary>
    /// One user's write must not settle another's. The signal is per-path because the caller is
    /// waiting for the specific node it caused to be written.
    /// </summary>
    [Fact]
    public async Task WhenSettled_IgnoresOtherPaths()
    {
        var tracker = new ActivityWriteTracker();
        var settled = tracker.WhenSettled("alice/_UserActivity/Mine").FirstAsync().ToTask();

        var other = tracker.Begin("bob/_UserActivity/Theirs");
        other.Dispose();

        var raced = await Task.WhenAny(settled, Task.Delay(300));
        raced.Should().NotBe(settled, "bob's write says nothing about alice's");

        tracker.Begin("alice/_UserActivity/Mine").Dispose();
        await settled.WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Same-path concurrency is the documented create-vs-update race: the caller's write is not
    /// settled until the LAST outstanding write for that path has ended, or it would read a node
    /// another write is still mutating.
    /// </summary>
    [Fact]
    public async Task WhenSettled_WaitsForEveryConcurrentWriteToTheSamePath()
    {
        var tracker = new ActivityWriteTracker();
        var settled = tracker.WhenSettled("alice/_UserActivity/Same").FirstAsync().ToTask();

        var first = tracker.Begin("alice/_UserActivity/Same");
        var second = tracker.Begin("alice/_UserActivity/Same");
        first.Dispose();

        var raced = await Task.WhenAny(settled, Task.Delay(300));
        raced.Should().NotBe(settled, "the second write to the same path is still running");

        second.Dispose();
        await settled.WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>An empty or whitespace path is a caller bug, same contract as <c>Begin</c>.</summary>
    [Fact]
    public void WhenSettled_RejectsAnEmptyPath()
    {
        var tracker = new ActivityWriteTracker();
        Assert.Throws<ArgumentException>(() => tracker.WhenSettled(" "));
    }

    /// <summary>
    /// 🚨 THE #1036 SIGNAL, and the false pass it exists to kill. Five writes for one path that do
    /// NOT overlap — write i ends before write i+1 begins, which is what five independent posts can
    /// legitimately do. The one-argument overload completes after the FIRST of them: correct for its
    /// own contract ("my write finished"), and a silent false pass for a caller that is about to
    /// assert on the result of all five. The counted overload spans every cycle.
    ///
    /// <para>Both are subscribed HERE, at the same instant, so the difference is the signal's
    /// semantics and nothing else.</para>
    /// </summary>
    [Fact]
    public async Task WhenSettled_WithCount_SpansNonOverlappingWrites_WhereTheSingleWriteSignalStops()
    {
        const string path = "charlie/_UserActivity/charlie_doc";
        var tracker = new ActivityWriteTracker();

        var settledOnce = tracker.WhenSettled(path).FirstAsync().ToTask();
        var settledFive = tracker.WhenSettled(path, writes: 5).FirstAsync().ToTask();

        // Write 1, complete before write 2 starts — deliberately NO overlap.
        tracker.Begin(path).Dispose();

        // The one-write signal is already done. That is precisely the hole: a HaveCount assertion
        // running here would be checked against one write's worth of state, with four to come.
        await settledOnce.WaitAsync(TimeSpan.FromSeconds(3));

        for (var i = 2; i <= 4; i++)
        {
            tracker.Begin(path).Dispose();
            var early = await Task.WhenAny(settledFive, Task.Delay(100));
            early.Should().NotBe(settledFive,
                $"only {i} of 5 writes have run — completing here would hand the caller an incomplete set");
        }

        tracker.Begin(path).Dispose();               // the fifth
        await settledFive.WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// The other shape the same five posts can take: all five in flight at once. The per-path
    /// counter only returns to zero when the last one ends, so the counted signal must not fire on
    /// any of the intermediate releases either.
    /// </summary>
    [Fact]
    public async Task WhenSettled_WithCount_WaitsForTheLastOfFiveOverlappingWrites()
    {
        const string path = "charlie/_UserActivity/charlie_doc";
        var tracker = new ActivityWriteTracker();
        var settled = tracker.WhenSettled(path, writes: 5).FirstAsync().ToTask();

        var writes = Enumerable.Range(0, 5).Select(_ => tracker.Begin(path)).ToArray();
        tracker.Count.Should().Be(5);

        foreach (var write in writes[..4])
        {
            write.Dispose();
            var early = await Task.WhenAny(settled, Task.Delay(100));
            early.Should().NotBe(settled, "a write for this path is still running");
        }

        writes[4].Dispose();
        await settled.WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Overlap is not all-or-nothing: real posts interleave. Two writes overlap, then two more run
    /// one at a time. The count is over ENTER→LEAVE cycles, not over "times the path went quiet".
    /// </summary>
    [Fact]
    public async Task WhenSettled_WithCount_CountsWritesNotQuietPeriods()
    {
        const string path = "charlie/_UserActivity/charlie_doc";
        var tracker = new ActivityWriteTracker();
        var settled = tracker.WhenSettled(path, writes: 4).FirstAsync().ToTask();

        var first = tracker.Begin(path);
        var second = tracker.Begin(path);         // overlaps the first
        first.Dispose();
        second.Dispose();                          // path goes quiet at 2 of 4

        var early = await Task.WhenAny(settled, Task.Delay(200));
        early.Should().NotBe(settled,
            "the path went quiet, but only 2 of the 4 writes have run — quiet is not the criterion");

        tracker.Begin(path).Dispose();             // 3rd, on its own
        var stillShort = await Task.WhenAny(settled, Task.Delay(100));
        stillShort.Should().NotBe(settled, "3 of 4");

        tracker.Begin(path).Dispose();             // 4th
        await settled.WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// The <see cref="ActivityWriteTracker.Drain"/> trap, restated for the counted signal: writes
    /// that never started must never be counted. An idle tracker asked for five writes must sit
    /// there — a caller whose posts were dropped has to see a timeout, not a green assertion over
    /// an empty set.
    /// </summary>
    [Fact]
    public async Task WhenSettled_WithCount_NeverCompletesForWritesThatNeverStarted()
    {
        const string path = "charlie/_UserActivity/charlie_doc";
        var tracker = new ActivityWriteTracker();

        // Drain: completes at once on an idle tracker — correct for shutdown, wrong as a write signal.
        await tracker.Drain().Timeout(TimeSpan.FromSeconds(2)).FirstAsync();

        var settled = tracker.WhenSettled(path, writes: 5).FirstAsync().ToTask();

        var raced = await Task.WhenAny(settled, Task.Delay(300));
        raced.Should().NotBe(settled, "not one of the five writes has started");

        // And a partial run is still not a completion — four writes must not settle a five-write wait.
        for (var i = 0; i < 4; i++)
            tracker.Begin(path).Dispose();

        var partial = await Task.WhenAny(settled, Task.Delay(300));
        partial.Should().NotBe(settled,
            "4 of 5 writes ran — the fifth is still missing and its result is not in the set yet");
    }

    /// <summary>Another path's five writes say nothing about this one's, same as the single-write signal.</summary>
    [Fact]
    public async Task WhenSettled_WithCount_IgnoresOtherPaths()
    {
        var tracker = new ActivityWriteTracker();
        var settled = tracker.WhenSettled("alice/_UserActivity/Mine", writes: 2).FirstAsync().ToTask();

        for (var i = 0; i < 5; i++)
            tracker.Begin("bob/_UserActivity/Theirs").Dispose();

        var raced = await Task.WhenAny(settled, Task.Delay(300));
        raced.Should().NotBe(settled, "bob's five writes say nothing about alice's two");

        tracker.Begin("alice/_UserActivity/Mine").Dispose();
        tracker.Begin("alice/_UserActivity/Mine").Dispose();
        await settled.WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// A write already in flight when the subscription attaches is a real write for that path and
    /// counts — that is what makes <c>WhenSettled(path)</c> the <c>writes: 1</c> case rather than a
    /// separate mechanism. (Writes that already FINISHED cannot be counted and the wait then times
    /// out loudly; erring toward waiting is the whole design.)
    /// </summary>
    [Fact]
    public async Task WhenSettled_WithCount_CountsAWriteAlreadyInFlightAtSubscription()
    {
        const string path = "charlie/_UserActivity/charlie_doc";
        var tracker = new ActivityWriteTracker();

        var running = tracker.Begin(path);                    // started BEFORE the subscription
        var settled = tracker.WhenSettled(path, writes: 2).FirstAsync().ToTask();

        tracker.Begin(path).Dispose();                        // the second write
        var early = await Task.WhenAny(settled, Task.Delay(200));
        early.Should().NotBe(settled, "the write that was already running has not ended");

        running.Dispose();
        await settled.WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>Zero or negative writes is a caller bug — there is no such thing as settling after
    /// no writes (that is <see cref="ActivityWriteTracker.Drain"/>, and its idle-completion is
    /// exactly the false pass this overload exists to avoid).</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WhenSettled_WithCount_RejectsANonPositiveCount(int writes)
    {
        var tracker = new ActivityWriteTracker();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => tracker.WhenSettled("alice/_UserActivity/Doc", writes));
    }

    /// <summary>An empty or whitespace path is a caller bug, same contract as <c>Begin</c>.</summary>
    [Fact]
    public void WhenSettled_WithCount_RejectsAnEmptyPath()
    {
        var tracker = new ActivityWriteTracker();
        Assert.Throws<ArgumentException>(() => tracker.WhenSettled(" ", writes: 3));
    }
}
