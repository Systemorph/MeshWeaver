using System;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Systemorph/MeshWeaver#4261 — a retirement that fails its gate AFTER starting the teardown loses
/// the CAUSE, and the requester is told the generic disposal sentence instead.
///
/// <para><b>The production shape.</b> <c>DataContext.SettleInitializationGate</c> and
/// <c>MessageHub.TryRetireAfterTransientInitializationFault</c> both retire an activation whose
/// initialization met a TRANSIENT infrastructure fault (the database away, a name unresolved): the
/// backlog parked behind the init gate is answered "ask again, because &lt;the database&gt;", and the
/// hub disposes so demand routing re-creates it. Both used to call <c>Dispose()</c> FIRST and
/// <c>FailGate</c> SECOND, for one stated reason — the drain classified the refusal by reading
/// <c>hub.IsShuttingDown</c> at drain time, so the teardown had to have started for the answer to
/// come out transient rather than terminal.</para>
///
/// <para><b>Why that ordering could not hold.</b> The two calls do not run on the same thread.
/// <c>SettleInitializationGate</c> runs on the thread pool (<c>OpenInitializationGate</c> ends with
/// <c>ObserveOn(TaskPoolScheduler.Default)</c>), while <c>Dispose()</c> only POSTS a
/// <c>ShutdownRequest</c> whose turn reaches <c>MessageService.Dispose</c> later, on the hub's
/// action block. Both ends drain the SAME deferred backlog through
/// <c>DrainDeferredDeliveries</c>, whose <c>TryRemove</c> gives each delivery to exactly one of
/// them. Measured on a failing run: the two were 1 ms apart.</para>
///
/// <para><b>And the loss is total, not partial</b> — which is what these two facts pin.
/// <c>MessageService.Dispose</c> OPENS every remaining gate before it drains, so a <c>FailGate</c>
/// that arrives afterwards finds no gate to fail: it returns <c>false</c> having recorded nothing.
/// The specific cause is not merely outraced, it is never stored at all. Failing the gate FIRST —
/// which is only sound once the classification is STATED rather than derived — removes the race by
/// construction, because at that instant no teardown exists that could answer the backlog
/// differently.</para>
/// </summary>
public class FailedGateAnswersBeforeTheTeardownTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record GatedRequest : IRequest<GatedResponse>;

    private record GatedResponse;

    private const string NeverOpens = "test-never-opens";

    /// <summary>The sentence a retirement wants its requester to read.</summary>
    private const string TheCause =
        "its DataContext initialization met a transient infrastructure fault (TransientConnectionException)";

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).WithTypes(typeof(GatedRequest), typeof(GatedResponse));

    private IMessageHub GatedHub(IMessageHub host, Address address, bool selfPosting = false)
    {
        var gated = host.GetHostedHub(
            address,
            c =>
            {
                c = c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                    .WithInitializationGate(NeverOpens, _ => false)
                    // A handler that WOULD answer, so a pass can only come from the refusal and
                    // never from the request being served.
                    .WithHandler<GatedRequest>((h, d) =>
                    {
                        h.Post(new GatedResponse(), o => o.ResponseFor(d));
                        return d.Processed();
                    });
                // PostPipeline fails closed with no AccessContext, and the self-posting cases below
                // post on the hub's own behalf — the same infrastructure identity a probe carries.
                return selfPosting ? c.WithPostingIdentity(PostingIdentity.System) : c;
            });
        gated.Should().NotBeNull();
        return gated!;
    }

    /// <summary>
    /// THE DEFECT. With the teardown already run, <c>FailGate</c> has nothing left to say: the gate
    /// it would fail was opened by <c>MessageService.Dispose</c> on its way to the drain, so the
    /// call reports <c>false</c> and the requester keeps the generic disposal sentence.
    ///
    /// <para>Deterministic by waiting for <c>DisposalCompleted</c> rather than by racing it — the
    /// production window is the same state reached 1 ms sooner.</para>
    /// </summary>
    [Fact]
    public async Task AfterTheTeardown_FailGateCanNoLongerNameTheCause()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = GetHost();
        var gated = GatedHub(host, new Address("gated-after", "1"));

        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(gated.Address))
            .FirstAsync()
            .Await(ct);
        await WaitForDeferredBacklog(host);

        gated.Dispose();
        await gated.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        gated.FailGate(NeverOpens, TheCause, ErrorType.ShuttingDown).Should().BeFalse(
            "MessageService.Dispose opens every remaining gate before it drains, so a FailGate that "
            + "arrives after the teardown records nothing at all — the cause is not outraced, it is "
            + "discarded");

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        failure.Failure!.Message.Should().NotContain("transient infrastructure fault",
            "this is the loss #4261 names: the requester is told the generic disposal sentence "
            + "instead of the cause the retirement exists to carry");
        failure.Failure.Message.Should().Contain("was disposed while",
            "the generic sentence is what it does get — asserted positively so 'the cause is "
            + "missing' cannot be satisfied by the requester hearing nothing at all");
    }

    /// <summary>
    /// THE CONTRACT, and the order the two retirement sites now use: the backlog is answered with
    /// the CAUSE before any teardown exists that could answer it differently. The assertion is made
    /// BEFORE <c>Dispose()</c> is called at all, so nothing here is a race that happened to be won.
    /// </summary>
    [Fact]
    public async Task FailingTheGateFirst_TheRequesterLearnsTheCause()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = GetHost();
        var gated = GatedHub(host, new Address("gated-before", "1"));

        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(gated.Address))
            .FirstAsync()
            .Await(ct);
        await WaitForDeferredBacklog(host);

        gated.FailGate(NeverOpens, TheCause, ErrorType.ShuttingDown).Should().BeTrue(
            "the gate is still there to fail — which is the whole reason this call comes first");

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        failure.Failure!.Message.Should().Contain("transient infrastructure fault",
            "the refusal names the cause the caller should expect to clear");
        failure.Failure.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "a retired activation is transient — 'ask again', never 'this address is broken'");

        // The teardown follows, and finds nothing left to answer.
        gated.Dispose();
        await gated.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }

    /// <summary>
    /// Why failing the gate first is only SOUND once the classification is stated: on the fallback
    /// carrier the refusal used to be classified from <c>hub.IsShuttingDown</c> AT DRAIN TIME, so a
    /// backlog answered before the teardown started came out <see cref="ErrorType.Failed"/> —
    /// terminal, the exact verdict the retirement exists to avoid.
    ///
    /// <para>The fallback is reached here because the sender IS the hub
    /// (<c>NackThroughParent</c> declines <c>sender-is-self</c> and <c>ReportFailure</c> classifies
    /// instead). That is not a contrivance: it is the <c>$model-probe</c> shape #4178 measured 76
    /// times per gate shard.</para>
    /// </summary>
    [Fact]
    public async Task TheClassificationIsStatedByTheFailer_NotDerivedFromTheRunLevel()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = GetHost();
        var gated = GatedHub(host, new Address("gated-stated", "1"), selfPosting: true);

        var response = gated
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(gated.Address))
            .FirstAsync()
            .Await(ct);
        await WaitForDeferredBacklog(host);

        gated.IsShuttingDown.Should().BeFalse(
            "the point of the fix is that the gate is failed while the hub is still whole");
        gated.FailGate(NeverOpens, TheCause, ErrorType.ShuttingDown).Should().BeTrue();

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        failure.Failure!.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "the classification travels with the gate failure — a hub that has not begun its "
            + "teardown can still declare a refusal transient, which is what lets the backlog be "
            + "answered before the teardown starts");
    }

    /// <summary>
    /// The CONTROL for the test above, and the reason the stated form had to be added rather than
    /// the ordering simply swapped: the two-argument <c>FailGate</c> still derives, so the very
    /// same retirement, failing its gate first without stating a classification, hands the caller
    /// the TERMINAL <see cref="ErrorType.Failed"/>.
    /// </summary>
    [Fact]
    public async Task WithoutAStatedClassification_TheSameRefusalComesOutTerminal()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = GetHost();
        var gated = GatedHub(host, new Address("gated-derived", "1"), selfPosting: true);

        var response = gated
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(gated.Address))
            .FirstAsync()
            .Await(ct);
        await WaitForDeferredBacklog(host);

        gated.FailGate(NeverOpens, TheCause).Should().BeTrue();

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        failure.Failure!.ErrorType.Should().Be(ErrorType.Failed,
            "derived from a hub that is not shutting down, the refusal is terminal — which is "
            + "exactly why the retirement sites used to call Dispose() first, and exactly what the "
            + "stated classification replaces");
    }

    /// <summary>
    /// Polls the public disposal diagnostics (they report <c>deferred=&lt;N&gt;</c> per hub, walking
    /// hosted hubs) until something is parked. The gated hub is the only hub in each test that
    /// defers, so a non-zero count is unambiguously our request.
    /// </summary>
    private static async Task WaitForDeferredBacklog(IMessageHub host)
    {
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Select(_ => host.GetDisposalDiagnostics())
            .Where(snapshot => Regex.Matches(snapshot, @"deferred=(\d+)")
                .Any(m => int.Parse(m.Groups[1].Value) > 0))
            .Take(1)
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
    }
}
