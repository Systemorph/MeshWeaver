using System;
using System.Text;
using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 THE FAILURE REPORT MUST SURVIVE THE FAILURE IT REPORTS — issues #3044 and #3049.
///
/// <para><b>The incident.</b> On 2026-09-02, three times in ninety seconds and then once more
/// fourteen minutes later, a portal pod logged
/// <c>Failed to post DeliveryFailure message for RawJson (ID: …) — breaking error cascade</c> with a
/// <c>System.OutOfMemoryException</c> underneath it. The sender was left with neither its message
/// nor any notification that the message had been lost: from its side the delivery simply vanished
/// and its <c>Observe(...)</c> waited out its budget. Two frames were recorded, and they are two
/// halves of one defect — <c>JsonReaderHelper.TranscodeHelper</c> under
/// <c>MessageService.Post</c> (#3044) and <c>MessageDeliveryConverter.Write</c> →
/// <c>Utf8JsonWriter.TranscodeAndWriteRawValue</c> → <c>SharedArrayPool.Rent</c> →
/// <c>GC.AllocateNewArray</c> (#3049).</para>
///
/// <para><b>Three defects, all on the reporting path, none of them the transport.</b></para>
/// <list type="number">
///   <item><b>The NACK carried the payload it was reporting on.</b> The rule "a NACK about an
///     oversized message must not BE one" shipped twice as a hand-applied call at ONE site each
///     (<c>RoutingGrain.PostFailure</c> #1890, <c>OrleansRoutingService.SendDeliveryFailure</c>
///     #2885). <c>MessageService.ReportFailure</c> — the hub's own reporter, and the site all four
///     production occurrences ran through — was neither of them. With around twenty
///     <c>new DeliveryFailure(delivery)</c> sites in the repository, "remember to strip" was never
///     a control; it is now the record's construction invariant.</item>
///   <item><b><c>Post</c> rendered every delivery to JSON for a log line nobody read.</b> The
///     render was a method ARGUMENT, so it ran before <c>LogDebug</c> could discard it — on every
///     post in the process, with Debug off, in production.</item>
///   <item><b><c>[PreventLogging]</c> did not reach <see cref="RawJson"/>.</b>
///     <see cref="LoggingTypeInfoResolver"/> strips marked members by removing properties from a
///     resolved <c>JsonTypeInfo</c>, and a type claimed by a custom converter has no properties to
///     remove — so the attribute on <c>RawJson.Content</c>, whose own doc comment promises the
///     payload is never dumped, was inert.</item>
/// </list>
///
/// <para><b>Why none of these is fixed by a bound.</b> An OOM during serialisation means the
/// ALLOCATION was the failure, so refusing the delivery afterwards is too late — and a
/// <c>try/catch</c> around it (which the incident's own log line already is) turns a lost message
/// into a lost message plus a lost report. The fix is to stop doing the allocation: a log nobody
/// reads is not rendered, a payload is never a log's content, and a report about a message does not
/// carry the message.</para>
/// </summary>
public class OversizedFailureReportSurvivesTest : TestBase
{
    private static readonly Address SenderAddress = new("portal", "import-producer");
    private static readonly Address TargetAddress = new("import", "GpdhJsyWQUuhnvNbsiY6HQ");

    /// <summary>
    /// Comfortably over the 1 MiB memory-stream block — the tighter of the two transport bounds and
    /// the one a failure report itself must survive.
    /// </summary>
    private const int OversizedPayloadBytes = 1_200_000;

    /// <summary>Well under every bound: the control that proves ordinary reporting is untouched.</summary>
    private const int SmallPayloadBytes = 512;

    public OversizedFailureReportSurvivesTest(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton<AccessService>();
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(SenderAddress, conf => conf
            .WithPostingIdentity(PostingIdentity.System)));
    }

    private IMessageHub Hub => ServiceProvider.GetRequiredService<IMessageHub>();

    /// <summary>
    /// A routed delivery whose packaged payload is <paramref name="payloadBytes"/> of ASCII JSON —
    /// the shape a reporter actually sees, since <c>MeshBuilder</c> packages every delivery to
    /// <see cref="RawJson"/> before it reaches a transport. The content is VALID JSON because
    /// <c>RawJsonConverter</c> writes it with <c>WriteRawValue</c>, which validates.
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

    private static string PayloadOf(IMessageDelivery delivery) =>
        delivery.Message is RawJson raw ? raw.Content : string.Empty;

    /// <summary>
    /// 🚨 THE INVARIANT, at the type rather than at a call site. Constructing a
    /// <see cref="DeliveryFailure"/> about an undeliverable payload must not produce an
    /// undeliverable failure report.
    ///
    /// <para>Against <c>origin/main</c> this fails: <c>new DeliveryFailure(oversized)</c> keeps the
    /// whole body, because stripping was something two of the roughly twenty construction sites
    /// happened to do first.</para>
    /// </summary>
    [Fact]
    public void A_failure_report_does_not_carry_the_payload_it_reports_on()
    {
        var oversized = DeliveryOf(OversizedPayloadBytes);

        var report = new DeliveryFailure(oversized, "the transport could not carry it");

        PayloadOf(report.Delivery).Length.Should().BeLessThan(OversizedPayloadBytes,
            "a report about a message that could not be delivered must not itself be a message that "
            + "cannot be delivered — it travels the SAME transport back to the sender and dies at "
            + "exactly the wall it is describing, leaving the producer with neither the message nor "
            + "the report (#1890, and again as #3044/#3049 at MessageService.ReportFailure)");
    }

    /// <summary>
    /// A stripped report is still an ATTRIBUTABLE one. The byte count says how far over the message
    /// was, and the head carries the <c>$type</c> discriminator — which is what identifies the
    /// producer. A NACK that merely said "too big" would replace one blind spot with another.
    /// </summary>
    [Fact]
    public void A_stripped_report_still_names_what_it_dropped()
    {
        var report = new DeliveryFailure(DeliveryOf(OversizedPayloadBytes));

        var echoed = PayloadOf(report.Delivery);

        echoed.Should().Contain(OversizedPayloadBytes.ToString(),
            "the exact size is the fact the Orleans-side rejection could never supply — it knows a "
            + "queue id and nothing else");
        echoed.Should().Contain("StaticRepoImportPayload",
            "the $type discriminator sits at the front of the JSON and is what makes the PRODUCER "
            + "identifiable; a refusal nobody can act on is not a refusal");
        report.Delivery.Id.Should().Be("d-import", "the delivery id must survive the strip");
        report.Delivery.Sender.Should().Be(SenderAddress, "so does the sender");
    }

    /// <summary>
    /// THE CONTROL. An ordinary failure report is unchanged — the strip is reached only where
    /// carrying the payload is what would lose the report, so nothing that works today behaves
    /// differently.
    /// </summary>
    [Fact]
    public void An_ordinary_failure_report_still_echoes_its_delivery()
    {
        var small = DeliveryOf(SmallPayloadBytes, "d-small");

        var report = new DeliveryFailure(small, "handler threw");

        report.Delivery.Should().BeSameAs(small,
            "a payload that fits is echoed unchanged — the invariant must be invisible to every "
            + "ordinary NACK, or it is a behaviour change rather than a fix");
    }

    /// <summary>
    /// 🚨 <c>[PreventLogging]</c> REACHES <see cref="RawJson"/> — the third defect.
    ///
    /// <para><c>RawJson.Content</c> has carried <c>[PreventLogging]</c> since it was written, and
    /// its doc comment states the intent: "logging it in full is just re-dumping the message as a
    /// string". The attribute did nothing. <see cref="LoggingTypeInfoResolver"/> strips marked
    /// members by removing properties from a resolved <c>JsonTypeInfo</c>, which requires
    /// <c>Kind == Object</c>; a type claimed by a custom <c>JsonConverter&lt;T&gt;</c> has
    /// <c>Kind == None</c> and no properties at all, so there was nothing to remove and
    /// <c>RawJsonConverter</c> went on emitting the body verbatim.</para>
    ///
    /// <para>Against <c>origin/main</c> the render is the whole 1.2 MB payload.</para>
    /// </summary>
    [Fact]
    public void The_logging_options_never_render_a_raw_payload()
    {
        var delivery = DeliveryOf(OversizedPayloadBytes);

        var rendered = JsonSerializer.Serialize(delivery, Hub.CreateLoggingSerializerOptions());

        rendered.Length.Should().BeLessThan(OversizedPayloadBytes / 100,
            "the cost of rendering a log line must not depend on the size of the message — "
            + "WriteRawValue transcodes UTF-16 → UTF-8 at up to 3 bytes per char out of the shared "
            + "array pool, which is the allocation that threw OutOfMemoryException on the ERROR "
            + "path, where the process is already in trouble");
        rendered.Should().Contain("StaticRepoImportPayload",
            "redaction must not cost attribution: the head of the payload carries the $type "
            + "discriminator, and a log line that cannot identify the message is worse than a "
            + "bounded one");
        rendered.Should().Contain("contentOmitted",
            "the line must SAY that it elided the body — a silent truncation reads as a small "
            + "message and sends the next reader looking in the wrong place");
    }

    /// <summary>
    /// 🚨 POSTING DOES NOT RENDER — the second defect, measured as allocation, which is what
    /// actually failed in production.
    ///
    /// <para><c>Post</c> read
    /// <c>logger.LogDebug("…", JsonSerializer.Serialize(ret, LoggingSerializerOptions), …)</c>.
    /// Arguments are evaluated BEFORE the call, so the render ran on every post in the process and
    /// was then discarded by the logger whenever Debug was off — which in production it always is.
    /// A defect of this shape is invisible to every functional test: the behaviour is correct, the
    /// output is correct, and the only symptom is a multiple of the payload in transient
    /// allocation. So it is measured directly.</para>
    ///
    /// <para><c>GC.GetAllocatedBytesForCurrentThread</c> is an exact per-thread counter, not a
    /// sample, and <c>MessageService.Post</c> is synchronous on the calling thread — so this is a
    /// deterministic fact and not a timing one. The bound is deliberately loose (one payload, where
    /// the old path cost at least two): the assertion is "the payload is not copied", not a
    /// budget.</para>
    /// </summary>
    [Fact]
    public void Posting_a_large_delivery_does_not_render_its_payload()
    {
        var hub = Hub;
        var payload = new RawJson(PayloadOf(DeliveryOf(OversizedPayloadBytes)));
        // Warm every lazy path (options construction, the reflection cache in PostImpl) so what is
        // measured below is the post itself and not one-time initialisation.
        hub.Post(payload, o => o.WithTarget(TargetAddress));

        var before = GC.GetAllocatedBytesForCurrentThread();
        hub.Post(payload, o => o.WithTarget(TargetAddress));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(OversizedPayloadBytes,
            $"posting must not copy the payload ({allocated:N0} bytes allocated for a "
            + $"{OversizedPayloadBytes:N0}-byte body). On origin/main every post serialised the "
            + "whole delivery to JSON to build an argument for a LogDebug that discards it — and "
            + "for the DeliveryFailure that MessageService.ReportFailure posts about an oversized "
            + "RawJson, that discarded render is what threw OutOfMemoryException on a production "
            + "pod and lost the failure notification (#3044/#3049)");
    }
}
