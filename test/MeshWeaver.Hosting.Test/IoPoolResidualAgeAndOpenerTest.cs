using System.Diagnostics;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A teardown residual says not only WHICH leaf did not unwind, but HOW LONG it had been in flight,
/// WHO opened it and WHICH thread it started on (#7277).
///
/// <para><b>Why.</b> The silo teardown on memex reported <c>Query=1 [Using`2[…MeshQuery+QueryMembers]]</c>:
/// a synchronous <see cref="IIoPool.SubscribeThroughPool{T}"/> subscribe still running 30 s after the
/// pool was cancelled. That site is the ONE fan-out type every query in the process subscribes
/// through, so it named no caller; and with no age it could not tell a subscribe that had hung long
/// before the stop from one that was slow to unwind during it. This is an INSTRUMENT for the next
/// occurrence, not a fix of the leaf.</para>
///
/// <para>The leaf here is that exact shape: a source whose <c>Subscribe</c> blocks synchronously, so
/// no cancellation token can reach it. It is held by a volatile int released in a <c>finally</c> — no
/// sleep, no hand-woven gate.</para>
/// </summary>
public class IoPoolResidualAgeAndOpenerTest
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(20);

    private static readonly Regex InFlight = new(
        @" — in flight (?<secs>\d+(\.\d+)?) s, opened by (?<opener>.+), started on thread (?<thread>\d+)$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// With <see cref="IoPoolOptions.CaptureLeafOpeners"/> on, the residual names the caller that
    /// subscribed (this test method), an age bounded by what the test measured, and the thread the
    /// blocked subscribe is running on.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AResidual_NamesItsAge_ItsOpener_AndItsThread()
    {
        var (entry, drained, upperBound, leafThread) = await DrainOverABlockedSubscribe(captureOpeners: true, reportCulture: null, TestContext.Current.CancellationToken);

        var m = InFlight.Match(entry);
        m.Success.Should().BeTrue($"the residual must read '… in flight N s, opened by X, started on thread T', got: {entry}");
        entry.Should().StartWith("System.Reactive.Linq.",
            "the site still leads — the instrument adds to it, it does not replace it");

        var secs = double.Parse(m.Groups["secs"].Value, CultureInfo.InvariantCulture);
        secs.Should().BeGreaterThanOrEqualTo(drained.TotalSeconds - 0.1,
            "the leaf was in flight for at least the whole drain that reported it (0.1 s = the format's rounding)");
        secs.Should().BeLessThanOrEqualTo(upperBound.TotalSeconds + 0.1,
            "a leaf cannot have been in flight longer than since the test subscribed it");

        m.Groups["opener"].Value.Should().Contain(nameof(DrainOverABlockedSubscribe),
            "the opener is the code that SUBSCRIBED, captured on its own thread — the subscribe path "
            + "otherwise names only the source's type, which every query shares");
        m.Groups["opener"].Value.Should().NotContain("System.Reactive",
            "Rx's own frames say nothing about who asked and are skipped");

        int.Parse(m.Groups["thread"].Value, CultureInfo.InvariantCulture).Should().Be(leafThread,
            "for a synchronous leaf the thread it started on is the one it is blocked on — the thread a dump shows");
    }

    /// <summary>
    /// Negative control: with the switch OFF (the production default) the age and the thread are
    /// still reported, and the opener says plainly that it was not captured — it never names a caller.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task WithCaptureOff_TheOpenerSaysItWasNotCaptured()
    {
        var (entry, _, _, leafThread) = await DrainOverABlockedSubscribe(captureOpeners: false, reportCulture: null, TestContext.Current.CancellationToken);

        var m = InFlight.Match(entry);
        m.Success.Should().BeTrue($"age and thread are always recorded, got: {entry}");
        m.Groups["opener"].Value.Should().Be(IoPool.UncapturedOpener);
        m.Groups["opener"].Value.Should().NotContain(nameof(DrainOverABlockedSubscribe));
        int.Parse(m.Groups["thread"].Value, CultureInfo.InvariantCulture).Should().Be(leafThread);
    }

    /// <summary>
    /// The age is formatted INVARIANTLY: read on a de-DE thread the report still says
    /// <c>in flight 0.4 s</c>, never <c>0,4 s</c> — a diagnostic is parsed by tools and read in every
    /// locale, and AGENTS.md forbids formatting off the current culture.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheAge_IsFormattedInvariantly_UnderAGermanCulture()
    {
        var german = CultureInfo.GetCultureInfo("de-DE");
        // Negative control: the culture really does change this format — so a dot below is the
        // invariant formatting at work, not a culture that happens to use one.
        (0.4).ToString("0.0", german).Should().Be("0,4");

        var (entry, _, _, _) = await DrainOverABlockedSubscribe(captureOpeners: false, reportCulture: german, TestContext.Current.CancellationToken);

        entry.Should().MatchRegex(@" — in flight \d+\.\d s, ", $"the age uses '.' whatever the reader's culture, got: {entry}");
        Regex.IsMatch(entry, @" — in flight \d+,\d s, ").Should().BeFalse("no culture decimal comma may leak into the age");
    }

    /// <summary>
    /// An ASYNC leaf that suspends before it blocks is reported with the thread it STARTED on — the
    /// await may have resumed it elsewhere, so the label says "started on", never "parked on".
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnAsyncLeafThatSuspended_IsReportedWithItsStartingThread()
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions
        {
            DrainTimeout = TimeSpan.FromMilliseconds(250),
            DrainGrace = TimeSpan.FromMilliseconds(100),
        });
        var pool = registry.Get(IoPoolNames.Http);

        var release = 0;
        var startThread = 0;
        var entered = new AsyncSubject<Unit>();
        pool.Invoke(async _ =>
        {
            Volatile.Write(ref startThread, Environment.CurrentManagedThreadId);
            await Task.Yield();   // suspends: the rest may run on any ThreadPool thread
            entered.OnNext(Unit.Default);
            entered.OnCompleted();
            SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, Hold);
            return 0;
        }).Subscribe(_ => { }, _ => { });

        try
        {
            await entered.Timeout(TimeSpan.FromSeconds(10)).Await(TestContext.Current.CancellationToken);
            registry.DrainAll(out var byPool).Should().Be(1, "the leaf ignores its token past the budget");
            var entry = byPool.Should().ContainSingle().Which.Sites.Should().ContainSingle().Which;
            var m = InFlight.Match(entry);
            m.Success.Should().BeTrue($"got: {entry}");
            int.Parse(m.Groups["thread"].Value, CultureInfo.InvariantCulture).Should().Be(Volatile.Read(ref startThread),
                "the recorded thread is the one the leaf STARTED on, which is all the pool can know of an async leaf");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>Negative control: a subscribe that returned leaves nothing to report.</summary>
    [Fact(Timeout = 30_000)]
    public async Task ASubscribeThatReturned_LeavesNoResidual()
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions
        {
            DrainTimeout = TimeSpan.FromMilliseconds(250),
            DrainGrace = TimeSpan.FromMilliseconds(100),
            CaptureLeafOpeners = true,
        });
        var pool = registry.Get(IoPoolNames.Query);

        await pool.SubscribeThroughPool(Observable.Return(1)).FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        registry.DrainAll(out var byPool).Should().Be(0);
        byPool.Should().BeEmpty("nothing is in flight, so there is nothing to name");
    }

    private static async Task<(string Entry, TimeSpan Drained, TimeSpan UpperBound, int LeafThread)>
        DrainOverABlockedSubscribe(bool captureOpeners, CultureInfo? reportCulture, CancellationToken ct)
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions
        {
            // Millisecond budgets: the subject is what the residual SAYS, not the 30 s value.
            DrainTimeout = TimeSpan.FromMilliseconds(250),
            DrainGrace = TimeSpan.FromMilliseconds(100),
            CaptureLeafOpeners = captureOpeners,
        });
        var pool = registry.Get(IoPoolNames.Query);

        var release = 0;
        var leafThread = 0;
        var entered = new AsyncSubject<Unit>();
        // The #7277 shape: a source whose SUBSCRIBE blocks — no token can reach it.
        var blockingSource = Observable.Create<int>(_ =>
        {
            Volatile.Write(ref leafThread, Environment.CurrentManagedThreadId);
            entered.OnNext(Unit.Default);
            entered.OnCompleted();
            SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, Hold);
            return System.Reactive.Disposables.Disposable.Empty;
        });

        var sinceSubscribe = Stopwatch.StartNew();
        try
        {
            pool.SubscribeThroughPool(blockingSource).Subscribe(_ => { }, _ => { });
            await entered.Timeout(TimeSpan.FromSeconds(10)).Await(ct);

            var drainWatch = Stopwatch.StartNew();
            // The report is formatted on the thread that reads it — DrainAll's caller — so that is
            // the thread whose culture a locale test sets.
            var callerCulture = CultureInfo.CurrentCulture;
            if (reportCulture is not null)
                CultureInfo.CurrentCulture = reportCulture;
            int total;
            IReadOnlyList<IoPoolRegistry.PoolResidual> byPool;
            try { total = registry.DrainAll(out byPool); }
            finally { CultureInfo.CurrentCulture = callerCulture; }
            var drained = drainWatch.Elapsed;
            var upperBound = sinceSubscribe.Elapsed;

            total.Should().Be(1, "one subscribe is blocked past the budget");
            byPool.Should().ContainSingle().Which.Pool.Should().Be(IoPoolNames.Query);
            var entry = byPool[0].Sites.Should().ContainSingle("one leaf is in flight, so one entry").Which;
            return (entry, drained, upperBound, Volatile.Read(ref leafThread));
        }
        finally
        {
            // In a finally: a failing assertion must not strand the blocked subscribe on a pool thread.
            Volatile.Write(ref release, 1);
        }
    }
}
