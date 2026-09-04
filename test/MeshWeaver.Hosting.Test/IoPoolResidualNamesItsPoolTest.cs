using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A teardown residual has to say WHICH pool did not finish, through a channel that still works
/// during teardown (#2578, #2616).
///
/// <para><b>The bug this pins.</b> <see cref="IoPoolRegistry.DrainAll()"/> returned a bare total,
/// and named the offending pool only in an <c>ILogger</c> warning. But <c>DrainAll</c> runs after
/// <c>Mesh.Dispose()</c>, and the mesh's log sink has stopped capturing by then — measured on the
/// #2616 shard-2 trx, of <b>294</b> <c>Mesh.Dispose() invoking</c> windows, <b>zero</b> carry an
/// ILogger line at any level, while the same trx holds 84 <c>[Warning] [SynchronizationStream]</c>
/// records from before dispose. So the one diagnostic written to name the pool was structurally
/// invisible in exactly the window it existed for.</para>
///
/// <para>The visible consequence: two occurrences of the same drain flake (#2578 on 2026-08-28
/// 07:46Z, #2616 at 12:32Z the same day) both reported
/// <c>DISPOSE_DONE teardown DIRTY — 1 pooled I/O leaf(s) still running</c> and nothing more. An
/// anonymous <c>1</c> is not actionable: <c>Query=1</c> and <c>Compile=1</c> are different bugs
/// with different owners, and <see cref="IoPool.Drain"/> can produce a residual from three
/// distinct causes. The residual therefore travels back as a RETURN VALUE, so the caller can put
/// it where it survives — <c>TestPhaseTrace</c> in the test base, <c>TeardownReport</c> in
/// production.</para>
/// </summary>
public class IoPoolResidualNamesItsPoolTest
{
    /// <summary>
    /// 🚨 The regression this file exists for. A leaf that ignores its cancellation token must be
    /// reported AS BELONGING TO ITS POOL, not merely counted.
    ///
    /// <para>🚨 <b>Fast on purpose.</b> A residual is by definition a leaf that outlives
    /// <c>IoPool.Drain</c>'s budget, so the only way to observe one is to let the budget EXPIRE —
    /// and <c>Drain</c> spends it three times over (the cancel join, each gate slot, the
    /// blocking-idle join). At the production 30 s that is 30-90 s of shard time per run, and it
    /// cannot pass at all under <c>test/xunit.runner.json</c>'s <c>methodTimeout: 30000</c>: the
    /// first version of this test was killed at exactly 30 s with no assertion message, and its
    /// <c>release.Set()</c> — sitting after an <c>await</c> of the method-timeout token — never
    /// ran, so the leaf kept a pool thread blocked into the next test.</para>
    ///
    /// <para>The subject here is <b>that the residual is reported and NAMES its pool</b>, not the
    /// numeric budget, so the pool is built with a millisecond budget and the 30 s default is
    /// pinned separately by <see cref="TheDefaultDrainBudget_IsTheProductionContract"/>.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task DrainAll_reportsTheResidual_againstTheNameOfThePoolThatLeaked()
    {
        // A millisecond budget: the SAME code path through the SAME DrainAll, three orders of
        // magnitude less waiting. The subject is the residual naming its pool, not the 30 s value.
        using var registry = new IoPoolRegistry(
            new IoPoolOptions { DrainTimeout = TimeSpan.FromMilliseconds(250), DrainGrace = TimeSpan.FromMilliseconds(100) });
        var pool = registry.Get(IoPoolNames.Query);

        // 🚨 NO HAND-WOVEN GATE AND NO BLOCKING BRIDGE. The first version of this used
        // ManualResetEventSlim (a hand-woven gate — /async bans it); the second traded that for
        // AsyncSubject + .Wait(), which is an observable→blocking bridge and trips
        // BlockingBridgeInTestRatchetGuard. Satisfying one guard by violating another is not a fix.
        // What the test actually needs is: (a) know the leaf started, (b) have it outlive the drain
        // budget. Neither needs a signal back INTO the leaf.
        var entered = new AsyncSubject<Unit>();

        pool.InvokeBlocking(_ =>
        {
            entered.OnNext(Unit.Default);
            entered.OnCompleted();
            // The SUBJECT of the test: a leaf that ignores its cancellation token. Sleeping IS
            // ignoring it — and with a 250 ms drain budget this outlives it four times over while
            // costing the suite nothing, because the drain returns without waiting for the leaf.
            Thread.Sleep(TimeSpan.FromSeconds(1));
            return 0;
        }).Subscribe(_ => { }, _ => { });

        // The precondition, through the sanctioned test bridge — no .Wait(), no gate.
        await entered.Timeout(TimeSpan.FromSeconds(10)).Await(TestContext.Current.CancellationToken);

        var total = registry.DrainAll(out var byPool);

        total.Should().BeGreaterThan(0,
            "the leaf ignored its token and outlived the budget — that is a residual");

        byPool.Should().ContainSingle(
            "exactly one pool leaked, and the caller needs to know WHICH — a bare total is what "
            + "made #2578 and #2616 unactionable twice over");
        byPool[0].Pool.Should().Be(IoPoolNames.Query,
            "the residual must carry the leaking pool's NAME: Query=1 and Compile=1 are different "
            + "bugs with different owners");
        byPool[0].Residual.Should().BeGreaterThan(0);
        // The wire format the teardown traces embed, so it is part of the contract: the pool's
        // name and count first — and, since #2770, the leaf that is still running, in brackets.
        // A bare `Query=1` sent the reader into every Query-pool caller in the process; the site
        // is the difference between "some query leaked" and the method that owns it.
        var wire = byPool[0].ToString();
        wire.Should().StartWith($"{IoPoolNames.Query}={byPool[0].Residual} [",
            "the residual leads with the pool's name and count, exactly as before #2770");
        wire.Should().Contain(nameof(IoPoolResidualNamesItsPoolTest),
            "the bracketed site names the leaf's enclosing method, and the leaf that leaked is "
            + "the lambda THIS test handed to the pool — a residual naming any other site would "
            + "be pointing the reader at somebody else's code");
        byPool[0].Sites.Should().ContainSingle(
            "one leaf leaked, so one site; a duplicate here would mean the same delegate was "
            + "counted twice");
    }

    /// <summary>
    /// The budget above is a TEST value; this pins the production one, so making it injectable
    /// cannot quietly change the teardown contract (#2578/#2616 both turn on 30 s being the
    /// window in which a leaf must unwind).
    /// </summary>
    [Fact]
    public void TheDefaultDrainGrace_IsTheProductionContract()
        => new IoPoolOptions().DrainGrace.Should().Be(TimeSpan.FromSeconds(8),
            "the grace a leaf gets to finish on its own before teardown cancels it matches the hub "
            + "disposal stall budget, so 'no progress' means the same thing at every layer");

    [Fact]
    public void TheDefaultDrainBudget_IsTheProductionContract()
        => new IoPoolOptions().DrainTimeout.Should().Be(TimeSpan.FromSeconds(30),
            "30 s is the teardown contract — a test may shorten its OWN pool's budget, but the "
            + "default must not drift");

    /// <summary>
    /// The other direction: a clean drain must report NOTHING, so a reader can trust the absence
    /// of a pool name. A diagnostic that names a pool on every healthy teardown would be ignored
    /// within a day.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void DrainAll_reportsNoPool_whenEveryLeafUnwound()
    {
        using var registry = new IoPoolRegistry();
        var pool = registry.Get(IoPoolNames.Query);

        pool.InvokeBlocking(_ => 0).Subscribe(_ => { }, _ => { });

        var total = registry.DrainAll(out var byPool);

        total.Should().Be(0, "the leaf finished — the join is real");
        byPool.Should().BeEmpty("a clean drain names no pool");
    }
}
