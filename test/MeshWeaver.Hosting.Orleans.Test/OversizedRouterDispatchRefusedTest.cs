using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 An oversized delivery is refused BEFORE the router's own grain call — issue #2885.
///
/// <para><b>The incident.</b> On 2026-08-31 <c>OrleansRoutingService</c> logged
/// <c>Failed to deliver to import/xDAfkqsVUE-OMBHb0mVtSg</c> with a
/// <c>System.OutOfMemoryException</c> raised at <c>GC.AllocateNewArray</c> →
/// <c>SharedArrayPool.Rent</c> → <c>Utf8JsonWriter.TranscodeAndWriteRawValue</c>, beneath
/// <c>MessageDeliveryConverter.Write</c>. <c>import/{meshHubId}</c> is the bulk-import hub, so the
/// producer is the part of the system that legitimately moves large datasets.</para>
///
/// <para><b>Why the #2897 guard did not catch it, and why that was a PLACEMENT bug rather than a
/// bound bug.</b> <c>RoutingGrain</c> refuses an oversized body on both forward legs — but the hop
/// that died is how a delivery REACHES <c>RoutingGrain</c>: <c>DispatchObservable</c> calls
/// <c>IRoutingGrain.RouteMessage(delivery)</c>, and Orleans serialises that ARGUMENT with the mesh's
/// own System.Text.Json options (<c>AddJsonSerializer(_ =&gt; true, …)</c> claims every type). The
/// packaged <see cref="RawJson"/> therefore goes through <c>RawJsonConverter.WriteRawValue(string)</c>
/// and is transcoded UTF-16 → UTF-8 against a rent of up to 3 bytes per char. That rent is the
/// allocation that threw. All three guarded sites are strictly DOWNSTREAM of it, so none of them
/// ever executed.</para>
///
/// <para><b>Why refusal, and not a bigger buffer or a catch.</b> The bound is the transport's own
/// <c>MaxMessageBodySize</c>, so a payload at or over it is already undeliverable — refusing only
/// converts a silent loss into a loud, attributable one, and
/// <see cref="A_delivery_that_fits_still_reaches_grain_placement"/> is the control that proves
/// ordinary routing is untouched. Catching the <c>OutOfMemoryException</c> would suppress a symptom
/// while leaving the pod's allocation failure in place; raising the limit would make such frames
/// normal traffic.</para>
///
/// <para>🚨 <b>What these tests do NOT establish.</b> The incident's stack carries no payload size,
/// so it cannot be shown that this bound would have refused THAT delivery: the transcode peaks at
/// ~3× the payload, so a body under the frame limit can still exhaust a memory-pressured pod. What
/// is established is that the leg is now bounded and attributable at all, where before it was
/// neither. The residual — an import that builds one delivery whole instead of batching — is
/// producer-side work that stays open on #2885.</para>
///
/// <para><b>No cluster, no mocks, no timing.</b> The real <see cref="OrleansRoutingService"/> is
/// driven with a <c>null</c> <see cref="IGrainFactory"/>, exactly as
/// <c>OrleansRoutingShutdownClassificationTest</c> does: reaching grain placement is observable as
/// the <see cref="NullReferenceException"/> it raises, so "was refused before the grain call" and
/// "reached the grain call" are BOTH directly assertable rather than inferred from silence. The
/// receiving hub is a real <see cref="IMessageHub"/> at the sender's address, so the NACK asserted
/// below is the actual <see cref="DeliveryFailure"/> the router posts.</para>
/// </summary>
public class OversizedRouterDispatchRefusedTest : TestBase
{
    private static readonly Address SenderAddress = new("portal", "import-producer");

    /// <summary>The incident's target: <c>StaticRepoImporter</c>'s dedicated bulk-import hub.</summary>
    private static readonly Address TargetAddress = new("import", "xDAfkqsVUE-OMBHb0mVtSg");

    /// <summary>
    /// The bound the router is driven at. <see cref="OrleansRoutingService.GrainBodyLimitBytes"/> is
    /// an instance test seam — the same shape as <c>CanHostGrains</c> — so the decision path runs
    /// exactly as production runs it without allocating 100 MiB on a shared build machine. The real
    /// default is pinned separately by
    /// <c>OversizedGrainDeliveryRefusedTest.The_orleans_body_size_default_is_what_the_fallback_is_calibrated_against</c>.
    /// </summary>
    private const int TestLimitBytes = 4096;

    /// <summary>
    /// Over the 1 MiB memory-stream block, so ONE fixture drives both halves of the fix: the
    /// router's refusal (measured against <see cref="TestLimitBytes"/>) and the stripping of the
    /// NACK's echoed payload (measured against the tighter default bound the report itself must
    /// survive).
    /// </summary>
    private const int OversizedPayloadBytes = 1_200_000;

    // RunContinuationsAsynchronously: without it everything awaiting this TCS resumes INLINE on the
    // hub's message-handling thread, so the awaiting test body would run on the single-threaded
    // action block it is still driving. Same reason as OrleansRoutingShutdownClassificationTest.
    /// <summary>
    /// The NACK the router posts back, as an <see cref="AsyncSubject{T}"/> rather than a
    /// <c>TaskCompletionSource</c>.
    ///
    /// <para>🚨 A TCS is a hand-woven async gate: its continuation can resume INLINE on the hub
    /// thread that completed it, inside the action block, which is the shape AGENTS.md forbids in
    /// `test/` as well as `src/`. An AsyncSubject carries the value the same way, replays it to a
    /// late subscriber, and is awaited through the repo's reactive assertion — so the wait is
    /// bounded by the assertion's own timeout and its failure names what did not arrive.</para>
    /// </summary>
    private readonly AsyncSubject<DeliveryFailure> nack = new();

    public OversizedRouterDispatchRefusedTest(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton<AccessService>();
        // The hub sits AT the sender address, so the DeliveryFailure the router posts back to
        // delivery.Sender lands on this handler — the real NACK, not a stand-in.
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(SenderAddress, conf => conf
            .WithHandler<DeliveryFailure>((_, d) => { nack.OnNext(d.Message); nack.OnCompleted(); return d.Processed(); })
            .WithPostingIdentity(PostingIdentity.System)));
    }

    /// <summary>
    /// The real router, with the null grain factory as the placement probe — see the class remarks.
    /// </summary>
    private OrleansRoutingService CreateRouter() =>
        new(null!, ServiceProvider, ServiceProvider.GetRequiredService<ILogger<OrleansRoutingService>>())
        {
            GrainBodyLimitBytes = TestLimitBytes
        };

    /// <summary>
    /// A routed delivery whose packaged payload is <paramref name="payloadBytes"/> of ASCII JSON —
    /// the shape the router actually sees, since <c>MeshBuilder</c> packages every delivery to
    /// <see cref="RawJson"/> before it reaches the routing service. The content is VALID JSON
    /// because <c>RawJsonConverter</c> writes it with <c>WriteRawValue</c>, which validates.
    /// </summary>
    private static IMessageDelivery DeliveryOf(int payloadBytes, string id = "d-import")
    {
        const string head = "{\"$type\":\"StaticRepoImportPayload\",\"nodes\":\"";
        const string tail = "\"}";
        var filler = new string('x', Math.Max(0, payloadBytes - head.Length - tail.Length));
        var json = head + filler + tail;
        Encoding.UTF8.GetByteCount(json).Should().Be(payloadBytes, "the fixture is exact ASCII");
        return new MessageDelivery<RawJson>(
            SenderAddress, TargetAddress, new RawJson(json), JsonSerializerOptions.Default) with
        { Id = id };
    }

    /// <summary>
    /// 🚨 THE regression. A body the transport cannot carry must never be handed to
    /// <c>IRoutingGrain.RouteMessage</c>, because serialising that argument is what allocated — and
    /// failed to allocate — in production.
    ///
    /// <para>Against <c>origin/main</c> the dispatch is issued and the null probe throws, i.e. the
    /// oversized delivery reaches exactly the call that OOM'd. The absence of that throw is the
    /// fix.</para>
    /// </summary>
    [Fact]
    public async Task The_oversized_delivery_never_reaches_grain_placement()
    {
        var routing = CreateRouter();

        var result = await routing.DeliverMessage(DeliveryOf(OversizedPayloadBytes)).FirstAsync().Await();

        result.State.Should().Be(MessageDeliveryState.Failed,
            "a delivery the transport provably cannot carry must be answered terminally here, not "
            + "reported Forwarded and then lost inside a serializer that cannot allocate for it");
        result.SenderWasNacked.Should().BeTrue(
            "this path posts its own DeliveryFailure, so it must declare that — otherwise whoever "
            + "finishes the delivery NACKs the sender a second time");
    }

    /// <summary>
    /// The sender must be told, and told TERMINALLY. The size is a property of the message, not of
    /// the attempt, so a transient verdict would arm the caller's recovery machinery to retry
    /// something that can never converge — which is the reconnect-and-resend loop #2897 recorded on
    /// the neighbouring leg.
    /// </summary>
    [Fact]
    public async Task The_sender_is_nacked_terminally_as_rejected()
    {
        var routing = CreateRouter();

        await routing.DeliverMessage(DeliveryOf(OversizedPayloadBytes)).FirstAsync().Await();

        var failure = await nack.Should().Within(10.Seconds())
            .Emit("the router must post a DeliveryFailure back to the sender for an oversized dispatch");
        failure.ErrorType.Should().Be(ErrorType.Rejected,
            "an oversized body cannot become deliverable on a retry; ShuttingDown or a transient "
            + "classification would make the sender retry it forever");
        failure.Message.Should().Contain(OversizedPayloadBytes.ToString("N0"),
            "the refusal says HOW BIG the thing it dropped was — the fact the OutOfMemoryException "
            + "stack could not supply");
        failure.Message.Should().Contain(TestLimitBytes.ToString("N0"),
            "…and what limit it was measured against, so a reader can tell gross from marginal");
        failure.Message.Should().Contain(TargetAddress.ToString(),
            "…and where it was going — the incident's only clue was the address");
        failure.Message.Should().Contain("d-import", "…and which delivery it was");
    }

    /// <summary>
    /// 🚨 The NACK must not BE the thing it is reporting. <see cref="DeliveryFailure"/> embeds the
    /// ORIGINAL delivery, payload and all, and travels the same transports the original could not
    /// survive — so an un-stripped report about an oversized payload re-runs the very 3×-payload
    /// transcode that OOM'd the pod, turning one refusal into a second allocation failure.
    ///
    /// <para><c>RoutingGrain.PostFailure</c> has stripped its echo since #1890;
    /// <c>OrleansRoutingService.SendDeliveryFailure</c> did not, which is the "a fix landed on one
    /// site and missed the other" asymmetry this closes. Against <c>origin/main</c> the full
    /// 1.2 MB filler comes back on the failure report.</para>
    /// </summary>
    [Fact]
    public async Task The_nack_does_not_echo_the_oversized_payload()
    {
        var routing = CreateRouter();

        await routing.DeliverMessage(DeliveryOf(OversizedPayloadBytes)).FirstAsync().Await();

        var failure = await nack.Should().Within(10.Seconds())
            .Emit("the router must post a DeliveryFailure back to the sender for an oversized dispatch");
        var echoed = JsonSerializer.Serialize(failure.Delivery.Message);

        echoed.Should().NotContain(new string('x', 10_000),
            "the report must not carry the payload it is reporting on — that is how a failure "
            + "report about an undeliverable message becomes an undeliverable message");
        echoed.Should().Contain("payloadOmitted",
            "…and it must say the payload was replaced rather than silently dropping the field, so "
            + "a reader is not left wondering which message this was about");
        echoed.Should().Contain(OversizedPayloadBytes.ToString(),
            "…keeping the size, which is the fact that explains the refusal");
    }

    /// <summary>
    /// The control for every guard above: a delivery that FITS still goes to grain placement, so
    /// the refusal is scoped to what the transport cannot carry and is not quietly swallowing
    /// ordinary routing. Reaching placement is observable as the null probe's throw.
    ///
    /// <para>Without this, "the router refuses oversized deliveries" and "the router refuses
    /// everything" are indistinguishable — and a guard whose control cannot fail is not a guard.</para>
    /// </summary>
    [Fact]
    public async Task A_delivery_that_fits_still_reaches_grain_placement()
    {
        var routing = CreateRouter();

        await Assert.ThrowsAsync<NullReferenceException>(
            () => routing.DeliverMessage(DeliveryOf(TestLimitBytes - 1)).FirstAsync().Await());
    }
}
