using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// A gate that can NEVER open must FAIL what it holds — issue #1270, part 1.
///
/// <para><b>The hole.</b> Every hub built with <c>AddData()</c> starts with
/// <c>DataContextInit</c> SHUT. <c>DataContext.OpenInitializationGate</c>'s watchdog treats
/// <see cref="IMessageHub.IsShuttingDown"/> as a recognized outcome and returns — correctly
/// recording no FAILED residue (#1122) — but it returned WITHOUT opening the gate, and shutdown is
/// precisely the state after which nothing can ever open it. Everything already parked behind it,
/// and everything arriving during the rest of the teardown, was stranded: the sender's
/// <c>hub.Observe(...)</c> heard nothing until an unrelated deadline expired — the 30 s per-message
/// deferral timeout, or whenever the teardown finally reached <c>messageService.Dispose()</c>.
/// Observed in production shape as a <c>CreateNodeRequest</c> recorded
/// <c>DEFERRED gates=[DataContextInit]</c> at <c>runLevel=Quiescing</c> that never drained. #1269
/// fixed the CALLER that parked there; the gate itself still stranded anything that did.</para>
///
/// <para><b>Why the window is wide, not instantaneous.</b> The Quiescing phase waits for the hub's
/// own pending response callbacks to drain, up to <c>QuiesceTimeout</c> — and
/// <c>messageService.Dispose()</c>, the drain that finally answers the deferred backlog, runs two
/// phases later. So a hub with an outstanding callback sits at <c>Quiescing</c> for the whole
/// budget with its dead gate still holding traffic. This fixture reproduces exactly that: the
/// victim holds a request the host never answers, so its quiesce cannot complete.</para>
///
/// <para><b>The assertion is an ORDERING, not a clock.</b> It samples the victim's
/// <see cref="IMessageHub.RunLevel"/> at the instant the deferred caller is answered. GREEN: the
/// answer arrives while the hub is still <c>Quiescing</c> — from the gate recognising it can never
/// open. RED (pre-fix): the answer arrives at <c>ShutDown</c>/<c>Dead</c>, i.e. only as a side
/// effect of the teardown eventually reaching the disposal drain, a whole quiesce budget later.</para>
/// </summary>
public class DataContextShutdownGateAnswersDeferredTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record HangingItem(string Id);

    private record ProbeRequest : IRequest<ProbeResponse>;

    private record ProbeResponse;

    /// <summary>Accepted by the host and deliberately never answered — the victim's stuck callback.</summary>
    private record SilentRequest : IRequest<SilentResponse>;

    private record SilentResponse;

    private static readonly Address VictimAddress = new("shutdown-gate-victim", "1");

    /// <summary>Long enough that "answered while Quiescing" and "answered at ShutDown" cannot be confused.</summary>
    private static readonly TimeSpan VictimQuiesceTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Fires after disposal has started but far inside the quiesce budget — prod is 120 s.</summary>
    private static readonly TimeSpan VictimInitTimeout = TimeSpan.FromSeconds(2);

    [Fact(Timeout = 120_000)]
    public async Task DeferredRequest_IsAnsweredWhileQuiescing_WhenInitEndsInShutdown()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = GetHost(c => c
            // ProbeRequest/Response too: the host is the SENDER, and a sender that has not
            // registered the type serialises it under an auto short name and logs a warning.
            .WithTypes(typeof(SilentRequest), typeof(SilentResponse),
                typeof(ProbeRequest), typeof(ProbeResponse))
            // Accepts and never replies: this is what keeps the victim's pending callback alive.
            .WithHandler<SilentRequest>((_, d) => d.Processed())
            .WithPostingIdentity(PostingIdentity.System));

        var victim = host.GetHostedHub(VictimAddress, c => c
            .WithTypes(typeof(ProbeRequest), typeof(ProbeResponse),
                typeof(SilentRequest), typeof(SilentResponse))
            // The handler WOULD answer, so a pass can only come from the gate failing — never
            // from the request quietly being served after all.
            .WithHandler<ProbeRequest>((h, d) =>
            {
                h.Post(new ProbeResponse(), o => o.ResponseFor(d));
                return d.Processed();
            })
            .WithQuiesceTimeout(VictimQuiesceTimeout)
            .WithPostingIdentity(PostingIdentity.System)
            .AddData(data => data
                .WithInitializationTimeout(VictimInitTimeout)
                // A data source whose initial load NEVER completes: DataContextInit stays shut,
                // so the watchdog — not a successful init — is what resolves the gate.
                .AddSource(src => src.WithType<HangingItem>(t => t
                    .WithKey(i => i.Id)
                    .WithInitialData(() => Observable.Never<IEnumerable<HangingItem>>())))));
        victim.Should().NotBeNull();

        // 1. Give the victim a pending response callback that can never resolve, so its Quiescing
        //    phase burns the whole budget. Disposing the subscription at the end of the test
        //    releases it, so teardown stays fast.
        using var stuckCallback = victim!
            .Observe<SilentResponse>(new SilentRequest(), o => o.WithTarget(host.Address))
            .Subscribe(_ => { }, _ => { });

        // 2. The delivery whose fate this test is about: a request from ANOTHER hub, parked
        //    behind the shut gate. Both arms sample the victim's RunLevel when the answer lands.
        var deferred = Answer(host, victim, TimeSpan.FromSeconds(90), ct);
        await WaitForDeferredCount(victim, 1, ct);
        Output.WriteLine($"before dispose: {victim.GetPendingRequestDiagnostics()}");

        // 3. Tear the victim down. `disposalStarted` flips synchronously, so the DataContext
        //    watchdog firing ~2 s later lands squarely in the shutting-down branch — with the
        //    gate still shut and the request behind it.
        victim.Dispose();

        var outcome = await deferred;
        Output.WriteLine($"deferred request answered as '{outcome.Outcome}' at RunLevel={outcome.Level}");

        outcome.Outcome.Should().Be(nameof(DeliveryFailureException),
            "a delivery held behind a gate that can never open must get a TERMINAL answer it can "
            + "surface, never silence");
        outcome.Level.Should().BeOneOf(
            [MessageHubRunLevel.Starting, MessageHubRunLevel.Quiescing],
            "the answer must come from the gate recognising that shutdown makes it un-openable — "
            + "an answer at ShutDown/Dead means it only arrived as a side effect of the teardown "
            + "eventually reaching messageService.Dispose(), a whole quiesce budget later, which "
            + "is exactly the strand this fixes");

        // 4. …and the same must hold for a delivery arriving AFTER the gate is declared dead: it
        //    must be answered on arrival, never parked behind a gate nothing will ever release.
        var afterwards = await Answer(host, victim, TimeSpan.FromSeconds(20), ct);
        Output.WriteLine($"post-failure request answered as '{afterwards.Outcome}' at RunLevel={afterwards.Level}");
        afterwards.Outcome.Should().Be(nameof(DeliveryFailureException),
            "once a gate is known to be dead, a message that WOULD have been deferred behind it "
            + "must be failed on arrival rather than parked");
    }

    /// <summary>
    /// Posts a probe from <paramref name="sender"/> to the victim and resolves to HOW it was
    /// answered plus the victim's <see cref="IMessageHub.RunLevel"/> at that instant.
    /// </summary>
    private static Task<(string Outcome, MessageHubRunLevel Level)> Answer(
        IMessageHub sender, IMessageHub victim, TimeSpan budget, CancellationToken ct)
        => sender.Observe<ProbeResponse>(new ProbeRequest(), o => o.WithTarget(VictimAddress))
            .Select(_ => (Outcome: "answered", Level: victim.RunLevel))
            .Catch((Exception ex) =>
                Observable.Return((Outcome: ex.GetType().Name, Level: victim.RunLevel)))
            .FirstAsync()
            .Timeout(budget)
            .ToTask(ct);

    /// <summary>
    /// Waits until the victim's deferred queue holds <paramref name="count"/> message(s). Queue
    /// depth is public diagnostic surface (<see cref="IMessageHub.GetPendingRequestDiagnostics"/>)
    /// with no observable behind it, so this is the sanctioned reactive re-query — never a sleep.
    /// </summary>
    private static Task WaitForDeferredCount(IMessageHub victim, int count, CancellationToken ct)
        => Observable.Interval(TimeSpan.FromMilliseconds(20))
            .StartWith(0L)
            .Select(_ => victim.GetPendingRequestDiagnostics())
            .Where(diagnostics => diagnostics.Contains($"deferred={count}", StringComparison.Ordinal))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(20))
            .ToTask(ct);
}
