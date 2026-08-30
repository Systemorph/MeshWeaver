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
    public void DrainAll_reportsTheResidual_againstTheNameOfThePoolThatLeaked()
    {
        // A millisecond budget: the SAME code path through the SAME DrainAll, three orders of
        // magnitude less waiting. The subject is the residual naming its pool, not the 30 s value.
        using var registry = new IoPoolRegistry(
            new IoPoolOptions { DrainTimeout = TimeSpan.FromMilliseconds(250) });
        var pool = registry.Get(IoPoolNames.Query);

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        pool.InvokeBlocking(_ =>
        {
            entered.Set();
            // Deliberately ignores the token and outlives IoPool's drain budget — that IS the
            // condition a residual reports.
            release.Wait(TimeSpan.FromMinutes(2));
            return 0;
        }).Subscribe(_ => { }, _ => { });

        entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)
            .Should().BeTrue("the leaf must actually be running before the drain starts");

        // 🚨 release.Set() is UNCONDITIONAL. It was previously the statement after this await, so
        // when the await threw (its token IS the method-timeout token) the blocked leaf was never
        // freed: it held a pool thread for its full two minutes and `using` disposed over live
        // work — the residual escaping from the test written to prove residuals are reported.
        var total = 0;
        IReadOnlyList<IoPoolRegistry.PoolResidual> byPool = [];
        try
        {
            total = registry.DrainAll(out byPool);
        }
        finally
        {
            release.Set();
        }

        total.Should().BeGreaterThan(0,
            "the leaf ignored its token and outlived the budget — that is a residual");

        byPool.Should().ContainSingle(
            "exactly one pool leaked, and the caller needs to know WHICH — a bare total is what "
            + "made #2578 and #2616 unactionable twice over");
        byPool[0].Pool.Should().Be(IoPoolNames.Query,
            "the residual must carry the leaking pool's NAME: Query=1 and Compile=1 are different "
            + "bugs with different owners");
        byPool[0].Residual.Should().BeGreaterThan(0);
        byPool[0].ToString().Should().Be($"{IoPoolNames.Query}={byPool[0].Residual}",
            "this is the wire format the teardown traces embed, so it is part of the contract");
    }

    /// <summary>
    /// The budget above is a TEST value; this pins the production one, so making it injectable
    /// cannot quietly change the teardown contract (#2578/#2616 both turn on 30 s being the
    /// window in which a leaf must unwind).
    /// </summary>
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
