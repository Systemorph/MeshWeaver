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
            .WithPostingIdentity(PostingIdentity.System)
            // Registered so the polymorphic converter writes a real $type discriminator for it —
            // the same shape a production payload has, and the fact the stripped report quotes back.
            .WithTypes(typeof(ImportPayload))));
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

    // ────────────────────────────────────────────────────────────────────────────────────────────
    // 🚨 #3104 — THE SAME INVARIANT, FOR A PAYLOAD THAT IS STILL A CLR OBJECT.
    //
    // Everything above measures RawJson. DeliveryPayloadBounds.IsOversized opens
    // `if (delivery?.Message is not RawJson …) return false;`, so until #3104 a typed payload was
    // never measured and never stripped — and the constructor invariant introduced above inherited
    // that blind spot wholesale. The gap is not a corner case: AccessControlPipeline NACKs a message
    // that is still an object, because [RequiresPermission] is an attribute on the message TYPE and
    // a permission check cannot run against a RawJson. Every in-process NACK is in that position.
    // ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The payload shape a pre-packaging NACK actually sees: a CLR object, not
    /// <see cref="RawJson"/>. Public so the polymorphic converter can name it in the <c>$type</c>
    /// discriminator, which is what a stripped report quotes back.
    /// </summary>
    /// <param name="Nodes">The bulk field — what makes an import payload big.</param>
    public record ImportPayload(string Nodes);

    /// <summary>
    /// A payload that CANNOT be serialised: the self-reference makes <see cref="JsonSerializer"/>
    /// throw <see cref="JsonException"/> ("a possible object cycle was detected"). The subject of
    /// <see cref="An_unmeasurable_payload_is_not_treated_as_oversized"/>.
    /// </summary>
    public class CyclicPayload
    {
        /// <summary>Points at itself, which is the whole point.</summary>
        public CyclicPayload? Self { get; set; }
    }

    /// <summary>
    /// A delivery carrying an UNPACKAGED payload of roughly <paramref name="payloadBytes"/> — the
    /// shape every NACK raised before the packaging seam is handed.
    /// </summary>
    private IMessageDelivery TypedDeliveryOf(int payloadBytes, string id = "d-typed") =>
        new MessageDelivery<ImportPayload>(
            SenderAddress, TargetAddress,
            new ImportPayload(new string('x', payloadBytes)),
            Hub.JsonSerializerOptions) with
        { Id = id };

    /// <summary>The wire form of <paramref name="delivery"/> — what the transport would carry.</summary>
    private string Packaged(IMessageDelivery delivery) =>
        PayloadOf(delivery.Package(Hub.JsonSerializerOptions));

    /// <summary>
    /// A report about a failed delivery, as an outbound delivery ready to be packaged — exactly what
    /// <c>hub.Post(new DeliveryFailure(delivery), o =&gt; o.ResponseFor(delivery))</c> produces on
    /// its way back to the sender.
    /// </summary>
    private IMessageDelivery ReportAbout(IMessageDelivery failed, string reason) =>
        new MessageDelivery<DeliveryFailure>(
            TargetAddress, SenderAddress, new DeliveryFailure(failed, reason), Hub.JsonSerializerOptions);

    /// <summary>
    /// 🚨 THE HEADLINE. A permission denial about a multi-megabyte typed message must not itself be a
    /// multi-megabyte message.
    ///
    /// <para>Against <c>origin/main</c> this fails outright: the report reaches the packaging seam
    /// carrying the whole body, <c>Package</c> serialises it, and the frame going back to the sender
    /// is larger than the frame that could not be delivered in the first place. The constructor
    /// invariant does not help — it measures <see cref="RawJson"/>, and this payload is an
    /// <see cref="ImportPayload"/>.</para>
    /// </summary>
    [Fact]
    public void A_typed_failure_report_does_not_carry_the_payload_across_the_wire()
    {
        var denied = TypedDeliveryOf(OversizedPayloadBytes);

        var wire = Packaged(ReportAbout(denied, "access denied"));

        wire.Length.Should().BeLessThan(OversizedPayloadBytes / 100,
            "a NACK about an oversized message must not BE one, and 'oversized' is a property of the "
            + "payload rather than of the CLR shape it currently happens to have. A permission "
            + "denial NACKs a message that is still an object — [RequiresPermission] is an attribute "
            + "on the message TYPE, so AccessControlPipeline cannot even run against RawJson — so "
            + "before #3104 the entire pre-packaging half of the mesh echoed the body back verbatim "
            + "and the report died at the wall it was describing (#3049)");
    }

    /// <summary>
    /// A stripped TYPED report is still attributable. There is no <c>$type</c> discriminator to quote
    /// from — the payload was never JSON — so the CLR type name is what identifies the producer, and
    /// the byte count says how far over the bound it was.
    /// </summary>
    [Fact]
    public void A_stripped_typed_report_names_the_type_it_dropped()
    {
        var wire = Packaged(ReportAbout(TypedDeliveryOf(OversizedPayloadBytes), "access denied"));

        wire.Should().Contain(nameof(ImportPayload),
            "the CLR type name is the only handle on WHICH producer sent the undeliverable message "
            + "once the body is gone; before #3104 the omission marker rendered an empty head for a "
            + "typed payload, and a refusal nobody can act on is not a refusal");
        wire.Should().Contain("payloadOmitted",
            "the report must SAY that it dropped the body — a silently small report reads as a small "
            + "message and sends the next reader looking in the wrong place");
        wire.Should().Contain("access denied",
            "the strip must not cost the FAILURE's own message; only the echoed payload goes");
    }

    /// <summary>
    /// 🚨 THE CONTROL, and it carries as much weight as the headline. An ordinary typed NACK is
    /// untouched: its echoed payload arrives whole. A strip that fired unconditionally would pass the
    /// test above identically while destroying the diagnostic content of every ordinary failure in
    /// the mesh — this exists to keep a report DELIVERABLE, never to redact it.
    /// </summary>
    [Fact]
    public void An_ordinary_typed_failure_report_still_echoes_its_delivery()
    {
        var small = TypedDeliveryOf(SmallPayloadBytes, "d-small-typed");

        var wire = Packaged(ReportAbout(small, "handler threw"));

        wire.Should().Contain(new string('x', SmallPayloadBytes),
            "a payload that fits is echoed whole — the strip must be invisible to every ordinary "
            + "NACK, or it is a behaviour change dressed as a fix");
        wire.Should().NotContain("payloadOmitted",
            "and it must not even claim to have dropped something");
    }

    /// <summary>
    /// 🚨 THE FAST PATH IS NOT SLOWED, and — more importantly — not CHANGED. The options-aware
    /// overload must give a <see cref="RawJson"/> payload the same verdict AND the same byte count as
    /// the overload that has always handled it, via the same O(1) pre-filter. A widening that quietly
    /// re-decided the packaged case would be a change to the routers' refusal behaviour, which is not
    /// what #3104 is about.
    /// </summary>
    [Fact]
    public void The_raw_json_verdict_is_identical_with_and_without_options()
    {
        foreach (var delivery in new[]
                 { DeliveryOf(OversizedPayloadBytes), DeliveryOf(SmallPayloadBytes, "d-fits") })
        {
            var withoutOptions = DeliveryPayloadBounds.IsOversized(
                delivery, DeliveryPayloadBounds.MemoryStreamBlockBytes, out var bytesWithout);
            var withOptions = DeliveryPayloadBounds.IsOversized(
                delivery, Hub.JsonSerializerOptions, DeliveryPayloadBounds.MemoryStreamBlockBytes,
                out var bytesWith);

            withOptions.Should().Be(withoutOptions,
                "the RawJson branch must be the branch it always was — same verdict, same cost");
            bytesWith.Should().Be(bytesWithout, "and the same exact byte count");
        }
    }

    /// <summary>
    /// The degradation is EXACT. With no options in hand there is nothing to serialise with, so a
    /// typed payload is treated exactly as it was before #3104 — not measured, not stripped. A caller
    /// that cannot supply options is never made worse off, and the old overload's contract is
    /// unchanged.
    /// </summary>
    [Fact]
    public void Without_options_a_typed_payload_is_left_exactly_as_before()
    {
        var typed = TypedDeliveryOf(OversizedPayloadBytes);

        DeliveryPayloadBounds
            .IsOversized(typed, null, DeliveryPayloadBounds.MemoryStreamBlockBytes, out _)
            .Should().BeFalse("with no options there is no measurement, and inventing one would be a guess");
        DeliveryPayloadBounds.WithoutOversizedPayload(typed, null).Should().BeSameAs(typed,
            "and an unmeasured payload is echoed, exactly as it was before this overload existed");
    }

    /// <summary>
    /// 🚨 THE <c>out</c> COUNT IS PUBLISHED ONLY WITH A "YES" — both overloads, every branch.
    ///
    /// <para>Both document <c>payloadBytes</c> as "the exact UTF-8 byte count when oversized; 0
    /// otherwise", and both had a branch that broke the promise: a payload big enough to defeat the
    /// O(1) pre-filter (or, for a typed one, big enough to be worth measuring) but still UNDER the
    /// bound returned <c>false</c> with a real count sitting in the out parameter.</para>
    ///
    /// <para>Nothing in production reads the value on a false return — every caller reads it inside
    /// the branch — so this costs nothing to make true. It is worth making true because a
    /// non-zero count next to a "no" is the one shape a reader can mistake for evidence, and the
    /// next caller to reach for it would have no way to tell a measured-and-fits from an
    /// oversized-and-measured. Caught by review on this PR, not by a test — hence a test.</para>
    /// </summary>
    [Fact]
    public void A_payload_that_fits_reports_no_byte_count()
    {
        // RawJson, big enough that the 3 × Length pre-filter cannot prove it fits, so the exact
        // count IS computed — the branch that used to leak it.
        var justUnder = DeliveryOf(DeliveryPayloadBounds.MemoryStreamBlockBytes - 1, "d-just-under");
        DeliveryPayloadBounds
            .IsOversized(justUnder, DeliveryPayloadBounds.MemoryStreamBlockBytes, out var rawBytes)
            .Should().BeFalse("one byte under the block still fits");
        rawBytes.Should().Be(0,
            "the count is the answer to 'how far over', and there is no over — a measured-and-fits "
            + "must be indistinguishable from a proved-and-fits at the out parameter");

        // The same, through the typed path, which measures by serialising.
        var typed = TypedDeliveryOf(SmallPayloadBytes, "d-fits-typed");
        DeliveryPayloadBounds
            .IsOversized(typed, Hub.JsonSerializerOptions,
                DeliveryPayloadBounds.MemoryStreamBlockBytes, out var typedBytes)
            .Should().BeFalse("a small typed payload fits");
        typedBytes.Should().Be(0, "and it reports no count either");
    }

    /// <summary>
    /// 🚨 UNMEASURABLE IS NOT "TOO BIG". A payload the serializer refuses has an UNKNOWN size, and the
    /// honest answer to unknown is the behaviour that predates the measurement: echo it.
    ///
    /// <para>Treating a failed measurement as "oversized" would be a fail-closed default that
    /// silently destroys the diagnostic content of every NACK whose payload happens to be awkward to
    /// serialise — a correct-looking bug of exactly the shape this repository has been bitten by
    /// before. Failing open is also safe here: a payload that cannot be serialised cannot be packaged
    /// either, so it never reaches the transport wall this bound is about.</para>
    /// </summary>
    [Fact]
    public void An_unmeasurable_payload_is_not_treated_as_oversized()
    {
        var cyclic = new CyclicPayload();
        cyclic.Self = cyclic;
        var delivery = new MessageDelivery<CyclicPayload>(
            SenderAddress, TargetAddress, cyclic, Hub.JsonSerializerOptions);

        DeliveryPayloadBounds.WithoutOversizedPayload(delivery, Hub.JsonSerializerOptions)
            .Should().BeSameAs(delivery,
                "a measurement that could not be taken is not evidence of anything — least of all "
                + "evidence to throw the payload away on");
    }

    /// <summary>
    /// 🚨 THE MEASUREMENT COUNTS RATHER THAN RENDERS — asserted as an A/B against the obvious
    /// implementation, because allocation is what actually failed in production and because a bare
    /// budget would be a number nobody could defend.
    ///
    /// <para>The reason a typed payload was never measured is that measuring it means serialising it,
    /// and serialising on the error path is what threw <c>OutOfMemoryException</c> in #3049
    /// (<c>Utf8JsonWriter.TranscodeAndWriteRawValue</c> → <c>SharedArrayPool.Rent</c> →
    /// <c>GC.AllocateNewArray</c>). So the two candidates are compared directly, in one run, on one
    /// thread: the counting writer, against <c>JsonSerializer.Serialize(payload, options).Length</c>
    /// — which is what anyone would reach for first, and which materialises the whole document as a
    /// UTF-16 string on top of the writer's transcode buffers.</para>
    ///
    /// <para>🚨 <b>What this deliberately does NOT claim.</b> The counting writer is not O(1) in the
    /// payload: <c>Utf8JsonWriter</c> asks for a span big enough for the token it is about to write,
    /// so ONE giant string value still requests one large buffer (measured here at ~2× the payload,
    /// against ~5× for the render). "Cheaper than the thing it replaces, and the last allocation of
    /// that size in the operation" is the honest claim, and it is the one pinned — a payload found
    /// oversized here is replaced by a marker and never serialised again.</para>
    ///
    /// <para><c>GC.GetAllocatedBytesForCurrentThread</c> is an exact per-thread counter, not a sample,
    /// and both candidates are synchronous on the calling thread — so this is a deterministic fact
    /// and not a timing one.</para>
    /// </summary>
    [Fact]
    public void Measuring_a_typed_payload_costs_less_than_rendering_it()
    {
        var options = Hub.JsonSerializerOptions;
        var typed = TypedDeliveryOf(OversizedPayloadBytes);
        var payload = typed.Message;
        // Warm every lazy path on BOTH candidates (options, the type's JsonTypeInfo, the converter
        // cache) so what is compared below is the write and not one-time initialisation.
        DeliveryPayloadBounds.IsOversized(
            typed, options, DeliveryPayloadBounds.MemoryStreamBlockBytes, out _);
        _ = JsonSerializer.Serialize(payload, typeof(object), options).Length;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var oversized = DeliveryPayloadBounds.IsOversized(
            typed, options, DeliveryPayloadBounds.MemoryStreamBlockBytes, out var counted);
        var countingCost = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        var renderedBytes = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(payload, typeof(object), options));
        var renderingCost = GC.GetAllocatedBytesForCurrentThread() - before;

        oversized.Should().BeTrue("the fixture is comfortably over the 1 MiB block");
        counted.Should().Be(renderedBytes,
            "the count must be EXACT and must be the PACKAGED size — the same number of bytes the "
            + "real serialization produces, envelope and $type discriminator included. A measurement "
            + "that merely approximated would decide a refusal on a number nobody could reproduce");
        countingCost.Should().BeLessThan(renderingCost,
            $"counting allocated {countingCost:N0} bytes where rendering allocated "
            + $"{renderingCost:N0} for the same {OversizedPayloadBytes:N0}-byte payload. Rendering "
            + "to read a Length materialises the document as a UTF-16 string on top of the "
            + "shared-pool transcode rent — the allocation that threw OutOfMemoryException on a "
            + "production pod (#3049) — which would have this check reproduce the very failure it "
            + "exists to prevent, on the error path, where the process can least afford it");
    }
}
