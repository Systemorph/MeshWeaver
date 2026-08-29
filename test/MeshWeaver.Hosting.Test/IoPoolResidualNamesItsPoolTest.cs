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
    /// <para>Deliberately slow (~30 s): a residual is by definition a leaf that outlives
    /// <c>IoPool.Drain</c>'s budget, so the only way to observe one is to let the budget expire.
    /// That is the cost of pinning the diagnostic rather than trusting it — and the budget is a
    /// contract, so shortening it for the test would be testing something else.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task DrainAll_reportsTheResidual_againstTheNameOfThePoolThatLeaked()
    {
        using var registry = new IoPoolRegistry();
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

        var total = 0;
        IReadOnlyList<IoPoolRegistry.PoolResidual> byPool = [];
        var drain = Task.Run(() => total = registry.DrainAll(out byPool),
            TestContext.Current.CancellationToken);

        await drain.WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);
        release.Set();

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
