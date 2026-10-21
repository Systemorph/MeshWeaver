using System;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A kernel script used to EXECUTE — and its session to WARM UP — as async leaves of the shared
/// <see cref="IoPoolNames.Compile"/> pool, so a running cell held one of that pool's async slots for
/// its whole run (default: one per processor). The pool's other async leaves are exactly what a
/// script may wait on: ANOTHER kernel session's warm-up and execution (a cell that runs a cell) and
/// the language service's workspace compiles. With every slot held by a script waiting on such a
/// leaf, none is ever admitted — the nested-gate deadlock — and every Activity involved stays
/// Running for ever with nothing logged. The fix moves both waits to their own lane,
/// <see cref="IoPoolNames.KernelScript"/>.
///
/// <para>The mesh here has a Compile pool of ONE slot, so one cell is the whole pool. Both tests stay
/// Running (and fail) on the code before the change; both complete after it.</para>
///
/// <para>Measured while writing this: the pool's BLOCKING leaves (<c>InvokeBlocking</c> — prebuilt
/// adoption's MVID read, cell-surface assembly loads) are admitted by a separate scheduler, not the
/// async gate, so a script holding an async slot does NOT starve them. A first version of these tests
/// waited on a blocking leaf and passed on the unfixed code; that is why both now use async leaves.</para>
/// </summary>
public class KernelScriptHoldsNoCompileSlotTest(ITestOutputHelper output) : CSharpCellRunTestBase(output)
{
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IoPoolOptions>();
                return services.AddSingleton(new IoPoolOptions { Compile = 1 });
            });

    private IoPool CompilePool()
    {
        var compile = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>().Get(IoPoolNames.Compile);
        var pool = compile.Should().BeOfType<IoPool>().Which;
        pool.MaxConcurrency.Should().Be(1,
            "the mesh must run with a ONE-slot Compile pool, or one held slot saturates nothing");
        return pool;
    }

    /// <summary>
    /// The deadlock, reproduced end to end: a cell that RUNS ANOTHER CELL and waits for it. The
    /// inner cell's kernel session needs the Compile pool's async gate to warm up and execute; on the
    /// code before the change the outer cell held the only slot across that wait, so neither ever
    /// finished.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ACellThatRunsAnotherCellAndWaitsForIt_Completes()
    {
        var ct = TestContext.Current.CancellationToken;
        CompilePool();
        var inner = await CreateCell("40 + 2", ct);
        var outer = await CreateCell(RunsAndWaitsFor(inner), ct);

        var activity = await Run(outer, ct);
        var log = await Terminal(activity,
            "the outer cell held the only Compile slot while the inner cell's kernel needed it — the nested-gate deadlock", ct);

        log.Status.Should().Be(ActivityStatus.Succeeded,
            "the inner cell's kernel must be able to run while the outer cell waits for it");
        log.ReturnValue?.ToString().Should().Be("inner Succeeded",
            "the outer cell's own result proves the inner run finished");
    }

    /// <summary>
    /// The Compile pool's async gate saturated from OUTSIDE — its only slot held by other work, as a
    /// language-service compile does — and a plain cell still completes: running a cell does not
    /// queue behind compile work.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ACellRunsWhileTheCompilePoolIsSaturated()
    {
        var ct = TestContext.Current.CancellationToken;
        var compile = CompilePool();
        var parked = 0;
        var released = 0;
        // An ASYNC leaf, so it holds the async gate (a blocking leaf would not — see the class doc).
        var holder = compile
            .Invoke(_ =>
            {
                Volatile.Write(ref parked, 1);
                SpinWait.SpinUntil(() => Volatile.Read(ref released) == 1, Bound * 2);
                return Task.FromResult(0);
            })
            .Subscribe(_ => { }, _ => { });
        try
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref parked) == 1, Bound).Should().BeTrue(
                "the holder must own the Compile pool's only slot before the cell runs, or the test proves nothing");

            var cell = await CreateCell("1 + 1", ct);
            var activity = await Run(cell, ct);

            var log = await Terminal(activity,
                "the cell needed the Compile pool's only slot while other work held it", ct);

            log.Status.Should().Be(ActivityStatus.Succeeded,
                "a cell's warm-up and execution hold no Compile slot, so a saturated Compile pool cannot stall it");
        }
        finally
        {
            Volatile.Write(ref released, 1);
            holder.Dispose();
        }
    }
}
