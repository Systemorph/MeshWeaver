using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Systemorph/MeshWeaver#3712, the residual — <b>a report about a parked delivery must name the
/// gates RECORDED when it was parked, never a fresh read of the gate set.</b>
///
/// <para><b>Why a fresh read is wrong by construction.</b> <c>MessageService.gates</c> is not a
/// record of what held a delivery; it is the set of gates that are closed RIGHT NOW, and
/// <c>OpenGate</c> REMOVES an opened gate from it. So any report composed after the fact describes
/// the hub at report time and not the delivery at park time — and the two disagree in exactly the
/// case where a reader most needs the answer.</para>
///
/// <para><b>The measured instance.</b> The disposal discard (event 7301) re-read that set after
/// <c>Dispose()</c> had opened every gate to release the buffers, and filed 364 production Errors
/// saying a delivery was <i>"still deferred behind its initialization gates <c>[]</c>"</i> —
/// an EMPTY list, which reads as "nothing was holding it", the opposite of what happened
/// (<c>Admin/_LogIncident/d2249f800ffc2577</c>, 2026-09-08 → 09-14, 13 pods). #3789 gave the
/// tracker a <c>GatesAtDeferral</c> field and taught the discard to use it. The two OTHER readers
/// of the same drain kept the fresh read, and this is the regression that pins them.</para>
///
/// <para><b>Neither of them needs a teardown to go wrong.</b> <c>OpenGate</c> restores the parked
/// turns to the FRONT of the main queue, but a tracker is retired only when its turn actually RUNS
/// (<c>ProcessDeferredMessage</c>) — so a hub whose loop is busy across the open holds live
/// trackers whose gates have ALL opened. That is the first test below, and the old wording for it
/// was <c>"without opening init gates []"</c>: no gate named, and the one claim it made was
/// false.</para>
/// </summary>
public class DeferredFailuresNameTheGateThatHeldThemTest : HubTestBase
{
    private readonly FailureLog log = new();

    public DeferredFailuresNameTheGateThatHeldThemTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(l =>
        {
            l.Services.AddSingleton<ILoggerProvider>(log);
            // The deferral-timeout report reaches the log as a Warning from TryReportFailure BEFORE
            // it is posted — which is the only way to read it while the hub's turn loop is parked,
            // and parking that loop is what produces the state under test.
            l.AddFilter<FailureLog>(null, LogLevel.Debug);
        });
    }

    private record GatedRequest : IRequest<GatedResponse>;

    private record GatedResponse;

    /// <summary>Occupies the gated hub's turn loop once the gate opens, until the test releases it.</summary>
    private record Blocker;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(GatedRequest), typeof(GatedResponse), typeof(Blocker));

    /// <summary>
    /// The framework budget each test drives to expiry. Derived, never a literal: it has to
    /// dominate a handful of in-process posts and one gate open (the <see cref="TestTimeouts.Quick"/>
    /// class of convergence, CI factor included) while leaving the whole test inside xunit's 30 s
    /// <c>methodTimeout</c> — the budget IS most of the test's runtime, so it cannot be
    /// <see cref="TestTimeouts.Quick"/> itself.
    ///
    /// <para>🚨 Losing that race does not make a test pass vacuously: if the budget expired before
    /// the gate opened, the report would say the gates are STILL closed and both assertions below
    /// would go red. The bound can only produce an honest failure, never a false green.</para>
    /// </summary>
    private static TimeSpan FrameworkBudget => TestTimeouts.Quick / 3;

    /// <summary>Upper bound on a wait that should complete in milliseconds — a wedge, not a pace.</summary>
    private static TimeSpan Immediate => TestTimeouts.Quick / 3;

    private const string LateGate = "gate-the-test-opens";

    private static readonly Address LateGateAddress = new("late-gate", "1");

    /// <summary>Fires once the blocker's turn has begun — proof the gate opened AND the loop is now held.</summary>
    private readonly AsyncSubject<Unit> loopParked = new();

    /// <summary>Ends the blocker's turn. Completed in a <c>finally</c> so a failing assertion cannot strand the hub.</summary>
    private readonly AsyncSubject<Unit> release = new();

    /// <summary>
    /// THE SUBJECT. A delivery whose gates have all opened, whose turn has not run yet, and whose
    /// deferral budget then expires: the report must name the gate that held it and must not claim
    /// the gates never opened.
    ///
    /// <para><b>Why every step is entailed rather than timed.</b> The blocker is posted FIRST, so
    /// the FIFO turn loop defers it first and <c>OpenGate</c> restores it to the front — its turn
    /// therefore begins before the request's, and <see cref="loopParked"/> reports that it has.
    /// <c>OpenGate(...).Should().BeTrue()</c> is the proof the gate was still closed until the test
    /// opened it, and the <c>SINCE OPENED</c> assertion is the proof the budget expired AFTER that
    /// — a report composed before the open says the gate is still closed and fails here, so the
    /// ordering is checked by the assertion rather than assumed.</para>
    /// </summary>
    [Fact]
    public async Task ADeferralTimeoutAfterTheGatesOpened_NamesTheGateThatHeldTheDelivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = GetHost();

        var gated = host.GetHostedHub(
            LateGateAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse), typeof(Blocker))
                .WithDeferralTimeout(FrameworkBudget)
                .WithInitializationGate(LateGate)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    // Registered so a pass can only come from the timeout report, never from the
                    // request quietly going unhandled.
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                })
                .WithHandler<Blocker>(async (_, d, _) =>
                {
                    loopParked.OnNext(Unit.Default);
                    loopParked.OnCompleted();
                    await release;
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        host.Post(new Blocker(), o => o.WithTarget(LateGateAddress));
        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(LateGateAddress))
            .FirstAsync()
            .Await(ct);

        // Both are DEMONSTRABLY parked before the gate is touched — otherwise the open could
        // precede a deferral and this would be testing the ordinary path.
        await WaitForDeferredBacklog(host, atLeast: 2);

        try
        {
            gated!.OpenGate(LateGate).Should().BeTrue(
                "the gate must still have been closed when the test opened it — otherwise the "
                + "report under test was composed against a gate set nobody moved");

            // The restored blocker now holds the loop, so the request sits in the main queue with
            // its tracker still armed and every gate it was parked behind already gone.
            await loopParked.Timeout(Immediate).Await(ct);

            var report = await WaitForDeferralTimeoutReport(ct);
            Output.WriteLine(report);

            report.Should().Contain(LateGate,
                "the gates a delivery was PARKED BEHIND are recorded on its tracker, and that is "
                + "the set the report is about — re-reading the live gate set here names the hub's "
                + "state now, which after the open is the empty set and reads as 'nothing was "
                + "holding it' (#3712)");
            report.Should().Contain("closed at deferral",
                "the set has to be LABELLED as the recorded one: a reader who cannot tell the "
                + "recorded set from a live read cannot use either");
            report.Should().Contain("SINCE OPENED",
                "the live read is the SECOND fact, and together they are the diagnosis — gates "
                + "still shut means a stuck gate, all opened means the hub initialised and "
                + "something is holding the turn loop, which is a different investigation. It is "
                + "also this test's ordering control: a report composed before the gate opened "
                + "cannot produce this clause");
            report.Should().NotContain("gates []",
                "the negative control — the production defect this issue was filed on is an empty "
                + "gate list, which asserts the opposite of what happened");
        }
        finally
        {
            release.OnNext(Unit.Default);
            release.OnCompleted();
        }

        // READER 2 — the stranded sender, which is in another process as often as not and can see
        // no line of this hub's log.
        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        failure.Failure.Should().NotBeNull();
        failure.Failure!.ErrorType.Should().Be(ErrorType.Unavailable,
            "a hub that has not drained a parked delivery reached no verdict, so the same request "
            + "is meaningful again — the classification must stay retryable");
        failure.Failure.Message.Should().Contain(LateGate,
            "the sender gets the same fact as the log, or the answer it can actually read is the "
            + "one without the cause");
    }

    private const string GateThatOpens = "alpha-opens";

    private const string GateThatNeverOpens = "omega-never-opens";

    private static readonly Address StartupAddress = new("startup-fails", "1");

    /// <summary>
    /// The second reader of the same drain: the startup-timeout answer. It composed ONE hub-level
    /// sentence and handed it to every parked delivery, so a delivery held by two gates — one of
    /// which opened while it waited — was told only about the one still shut, and the gate that
    /// held it for most of its wait was never named.
    ///
    /// <para><b>Both facts, separately labelled.</b> The hub-level "still closed" set is right for
    /// the HUB and stays exactly as it was; the per-delivery clause is about THIS DELIVERY and
    /// comes off its tracker. Asserting the live clause names ONLY the gate that never opened is
    /// this test's ordering control: had the startup timer fired before the test opened the first
    /// gate, that clause would carry both names and this would go red rather than green.</para>
    /// </summary>
    [Fact]
    public async Task AStartupFailure_NamesEveryGateTheDeliveryWasParkedBehind_NotOnlyTheOnesStillShut()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = GetHost();

        var gated = host.GetHostedHub(
            StartupAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                .WithStartupTimeout(FrameworkBudget)
                .WithInitializationGate(GateThatOpens)
                .WithInitializationGate(GateThatNeverOpens)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(StartupAddress))
            .FirstAsync()
            .Await(ct);

        await WaitForDeferredBacklog(host, atLeast: 1);

        gated!.OpenGate(GateThatOpens).Should().BeTrue(
            "the gate must still have been closed when the test opened it");

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        var reported = failure.Failure!.Message ?? string.Empty;
        Output.WriteLine(reported);

        reported.Should().Contain("closed at deferral",
            "the per-delivery clause is what makes the answer about THIS DELIVERY rather than "
            + "about the hub — without it the sender is handed a hub-level sentence and has to "
            + "guess which part of it applied to its own request (#3712)");
        reported.Should().Contain(GateThatOpens,
            "this gate held the delivery for most of its wait and then opened, so it is absent "
            + "from every live read and can only be reported from the recorded set");

        StillClosedClause(reported).Should().Contain(GateThatNeverOpens,
            "the hub-level fact is not traded away for the per-delivery one — a rewrite that "
            + "swapped one for the other would read as a fix");
        StillClosedClause(reported).Should().NotContain(GateThatOpens,
            "the ordering control: the opened gate must be gone from the LIVE set, which is what "
            + "proves the startup timer fired after the test opened it — otherwise this test would "
            + "pass on the unfixed code, having measured nothing");
    }

    /// <summary>The bracketed set the hub-level startup sentence reports as still shut.</summary>
    private static string StillClosedClause(string message)
    {
        var match = Regex.Match(message, @"gates still closed: \[([^\]]*)\]");
        match.Success.Should().BeTrue(
            $"the startup failure must still carry the hub's own gate set; message was: {message}");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// Polls the public disposal diagnostics (which report <c>deferred=&lt;N&gt;</c> per hub,
    /// walking hosted hubs) until at least <paramref name="atLeast"/> deliveries are parked on one
    /// hub. The gated hub is the only hub in these tests whose gate does not open on its own, so a
    /// count that high is unambiguously ours.
    /// </summary>
    private static async Task WaitForDeferredBacklog(IMessageHub host, int atLeast)
    {
        await Observable.Interval(TestTimeouts.Quick / 100)
            .StartWith(0L)
            .Select(_ => host.GetDisposalDiagnostics())
            .Where(snapshot => Regex.Matches(snapshot, @"deferred=(\d+)")
                .Any(m => int.Parse(m.Groups[1].Value) >= atLeast))
            .Take(1)
            .Timeout(Immediate)
            .Await(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The deferral-timeout answer as the hub writes it, read off the log rather than off the
    /// caller's exception — the report is produced while the hub's turn loop is deliberately
    /// parked, so the posted <c>DeliveryFailure</c> cannot be routed until the test releases it.
    /// <c>TryReportFailure</c> logs the full text one line before it posts.
    /// </summary>
    private async Task<string> WaitForDeferralTimeoutReport(System.Threading.CancellationToken ct)
    {
        // 🚨 The needle is the part BOTH the old and the new wording share ("Hub <addr> deferred"),
        // never a phrase only the fix produces. A wait that matches the cure cannot distinguish
        // "the report is wrong" from "no report at all", and the diagnosis the reader needs lives
        // in the assertions below, not in an anonymous timeout.
        var needle = $"Hub {LateGateAddress} deferred";
        return await Observable.Interval(TestTimeouts.Quick / 100)
            .StartWith(0L)
            .Select(_ => log.Lines.FirstOrDefault(l => l.Contains(needle, StringComparison.Ordinal)))
            .Where(line => line is not null)
            .Select(line => line!)
            .Take(1)
            .Timeout(FrameworkBudget + Immediate)
            .Await(ct);
    }

    private sealed class FailureLog : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> lines = new();

        public IEnumerable<string> Lines => lines.ToArray();

        public ILogger CreateLogger(string categoryName) => new Capture(lines);

        public void Dispose() { }

        private sealed class Capture(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;

            public void Log<TState>(LogLevel level, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Enqueue(formatter(state, exception));
        }
    }
}
