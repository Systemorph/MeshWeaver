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
            .Timeout(TimeSpan.FromSeconds(30))
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
