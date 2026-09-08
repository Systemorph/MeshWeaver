using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The teardown intake gate's exemption for teardown's OWN traffic had no phase bound, and that is
/// what produced <a href="https://github.com/Systemorph/MeshWeaver/issues/3647">#3647</a>.
///
/// <para><b>The defect.</b> <c>MessageService.RefusesIntake</c> read
/// <c>if (delivery.Message is ShutdownRequest or DisposeRequest) return false;</c> — at every run
/// level, forever. The comment above it gave the reason ("it is what advances the phases at all"),
/// but the rule did not carry it. So a <see cref="DisposeRequest"/> arriving after the hub had
/// already begun its own teardown was ADMITTED into a window where <c>HandleDispose</c> is a proven
/// no-op: <c>IsShuttingDown</c> is set (no recycle announcement) and <c>MessageHub.Dispose()</c>
/// returns on its first line. It could do nothing except occupy a turn slot — and
/// <c>messageService.Dispose()</c>, a few statements later inside the hub's own ShutdownRequest
/// turn, then found it there and filed it at <b>Error</b> as accepted work that had been discarded.
/// The red-log pipeline opened #3647 on that line.</para>
///
/// <para><b>What the production line actually said</b> (memex, 2026-09-07T21:39:38Z, hub
/// <c>sync/KptJzzVNdk2qCCf4nMvcSA</c>): <c>1 turn(s) still queued and unprocessed (the pump stops
/// with this call). RunLevel=ShutDown; 0 deferred delivery(ies) …; last turn executing:
/// ShutdownRequest.</c> Every one of those four facts is reproduced by
/// <see cref="ADisposeRequestPostedInsideTheShutdownWindow_IsRefusedAtIntake"/> when the gate is
/// unbounded — and the count is 1 because at <c>RunLevel &gt;= DisposeHostedHubs</c> the gate
/// admits nothing else.</para>
///
/// <para><b>🚨 The turn was never lost, and the Error was the defect.</b> <c>Dispose()</c> runs
/// INSIDE the ShutdownRequest turn, so <c>DrainLoop</c> is one frame below on the same stack and
/// takes the next turn the moment that turn returns. Measured on the unfixed tree, 3 ms after the
/// Error: <c>Hub victim/… is disposing. Not processing DisposeRequest (id=…)</c> — the pump had
/// dequeued it and the disposing seam had dealt with it. Nothing was left waiting. So the fix is
/// not to widen anything, not to drain harder and not to retry: it is to stop admitting a message
/// one phase after the last phase that could use it, and to stop the report claiming a discard that
/// does not happen.</para>
///
/// <para><b>Why an absence needs the control arm.</b> This test's subject assertion is that two log
/// lines do NOT appear. That is exactly the shape that passes when nothing happened at all, so it
/// is paired with (a) the registrant recording the run level it actually posted from — the post
/// provably WAS made inside the shutdown window — and (b) a control arm in which the same message
/// type, sent to the same hub one phase earlier, is accepted and tears the hub down. A gate that
/// refused everything, or a fixture whose registrant never ran, fails one of those two.</para>
/// </summary>
public class ShutdownWindowAdmissionTest : HubTestBase
{
    private readonly TeardownLog teardown = new();

    public ShutdownWindowAdmissionTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(teardown));
    }

    private record Ping : IRequest<Pong>;

    private record Pong;

    /// <summary>
    /// The pin. A <see cref="DisposeRequest"/> that lands after the hub has begun disposing is
    /// refused at the door, so the hub reaches its ShutDown phase with an EMPTY turn queue and
    /// files no discard.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ADisposeRequestPostedInsideTheShutdownWindow_IsRefusedAtIntake()
    {
        var address = new Address("shutdown-window", "refused");
        var victim = await StartedVictim(address);

        // 🚨 The window, made deterministic rather than raced. A RegisterForDisposal registrant
        // runs inside DisposeImpl(), which is the ShutDown case of HandleShutdownCore — i.e. a few
        // statements before messageService.Dispose() reads the queue. Posting from there lands the
        // delivery in exactly the window the production line names, every run, with no timing.
        // It is also a real shape: RegisterForDisposal registrants are where the framework mints
        // and dispatches teardown-time answers (Doc/Architecture/TeardownVerdictsAreCausal).
        var postedFrom = new RunLevelWitness();
        victim.RegisterForDisposal(h =>
        {
            postedFrom.Record(h.RunLevel);
            h.Post(new DisposeRequest(), o => o.WithTarget(h.Address));
        });

        victim.Dispose();
        await WaitUntilDead(victim);

        // POSITIVE CONTROL #1 — the post was really made, and really made inside the window. Without
        // this the two absence assertions below would pass on a fixture whose registrant never ran.
        postedFrom.Level.Should().Be(MessageHubRunLevel.ShutDown,
            "the registrant runs inside DisposeImpl, so its post is issued from the terminal phase "
            + "— that IS the shutdown window, and if it were not this test would be asserting "
            + "absence about a message that was never sent");

        var lines = teardown.For(address);
        foreach (var line in lines)
            Output.WriteLine(line);

        // THE SUBJECT. #3647's own line, byte-for-byte the shape the incident carries.
        lines.Should().NotContain(l => l.Contains("[DISPOSE-DISCARD]", StringComparison.Ordinal),
            "the DisposeRequest was refused at intake, so nothing was queued when the ShutDown "
            + "phase read the queue — the Error that opened #3647 has no state left to report");

        // The tell that the turn was ADMITTED. Before the bound, the pump dequeued this delivery
        // milliseconds after the discard Error and the disposing seam logged exactly this. Its
        // absence is what says the gate refused rather than the seam mopping up.
        lines.Should().NotContain(
            l => l.Contains("is disposing. Not processing DisposeRequest", StringComparison.Ordinal),
            "a refusal happens at the DOOR: the delivery must never have become a turn, so the "
            + "disposing seam in RunHandler must never have seen it either");

        // POSITIVE CONTROL #2 — refusing it did not wedge the teardown.
        victim.RunLevel.Should().Be(MessageHubRunLevel.Dead,
            "the refusal costs the teardown nothing — the hub still reaches its terminal state");
    }

    /// <summary>
    /// The control arm, and the reason the gate is bounded by PHASE rather than switched off. The
    /// same message, to the same hub, ONE PHASE EARLIER — before its own teardown has begun — is
    /// still the ordinary recycle it has always been, and still disposes the hub.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ADisposeRequestArrivingBeforeTeardownBegins_StillRecyclesTheHub()
    {
        var address = new Address("shutdown-window", "accepted");
        var victim = await StartedVictim(address);

        victim.RunLevel.Should().Be(MessageHubRunLevel.Started,
            "the control arm is about a hub that has NOT begun disposing — the whole point of the "
            + "bound is that this case is unchanged");

        Mesh.Post(new DisposeRequest(), o => o.WithTarget(address));

        await WaitUntilDead(victim);
        victim.RunLevel.Should().Be(MessageHubRunLevel.Dead,
            "a routed DisposeRequest to a live hub is a recycle and must still be handled — a gate "
            + "that refused this would have traded a false Error for a hub nobody can recycle");
    }

    private async Task<IMessageHub> StartedVictim(Address address)
    {
        var victim = Mesh.GetHostedHub(address, c => c
            // Plumbing-only fixture with no signed-in user, exactly like the hubs HubTestBase
            // configures for itself: posts carry the System identity rather than none.
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<Ping>((h, d) =>
            {
                h.Post(new Pong(), o => o.ResponseFor(d));
                return d.Processed();
            }), HostedHubCreation.Always)!;

        // Bring-up must COMPLETE first. Dispose() cancels an unfinished initialization outright
        // (RunLevel < Started), which is a different path entirely.
        await victim.Observe(new Ping(), o => o.WithTarget(address))
            .Should().Within(TestTimeouts.Quick)
            .Emit("the hub must answer once before the test touches its teardown");
        return victim;
    }

    /// <summary>
    /// Waits on the CAUSE, never on a clock (Doc/Architecture/TeardownVerdictsAreCausal): a hub's
    /// teardown has no duration bound, and <c>RunLevel == Dead</c> is set strictly after
    /// <c>DisposeImpl()</c> and <c>messageService.Dispose()</c> have run — which is precisely the
    /// pair this test is asserting about. Polled off a timer rather than awaited on
    /// <c>DisposalCompleted</c>, because awaiting that resumes the continuation on the signalling
    /// thread, inside the hub's ShutDown <c>try</c>/<c>catch</c>, where a failing assertion would be
    /// swallowed and logged as "Error during shutdown of hub".
    /// </summary>
    private static Task WaitUntilDead(IMessageHub victim) =>
        Observable.Interval(TimeSpan.FromMilliseconds(25)).StartWith(0L)
            .Where(_ => victim.RunLevel == MessageHubRunLevel.Dead)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// The run level a disposal registrant observed when it posted. A plain volatile int — nothing
    /// waits on it, so it needs no signal of any kind.
    /// </summary>
    private sealed class RunLevelWitness
    {
        private int level = -1;

        public void Record(MessageHubRunLevel observed) => Volatile.Write(ref level, (int)observed);

        public MessageHubRunLevel? Level =>
            Volatile.Read(ref level) < 0 ? null : (MessageHubRunLevel)Volatile.Read(ref level);
    }

    /// <summary>
    /// Captures the two teardown lines this test is about, per hub address. Warning and above only:
    /// both lines ship at those levels in production, which is why one of them became an issue.
    /// </summary>
    private sealed class TeardownLog : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> entries = new();

        public ILogger CreateLogger(string categoryName) => new Capturing(entries);

        public void Dispose() { }

        public string[] For(Address address) =>
            entries.Where(e => e.Contains(address.ToString(), StringComparison.Ordinal)).ToArray();

        private sealed class Capturing(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning)
                    return;
                var message = formatter(state, exception);
                if (message.Contains("[DISPOSE-DISCARD]", StringComparison.Ordinal)
                    || message.Contains("is disposing. Not processing", StringComparison.Ordinal))
                    sink.Enqueue($"{logLevel} {eventId.Id} {message}");
            }
        }
    }
}
