using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Deterministic repro of the #6883 snapshot, and the guard on what the two verdicts it produced may
/// assert. Production (one pod, one partition root and its <c>sync/*</c> child, torn down together):
///
/// <code>
/// child  7314: RunLevel=ShutDown … Executing(ShutdownRequest, 24002ms) Registrants=0
///              "… a registered cleanup is BLOCKING … find the registrant."
/// owner  7313: RunLevel=DisposeHostedHubs … "it has 0 hosted hub(s) below it, so the stall is in a
///              hosted hub or a join it is waiting on — the diagnostics below name it."
/// </code>
///
/// <para>Both lines described something their snapshot could not support. <c>Registrants=0</c> is
/// what Rx's <c>CompositeDisposable</c> reports for the WHOLE registrant walk — it zeroes its count
/// before disposing an entry — so the field read as "nothing registered" exactly while a registrant
/// was blocking, and the verdict could not say which one. And the owner's snapshot named no child
/// because the child had already run its own registry removal (one of its registrants) while the
/// owner's join kept waiting on it.</para>
///
/// <para><b>Why deterministic.</b> The stall is MANUFACTURED, not waited for: a registrant (or a
/// reactive dispose action) parks the ShutDown turn on a volatile int under a bounded
/// <c>SpinWait.SpinUntil</c>, released in a <c>finally</c>; its engagement is a producer→test
/// <see cref="AsyncSubject{T}"/>, asserted, so a park that never engaged cannot read as a pass.
/// The verdicts then come from the hub's own stall detector one budget later.</para>
///
/// <para>What this does NOT reproduce: what blocked production's registrant. That stall resolved
/// on its own between 24 s and 32 s (two verdicts, none a budget later), and no stack was taken.
/// These verdicts are the reading that names it the next time.</para>
/// </summary>
public class ShutDownPhaseStallVerdictTest : HubTestBase
{
    private readonly VerdictCapture capture = new();

    /// <summary><c>MessageHub.DisposalShutDownPhaseBlocked</c> — the ShutDown phase holding the block.</summary>
    private const int ShutDownPhaseBlocked = 7314;

    /// <summary><c>MessageHub.DisposalStalledBelowThisHub</c> — the stall is in a hosted hub or a join.</summary>
    private const int StalledBelowThisHub = 7313;

    /// <summary>Written by the test to release a parked ShutDown turn; polled by the park.</summary>
    private int release;

    /// <summary>Completed by the parked turn the instant it is parked.</summary>
    private readonly AsyncSubject<Unit> parked = new();

    public ShutDownPhaseStallVerdictTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(capture));
    }

    /// <summary>
    /// A registered cleanup whose <c>Dispose</c> parks the action block. Named on purpose: the
    /// verdict must print THIS method's name, which is how a reader finds the line.
    /// </summary>
    private void ParkTheRegistrantWalk(IMessageHub _)
    {
        parked.OnNext(Unit.Default);
        parked.OnCompleted();
        // Bounded, and the bound is a diagnostic rather than a budget: the test releases it in its
        // own finally, long before this elapses.
        SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TimeSpan.FromSeconds(90));
    }

    /// <summary>
    /// The #6883 shape: an owner tearing down its <c>sync/*</c> child, the child blocked inside its
    /// registrant walk. The child's 7314 must name the registrant that is running and the step it is
    /// in, and say why <c>Registrants=0</c>; the owner's 7313 must name the child it is still joining.
    ///
    /// <para><b>Control, measured.</b> On the unfixed tree the child's verdict reads
    /// <c>"… find the registrant.\n Hub sync/… Registrants=0"</c> with no registrant named, and the
    /// owner's snapshot carries only its own line — both assertions below fail.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARegistrantBlockingTheWalk_IsNamed_AndTheOwnerStillNamesTheChild()
    {
        var id = Guid.NewGuid().AsString();
        var owner = (MessageHub)Mesh.GetHostedHub(new Address("owner", id),
            c => c.WithPostingIdentity(PostingIdentity.System), HostedHubCreation.Always)!;
        var child = (MessageHub)owner.GetHostedHub(new Address("sync", id),
            c => c.WithPostingIdentity(PostingIdentity.System), HostedHubCreation.Always)!;
        // Registered AFTER creation, as a stream registers on its sync hub: the walk runs the
        // child's own registry removal first, then parks here — production's order.
        child.RegisterForDisposal(ParkTheRegistrantWalk);

        try
        {
            owner.Dispose();
            await parked.Should().Within(TestTimeouts.Convergence).Emit(
                "the repro only means anything if the child's ShutDown turn actually parked in its walk",
                cancellationToken: TestContext.Current.CancellationToken);

            var childVerdict = await Verdict(ShutDownPhaseBlocked, child.Address);
            var ownerVerdict = await Verdict(StalledBelowThisHub, owner.Address);
            Output.WriteLine(childVerdict);
            Output.WriteLine(ownerVerdict);

            childVerdict.Should().Contain(nameof(ParkTheRegistrantWalk),
                "the verdict says 'find the registrant' — it must NAME the one whose Dispose holds the block");
            childVerdict.Should().Contain("registrant walk",
                "the step of the ShutDown phase holding the block is measured, not assumed");
            childVerdict.Should().Contain("Registrants=0 (walk begun",
                "Rx zeroes the composite's count before disposing an entry, so Registrants=0 during the "
                + "walk is expected — the line must say so, or it reads as 'nothing registered' (#6883)");
            childVerdict.Should().NotContain("NO registrant has run",
                "a registrant IS running; the no-registrant sentences belong to the other steps");

            ownerVerdict.Should().Contain(child.Address.ToString(),
                "the owner's join is still waiting on the child, so its 'the diagnostics below name it' "
                + "must actually name it — even though the child already took itself out of the registry");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        await owner.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "released, the walk finishes and the owner's join completes",
            cancellationToken: TestContext.Current.CancellationToken);
        child.RunLevel.Should().Be(MessageHubRunLevel.Dead);
    }

    /// <summary>
    /// The same phase blocked one step EARLIER — in a reactive dispose action, which runs before the
    /// registrant walk. No registered synchronous cleanup has run, so the verdict must not send the
    /// reader to one; it names the step that is holding the block.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AReactiveDisposeActionBlocking_IsNotBlamedOnARegistrant()
    {
        var hub = (MessageHub)Mesh.GetHostedHub(new Address("victim", Guid.NewGuid().AsString()),
            c => c.WithPostingIdentity(PostingIdentity.System), HostedHubCreation.Always)!;
        hub.RegisterForDisposal(_ =>
        {
            ParkTheRegistrantWalk(hub);
            return Observable.Return(Unit.Default);
        });

        try
        {
            hub.Dispose();
            await parked.Should().Within(TestTimeouts.Convergence).Emit(
                "the reactive dispose action must actually park the ShutDown turn",
                cancellationToken: TestContext.Current.CancellationToken);

            var verdict = await Verdict(ShutDownPhaseBlocked, hub.Address);
            Output.WriteLine(verdict);

            verdict.Should().Contain("reactive dispose actions");
            verdict.Should().Contain("NO registered synchronous cleanup has run yet",
                "the walk has not started, so 'find the registrant' would be false by construction");
            verdict.Should().NotContain("DisposingRegistrant(",
                "no registrant is running, and the snapshot must not claim one");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        await hub.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "released, the phase runs to Dead",
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>A late cleanup, registered after the walk began: the composite runs it inline on the
    /// REGISTERING thread, which holds no part of the action block.</summary>
    private static void LateOffTurnCleanup() { }

    /// <summary>A cleanup registered by another cleanup while that one is disposing.</summary>
    private static void NestedInnerCleanup(IMessageHub _) { }

    /// <summary>
    /// A late registration while the walk is blocked must not hide the blocker. Once the composite is
    /// disposed, <c>RegisterForDisposal</c> disposes the newcomer inline on the caller's thread; with
    /// one shared stamp that off-turn cleanup overwrote the blocked entry's stamp and cleared it on
    /// return, and the verdict read "between entries" while the entry still held the block.
    ///
    /// <para><b>Control, measured:</b> with the single CompareExchange-cleared stamp this verdict reads
    /// "the walk is between entries" and names neither cleanup.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ALateOffTurnRegistrant_DoesNotHideTheBlockedWalkEntry()
    {
        var hub = (MessageHub)Mesh.GetHostedHub(new Address("victim", Guid.NewGuid().AsString()),
            c => c.WithPostingIdentity(PostingIdentity.System), HostedHubCreation.Always)!;
        hub.RegisterForDisposal(ParkTheRegistrantWalk);

        try
        {
            hub.Dispose();
            await parked.Should().Within(TestTimeouts.Convergence).Emit(
                "the walk entry must actually park before the late registration races it",
                cancellationToken: TestContext.Current.CancellationToken);
            // Runs inline HERE, on the test thread, because the composite is already disposed.
            hub.RegisterForDisposal(_ => LateOffTurnCleanup());

            var verdict = await Verdict(ShutDownPhaseBlocked, hub.Address);
            Output.WriteLine(verdict);
            verdict.Should().Contain(nameof(ParkTheRegistrantWalk),
                "the entry still holding the block is the finding, whatever ran off-turn meanwhile");
            verdict.Should().NotContain("between entries",
                "a cleanup IS running on the walk; an off-turn cleanup's return must not erase it");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        await hub.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "released, the walk finishes", cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A cleanup that registers another cleanup while it runs, then blocks. The inner one runs inline
    /// on the walk thread and returns; the outer one must still be named as the blocker.
    ///
    /// <para><b>Control, measured:</b> with the single stamp, the inner cleanup's return cleared the
    /// outer one's stamp and the verdict read "between entries".</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ANestedRegistrantsReturn_DoesNotEraseTheOuterBlocker()
    {
        var hub = (MessageHub)Mesh.GetHostedHub(new Address("victim", Guid.NewGuid().AsString()),
            c => c.WithPostingIdentity(PostingIdentity.System), HostedHubCreation.Always)!;
        hub.RegisterForDisposal(h =>
        {
            // Disposed inline, on this (the walk) thread, because the composite is disposed.
            h.RegisterForDisposal(NestedInnerCleanup);
            ParkTheRegistrantWalk(h);
        });

        try
        {
            hub.Dispose();
            await parked.Should().Within(TestTimeouts.Convergence).Emit(
                "the outer cleanup must park after its nested registration returned",
                cancellationToken: TestContext.Current.CancellationToken);

            var verdict = await Verdict(ShutDownPhaseBlocked, hub.Address);
            Output.WriteLine(verdict);
            verdict.Should().Contain(nameof(ANestedRegistrantsReturn_DoesNotEraseTheOuterBlocker),
                "the outer cleanup — a lambda declared in this test method — is still on the block");
            verdict.Should().NotContain("between entries");
            verdict.Should().NotContain(nameof(NestedInnerCleanup) + ",",
                "the inner cleanup returned; it must not be reported as running");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        await hub.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "released, the walk finishes", cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Posted to a hub that handles it without ever answering.</summary>
    private record NeverAnswered;

    /// <summary>
    /// A subscriber of a pending callback blocking in its <c>onError</c> while the ShutDown phase
    /// errors the callbacks out. <c>CancelCallbacks</c> empties the registry BEFORE it errors the
    /// entries, so the snapshot's PendingCallbacks cannot name the one being errored; the verdict must.
    ///
    /// <para>The callback is registered in the owner's <c>DisposeHostedHubs</c> phase — by its
    /// child's cleanup — so the owner's quiescing has already drained and the callback is still
    /// pending when the owner reaches ShutDown.</para>
    ///
    /// <para><b>Control, measured:</b> on the previous revision the verdict pointed at
    /// "one of the PendingCallbacks below" over a snapshot that listed none.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ASubscriberBlockingInCancelCallbacks_IsNamedThoughTheRegistryIsCleared()
    {
        var id = Guid.NewGuid().AsString();
        var silent = Mesh.GetHostedHub(new Address("silent", id),
            c => c.WithPostingIdentity(PostingIdentity.System)
                  .WithHandler<NeverAnswered>((_, d) => d.Processed()), HostedHubCreation.Always)!;
        var owner = (MessageHub)Mesh.GetHostedHub(new Address("owner", id),
            c => c.WithPostingIdentity(PostingIdentity.System), HostedHubCreation.Always)!;
        var child = owner.GetHostedHub(new Address("child", id),
            c => c.WithPostingIdentity(PostingIdentity.System), HostedHubCreation.Always)!;
        child.RegisterForDisposal(_ =>
            owner.Observe(new NeverAnswered(), o => o.WithTarget(silent.Address))
                .Subscribe(_ => { }, _ => ParkTheRegistrantWalk(owner)));

        try
        {
            owner.Dispose();
            await parked.Should().Within(TestTimeouts.Convergence).Emit(
                "the subscriber must park inside the owner's callback cancellation",
                cancellationToken: TestContext.Current.CancellationToken);

            var verdict = await Verdict(ShutDownPhaseBlocked, owner.Address);
            Output.WriteLine(verdict);
            verdict.Should().Contain("erroring pending callbacks");
            verdict.Should().Contain(nameof(NeverAnswered) + " → " + silent.Address,
                "the callback whose subscriber blocks is named, with its target — the registry no longer can");
            verdict.Should().Contain("ErroringCallback(",
                "the snapshot carries the same record");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        await owner.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "released, the owner runs to Dead", cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The first Error with <paramref name="eventId"/> about <paramref name="address"/>. Polls the
    /// capture: the producer is a logger on the detector's own thread, with no signal of its own.
    /// </summary>
    private async Task<string> Verdict(int eventId, Address address)
    {
        var prefix = $"Hub {address} made no teardown progress";
        return await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .Select(_ => capture.Entries.FirstOrDefault(e => e.Id == eventId && e.Message.Contains(prefix)))
            .Where(e => e is not null)
            .Select(e => e!.Message)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
    }

    /// <summary>Captures every Error-level line with its event id.</summary>
    private sealed class VerdictCapture : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capturing(Entries);
        public void Dispose() { }

        public sealed record Entry(int Id, string Message);

        private sealed class Capturing(ConcurrentQueue<Entry> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                    sink.Enqueue(new Entry(eventId.Id, formatter(state, exception)));
            }
        }
    }
}
