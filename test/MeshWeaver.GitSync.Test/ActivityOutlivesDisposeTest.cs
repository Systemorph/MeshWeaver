using System;
using System.Reactive;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// An activity must never outlive the mesh that started it.
///
/// <para>The compile that crashed CI on 2026-08-04 (FutuRe.Test exit=139, ~1 run in 5) is one
/// instance of this, but the invariant is general and does not need Roslyn to state: work running
/// inside an activity is work the mesh owns, so disposal must not return while it is still in
/// flight. If it does, the work keeps running against a torn-down mesh — and when that work is
/// emitting or loading a collectible assembly, the ALC unloads underneath it and the process dies
/// on a bare SIGSEGV with no managed exception.</para>
///
/// <para>Why an activity is the right lifecycle handle: it is a normal hub running inside the
/// message-hub grain, and its ActivityLog reaching a terminal status (Succeeded OR Failed) is the
/// documented signal that it has finished and the hub is free to deactivate
/// (see CompileFinishAndDisposeTest). Nothing else covers it — the activity command deliberately
/// runs OFF the hub turn (ScheduleOffHubTurn), so it holds no grain lock, and a compile
/// additionally leaves the drainable I/O pool for a dedicated thread, so the pool's
/// "teardown cancels + joins in-flight work" guarantee does not reach it either.</para>
///
/// <para>🚨 The fix this drives must be CANCEL-THEN-JOIN and must NOT block the action block: the
/// activity's own Append/Finish writes go back through the hub, so a naive blocking join deadlocks
/// against the work it is waiting for — the same class as holding the grain turn across
/// <c>ctx.Log</c>.</para>
/// </summary>
public class ActivityOutlivesDisposeTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    /// <summary>
    /// A deliberately slow activity — a plain delay, no compile, no I/O — started and NOT awaited.
    /// Disposal must observe it to terminal before returning.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task DisposingTheMesh_WaitsForARunningActivity()
    {
        var space = "GhDispose" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Dispose-race space");

        // Reactive signals, not Task primitives: the command entering, and the command finishing.
        var entered = new AsyncSubject<Unit>();
        var completed = 0;

        // Start the activity and DO NOT await it. The trigger returns as soon as the activity
        // exists — by design — so this is precisely the window under test.
        Mesh.RunActivity(space, ActivityCategory.Import, "Slow probe",
                ctx =>
                {
                    entered.OnNext(Unit.Default);
                    entered.OnCompleted();
                    return Observable.Timer(TimeSpan.FromSeconds(5))
                        .Do(_ => Interlocked.Exchange(ref completed, 1))
                        .Select(_ => Unit.Default);
                })
            .Subscribe(_ => { }, ex => Output.WriteLine($"activity faulted: {ex.Message}"));

        // Wait for the command to be genuinely running, or the test proves nothing. Reactive
        // assertion — FirstAsync() blocks, so it has no place even here.
        await entered.Should().Within(TimeSpan.FromSeconds(30)).Emit();

        // The REAL teardown path. A bare Mesh.Dispose() runs only the synchronous half; the
        // documented contract is TeardownAsync — await DisposalCompleted, DrainAll() the pools,
        // then quiesce the AsyncDisposeQueue.
        var started = DateTimeOffset.UtcNow;
        await Mesh.TeardownAsync(TimeSpan.FromSeconds(60));
        var elapsed = DateTimeOffset.UtcNow - started;
        Output.WriteLine($"TeardownAsync returned after {elapsed.TotalMilliseconds:F0}ms; "
                         + $"command completed={Volatile.Read(ref completed) == 1}");

        // THE CONTRACT: teardown quiesces — it stops new work and waits for what is running to
        // finish. It must not return while an activity command is still live: that work keeps
        // executing against a torn-down mesh, and when it touches a collectible ALC's metadata
        // after unload the process dies on a bare signal with no managed exception.
        Volatile.Read(ref completed).Should().Be(1,
            $"TeardownAsync returned after {elapsed.TotalMilliseconds:F0}ms while the activity "
            + "command was still running. Activity work is mesh-owned: teardown must wait for it, "
            + "never walk away from it.");
    }
}
