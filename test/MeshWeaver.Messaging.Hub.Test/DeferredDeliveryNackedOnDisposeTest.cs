using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Deterministic repro for the SILENT-ABANDONMENT hang: a hub that is disposed while a
/// request is still parked behind its initialization gates used to throw that request away
/// without telling anyone, so the sender's <c>hub.Observe(...)</c> had nothing to resolve on
/// and burned its entire request budget in total silence.
///
/// <para><b>Why this is a production bug, not a test artefact.</b> <see cref="DisposeRequest"/>
/// is deliberately exempt from the init gate (deferring it would break disposal) while an
/// ordinary request is NOT. So any recycle that lands during a hub's activation window jumps
/// the queue and annihilates the very request that triggered the activation — and every recycle
/// path is affected: <c>NodeTypeEnrichmentHelpers.WithOverlaySelfHeal</c>'s self-recycle,
/// <c>RecycleLayoutArea</c>, the MCP <c>recycle</c> tool, a node delete. The caller — a page
/// load, a <c>GetMeshNode</c> read — then spins for its whole budget with no error to show.</para>
///
/// <para><b>Field evidence.</b> <c>ThreadAgentIntegrationTest</c> on CI: the
/// <c>ACME/ProductLaunch</c> instance hub was created, handed the routed <c>GetDataRequest</c>,
/// and self-disposed 13 ms later via the overlay self-heal watcher. The reader sat idle for its
/// full 60 s (process memory flat throughout — nothing was running) and the timeout diagnostic
/// reported <c>Target: NO LOCAL HUB</c>.</para>
///
/// <para><b>The contract pinned here.</b> The abandoned delivery is NACKed through the PARENT
/// hub (our own Post would re-enter the disposing service's shutdown gate and be dropped) with
/// the TRANSIENT <see cref="ErrorType.ShuttingDown"/> — never NotFound, never a generic terminal
/// failure. A recycled address reactivates on the next access, so the sender must read this as
/// "ask again", not "gone"; consumers with their own recovery machinery (SynchronizationStream's
/// resubscribe latch) rely on exactly that classification.</para>
/// </summary>
public class DeferredDeliveryNackedOnDisposeTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record GatedRequest : IRequest<GatedResponse>;

    private record GatedResponse;

    private static readonly Address GatedAddress = new("gated", "1");

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).WithTypes(typeof(GatedRequest), typeof(GatedResponse));

    [Fact(Timeout = 30_000)]
    public async Task DeferredRequest_IsNacked_WhenHubIsDisposedBeforeItsGateOpens()
    {
        var host = GetHost();

        // A hosted hub whose gate never opens — the activation window, held open forever so the
        // race is deterministic rather than millisecond-timed. It registers a handler that WOULD
        // answer, so a pass can only come from the NACK, never from the request being served.
        var gated = host.GetHostedHub(
            GatedAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                .WithInitializationGate("test-never-opens", _ => false)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        // Pre-registers the callback, then posts. The request lands on the gated hub and defers.
        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(GatedAddress))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        // Wait until the request is DEMONSTRABLY parked in the deferred queue before recycling.
        // Without this the test could dispose before the delivery was ever accepted, which is the
        // already-handled intake-gate case (ScheduleNotify NACKs that one) — a different code path
        // that would pass even with the defect present.
        await WaitForDeferredBacklog(host);

        // The recycle. DisposeRequest bypasses the gate, so it overtakes the deferred request and
        // tears the hub down with the request still inside it.
        host.Post(new DisposeRequest(), o => o.WithTarget(GatedAddress));

        // WITHOUT the fix this task never completes and the [Fact] timeout fires with no
        // explanation — precisely the production symptom (a page that spins).
        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);

        // TRANSIENT, so a recycled address reads as "retry", never as "gone".
        failure.Failure.Should().NotBeNull();
        failure.Failure!.ErrorType.Should().Be(ErrorType.ShuttingDown);
        failure.Failure.Message.Should().Contain("deferred",
            "the NACK must name WHY the message was abandoned — a bare failure sends the next "
            + "investigator hunting the wrong layer");
    }

    /// <summary>
    /// Polls the public disposal diagnostics (which report <c>deferred=&lt;N&gt;</c> per hub,
    /// walking hosted hubs) until something is parked. The gated hub is the only hub in this test
    /// that defers, so a non-zero count is unambiguously our request.
    /// </summary>
    private static async Task WaitForDeferredBacklog(IMessageHub host)
    {
        for (var i = 0; i < 100; i++)
        {
            foreach (Match m in Regex.Matches(host.GetDisposalDiagnostics(), @"deferred=(\d+)"))
                if (int.Parse(m.Groups[1].Value) > 0)
                    return;
            await Task.Delay(50);
        }

        Assert.Fail(
            "The request never reached the gated hub's deferred queue, so this test never "
            + "exercised the disposal path it exists to pin. Check that GatedRequest still "
            + "defers behind a closed initialization gate.");
    }
}
