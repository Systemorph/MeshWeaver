using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshWeaver.Fixture;
using System.Collections.Concurrent;

// Core twin of MeshWeaver.Plugins/src/MeshWeaver.Hosting.Monolith.Test/NackReachesTheWaiterDuringTeardownTest.cs (ported 2026-09-02):
// core's own CI cannot run the Plugins-hosted suite, so the 2026-09-02 regression of the owner-disposing
// NACK (PR #3070) reached main unseen. Keep the two in step.
//
// 🚨 EDITING THIS FILE? The Plugins copy is a separate file and this comment cannot make anyone open
// it — #3345: #3291 rewrote this twin to the no-forced-teardown contract, left the Plugins one
// asserting the contract it had just deleted, and the pin bump a day later produced a 55-second
// VERDICT_TIMEOUT that was filed as a core regression and bisected across five commits. What holds
// the two together now is Plugins' TeardownTwinParityTest, which compares this body against its own
// below the namespace line, at MW_PLATFORM_REF. It reddens in the PIN BUMP, not here — so a change
// to this file is not done until the Plugins copy carries it too.
// See Doc/Architecture/CrossRepoPairGate → "Shape 7's worst form".
namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#2778 — the OwnerDisposing NACK was dropped exactly when the parent is disposing hosted
/// hubs, and the caller then burned its full verdict budget in silence.</b>
///
/// <para><c>RegisterOwnerDisposingNack</c> delivered the NACK as
/// <c>parent.Post(...)</c> guarded by <c>parent.RunLevel &lt; DisposeHostedHubs</c>. With the run
/// levels ordered <c>Starting, Started, Quiescing, DisposeHostedHubs, …</c> that guard is open
/// for the first three and shut for the rest — so the one moment a whole BATCH of owner hubs goes
/// down with patches in flight is the one moment none of their NACKs is delivered.</para>
///
/// <para><b>The guard's rationale was an assumption the code could not verify.</b> It read:
/// <i>"during a whole-mesh teardown the parent is past that mark too, the post is skipped, and
/// nobody is waiting."</i> A caller whose wait outlives the START of teardown is still waiting,
/// and it is exactly that caller which is guaranteed to get silence instead of its answer.</para>
///
/// <para><b>Why this test disposes the MESH and not just the owner.</b> Disposing only the
/// per-node hub — what <c>LateNackReenqueueTest</c> and <c>OwnerDisposalNackTest</c> do — leaves
/// the parent at <c>Started</c>, where the old guard was OPEN and the NACK was delivered. Those
/// tests therefore pass with the defect fully present. Reaching the hole at all requires the
/// parent to be past <c>DisposeHostedHubs</c> when the owner's disposal action runs, and disposing
/// the mesh is what puts it there — which is also precisely the production shape the issue
/// reported (~13 distinct stream ids retiring in one burst).</para>
///
/// <para><b>What is asserted is that the NACK REACHES THE ARMED WATCH, not that the caller's
/// callback runs.</b> This distinction was learned from the first draft of this test, which
/// asserted the caller's terminal and failed even with the fix in place. The reason is sound and
/// worth recording: disposing the mesh takes the CALLER's own subscription down along with the
/// owner, so by the time the verdict is raised there is no live observer left to receive it —
/// `observer.OnError` on a disposed subscription is a silent no-op. Asserting on the caller's
/// callback would therefore be asserting on something this scenario cannot produce, whatever the
/// framework does.</para>
///
/// <para>The armed watch is the right subject because it IS the thing #2778 is about. The registry
/// entry is armed while a caller waits and is REMOVED by <c>Dispatch</c> — so the entry returning
/// to zero is precisely "the owner's verdict reached the waiter", the step that used to be skipped.
/// With the defect present the post is never made, nothing dispatches, and the entry simply sits
/// armed until it expires 30 s later.</para>
///
/// <para><b>Why the parked merge turn releases itself when the owner starts shutting down.</b>
/// The first shape of this test parked the owner's merge executor until the test's own
/// <c>finally</c> — a turn blind to the shutdown, held for the whole assertion window. The verdict
/// then reached the waiter only after the disposal watchdog had force-torn the parked sync hub down
/// at 8 s, which made the 10 s assertion "the watchdog plus two seconds" — and a loaded CI shard
/// lost those two seconds (run 33847949620). That force-teardown no longer exists: a hub whose turn
/// is parked stays honestly pending and reports the turn (<c>DisposalStallWatchdogTest</c>). So the
/// parked turn here is what accepted work is supposed to be — it observes the owner's
/// <c>IsShuttingDown</c> and finishes its job.</para>
///
/// <para><b>🚨 Why the assertion waits for the owner to be Dead instead of budgeting the
/// verdict.</b> The successor to that 10 s bound was a flat 6 s from <c>Mesh.Dispose()</c>, and it
/// repeated the same error one layer up: it raced the OWNER'S WHOLE TEARDOWN, which the framework
/// deliberately does not bound. Measured in MeshWeaver.Plugins CI (run 34195323935, 2026-09-08):
/// the test timed out with not one framework warning in the window — no
/// <c>LATE_NACK_REENQUEUE</c>, no "reached no route", no <c>VERDICT_EXPIRED</c>, no
/// <c>QUIESCE-WAIT</c>, no <c>DISPOSE-WEDGE</c>, and the memory watchdog ticking on schedule.
/// Nothing had gone wrong; the owner had simply not reached its ShutDown phase yet, and nothing
/// promised it would. <c>DisposeHubsReactive</c> dropped its flat <c>Timeout(5s)</c> in #1317
/// because "a join must not out-run the answers it is joining", and the stall detector that
/// replaced it re-arms on any progress — so a large teardown that keeps moving has no duration
/// bound at all. The assertion is now CAUSAL: <c>RunLevel == Dead</c> is set strictly after
/// <c>DisposeImpl()</c> has run the disposal registrants, so it is the exact instant at which
/// "the owner has had its chance to answer" becomes true. With the #2778 defect present the owner
/// reaches Dead just as fast with the watch still armed, so the test fails SOONER than the 6 s
/// version did, and for the right reason. See Doc/Architecture/TeardownVerdictsAreCausal.</para>
/// </summary>
public class NackReachesTheWaiterDuringTeardownTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly StallVerdictCapture verdicts = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(verdicts));

    [Fact]
    public async Task OwnerDisposingUnderMeshTeardown_StillAnswersTheWaitingCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/teardown-nack-node";
        await NodeFactory.CreateNode(
                new MeshNode("teardown-nack-node", TestPartition) { Name = "initial", NodeType = "Markdown" })
            .Should().Emit();

        await RequestHub.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
            .Should().Emit();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
            .Where(n => n is not null)
            .FirstAsync().Timeout(10.Seconds()).Await(ct);

        var nodeHub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        nodeHub.Should().NotBeNull();

        // Park the owner's merge executor, so the write below is ACCEPTED by the owner's handler
        // (which is what registers the disposal NACK) but provably cannot be answered inside the
        // caller's ~2 s response bound. The caller therefore hands its wait to the late watch —
        // and being armed there is the precondition the whole issue turns on.
        //
        // 🚨 No hand-woven gate: the turn → test signal is an AsyncSubject the parked turn
        // completes; the release travels back INTO the deliberately parked turn, so it is a
        // volatile flag polled under a bounded SpinUntil and written in the `finally`. The turn
        // ALSO observes the owner's shutdown (see the class remarks): accepted work finishes its
        // job when the mesh goes down, it does not sit on the block waiting to be killed.
        var primary = nodeHub!.GetWorkspace().DataContext
            .GetDataSourceForType(typeof(MeshNode))!
            .GetStreamForPartition(null)!;
        var gateEntered = new AsyncSubject<Unit>();
        var releaseGate = 0;
        var owner = nodeHub!;
        primary.Update((Func<EntityStore?, ChangeItem<EntityStore>?>)(_ =>
        {
            gateEntered.OnNext(Unit.Default);
            gateEntered.OnCompleted();
            SpinWait.SpinUntil(
                () => Volatile.Read(ref releaseGate) == 1 || owner.IsShuttingDown,
                TimeSpan.FromSeconds(60));
            return null;
        }), _ => { });
        try
        {
            await gateEntered.Should().Within(10.Seconds()).Emit(
                "the gated turn must be running on the primary stream's executor before the write");

            var marker = $"teardown-nack-{Guid.NewGuid():N}"[..24];
            var workspace = Mesh.GetWorkspace();
            MeshNode? callerTerminal = null;
            Exception? callerError = null;
            using var writeSub = workspace.GetMeshNodeStream(path)
                .Update(n => n with { Name = marker })
                .Subscribe(n => callerTerminal = n, ex => callerError = ex);
            Output.WriteLine($"[write] patch posted with marker {marker}; owner merge is parked");

            // Fence on the caller actually WAITING — an armed late watch is that fact, and it is
            // the same fact the disposal NACK has to land on. Without it this test could pass by
            // asserting about a caller that was never waiting in the first place.
            var registry = Mesh.ServiceProvider.GetRequiredService<LatePatchResponseRegistry>();
            await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .Where(_ => registry.ArmedCount > 0)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            // 🚨 The watch this test is about is THIS ONE, named. `ArmedCount` is the whole
            // registry, and the framework legitimately arms a SECOND entry microseconds after the
            // first is consumed: dispatching the NACK runs the caller's LATE_NACK_REENQUEUE, which
            // posts a fresh PatchDataRequest and arms a watch for it. So "the count is back to
            // zero" is not the fact this test wants and cannot even be relied on to occur — it
            // held only because that re-attempt is refused quickly. `ArmedRequestIds` exists for
            // exactly this (see its remarks); the id is the correlation the NACK has to land on.
            var armedId = registry.ArmedRequestIds.Single();
            Output.WriteLine($"[fence] caller is armed on the late watch (request={armedId})");

            // 🚨 The scenario. Dispose the MESH, not the node hub: the owner's disposal action then
            // runs with its parent already past DisposeHostedHubs — the exact window in which the
            // old guard skipped the post and the caller heard nothing.
            Mesh.Dispose();
            Output.WriteLine("[dispose] mesh disposal invoked — parent is past DisposeHostedHubs");

            // 🚨 Wait for the OWNER to be terminally down — the causal precondition — and only
            // then assert. This is NOT a duration budget for the verdict, and that distinction is
            // the whole point.
            //
            // <para>The NACK is a ShutDown-phase disposal registrant, and the ordering inside
            // <c>HandleShutdownCore</c> is fixed: <c>DisposeImpl()</c> runs
            // <c>disposables.Dispose()</c> — which IS the registrant that mints and dispatches this
            // verdict — strictly before <c>RunLevel = Dead</c> and <c>SignalDisposalCompleted()</c>.
            // So <c>Dead</c> is the exact instant at which "the owner has had its chance to answer"
            // becomes true. Reading the registry there is a decision, not a race: with the #2778
            // defect present the owner reaches Dead just as fast and the entry is STILL armed, so
            // this fails at the first instant rather than six seconds later.</para>
            //
            // <para>🚨 It replaces a flat 6 s race against the whole teardown, which is what made
            // this test flaky in MeshWeaver.Plugins CI (run 34195323935, 2026-09-08): the failure
            // carried a bare TimeoutException and NOT ONE framework warning — no
            // LATE_NACK_REENQUEUE, no `reached no route`, no VERDICT_EXPIRED, no QUIESCE-WAIT, no
            // DISPOSE-WEDGE — i.e. the owner simply had not reached ShutDown yet. Nothing promises
            // it will. <c>DisposeHubsReactive</c> deliberately dropped its flat <c>Timeout(5s)</c>
            // in #1317 precisely because "a join must not out-run the answers it is joining", and
            // the disposal watchdog that replaced it is a STALL detector re-armed on any progress —
            // so a big teardown that keeps moving is unbounded BY DESIGN. Asserting a 6 s bound on
            // it was asserting a guarantee the framework does not make, and it is the same flat-cap
            // mistake #1317 removed one layer down. See Doc/Architecture/TeardownVerdictsAreCausal.
            // </para>
            //
            // <para>The bound below is a liveness guard on that PRECONDITION, named as such: it
            // must leave the watch admissible (below <see cref="LatePatchResponseRegistry.LateResponseWatchBound"/>),
            // because a question asked after the entry has expired cannot be answered either way.
            // If it ever trips, the failure says "the owner never finished disposing", which is a
            // different defect with a different investigation — and the stall-verdict assertion
            // below names it.</para>
            var ownerDownBound = LatePatchResponseRegistry.LateResponseWatchBound - 10.Seconds();
            await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .Where(_ => owner.RunLevel == MessageHubRunLevel.Dead)
                .FirstAsync().Timeout(ownerDownBound).Await(ct);
            Output.WriteLine($"[owner] {owner.Address} is Dead — its disposal registrants have run");

            registry.ArmedRequestIds.Should().NotContain(armedId,
                "the owner minted an OwnerDisposing NACK for this patch, and a caller was armed and "
                + "waiting for it; the verdict must reach that watch even though the parent is past "
                + "DisposeHostedHubs. The owner is now terminally Dead, so its ShutDown-phase "
                + "registrant has already run — a watch still armed here is the #2778 defect, and "
                + "the caller then hears nothing and burns the whole 31 s verdict budget for an "
                + "answer that had already been minted");

            // 🚨 CONSUMED, not merely gone. This is what the old 6 s bound was standing in for:
            // an entry that EXPIRES also stops being armed, so "not armed" alone would pass on the
            // defect. The registry counts that case itself (#3197 made an expired verdict a
            // reported fact rather than silence), so the distinction is now OBSERVED instead of
            // inferred from a stopwatch — and the observation holds however long the teardown took.
            registry.ExpiredVerdicts.Should().Be(0,
                "the watch must have been CONSUMED by the owner's dispatch, not have lapsed past "
                + "LateResponseWatchBound and been reported as a verdict that arrived too late");

            // Diagnostics only — under a full mesh teardown the caller's own subscription is going
            // down too, so whether its callback still runs is not this test's subject (see the
            // class remarks).
            Output.WriteLine(
                $"[caller] terminal={(callerTerminal is null ? "<null>" : callerTerminal.Name)} "
                + $"error={(callerError is null ? "<null>" : callerError.GetType().Name)}");

            // No hub was reported wedged and nothing was cancelled or torn down out of band: the
            // verdict travelled the ordinary teardown. A stall verdict here would mean the parked
            // turn did not observe the shutdown — the shape this test no longer relies on.
            foreach (var verdict in verdicts.Entries)
                Output.WriteLine(verdict);
            verdicts.Entries.Should().BeEmpty(
                "the waiter must be answered by the ordinary teardown, not by a stall verdict on a parked hub");
        }
        finally
        {
            // In a `finally` so a failing assertion above cannot strand the parked executor turn.
            Volatile.Write(ref releaseGate, 1);
        }
    }

    /// <summary>Captures the Error-level disposal stall verdicts (both shapes).</summary>
    private sealed class StallVerdictCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capturing(Entries);
        public void Dispose() { }

        private sealed class Capturing(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Error)
                    return;
                var message = formatter(state, exception);
                if (message.Contains("DISPOSAL DEADLOCK DETECTED", StringComparison.Ordinal)
                    || message.Contains("[DISPOSE-WEDGE]", StringComparison.Ordinal))
                    sink.Enqueue(message);
            }
        }
    }
}
