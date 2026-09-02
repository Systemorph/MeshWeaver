using System.Buffers;
using System.Text;
using System.Text.Json;

namespace MeshWeaver.Messaging;

/// <summary>
/// The two payload rules a delivery must obey on the way BACK — measured here, in the contract
/// assembly, so the types that must obey them can apply them structurally instead of relying on
/// every call site to remember.
///
/// <para><b>Why here and not in <c>MessageSizeGuard</c>.</b> The producer-side bound
/// (<c>MeshWeaver.Messaging.Hub</c>'s <c>MessageSizeGuard</c>) is applied by the two routers, which
/// both reference that assembly. These two rules are needed by <see cref="DeliveryFailure"/> itself
/// and by the Orleans grain implementations, and <see cref="DeliveryFailure"/> lives HERE — so a
/// helper in the Hub assembly is unreachable from the one type whose invariant it is. The
/// measurement is identical and lives in one place; <c>MessageSizeGuard</c> delegates to it and
/// keeps the wording, the incident history and the public surface it already had.</para>
///
/// <para>🚨 <b>Both rules exist because a payload's cost is paid PER HOP, not once.</b> Every hop
/// re-serialises the delivery — Orleans' <c>JsonCodec</c> deep-copies grain-call arguments and
/// results with the mesh's own System.Text.Json options, and
/// <c>Utf8JsonWriter.TranscodeAndWriteRawValue</c> rents up to 3 UTF-8 bytes per UTF-16 char to do
/// it. So a body that merely travels is affordable; a body that travels, comes BACK as the
/// acknowledgement, and then travels a THIRD time inside the failure report is not.</para>
/// </summary>
public static class DeliveryPayloadBounds
{
    /// <summary>
    /// Orleans' memory-stream block size, and therefore the tighter of the two transport ceilings on
    /// one message: <c>1 &lt;&lt; 20</c> = 1,048,576 bytes. Hard-coded in <c>MemoryAdapterFactory</c>
    /// with no configuration surface. <c>MessageSizeGuard.MemoryStreamBlockBytes</c> is this constant
    /// and is pinned against the real <c>FixedSizeBuffer</c> by a test.
    /// </summary>
    public const int MemoryStreamBlockBytes = 1 << 20;

    /// <summary>
    /// How much of a stripped payload is quoted back, so the message stays recognisable — its
    /// <c>$type</c> discriminator and first fields sit at the front of the JSON.
    /// </summary>
    public const int PayloadPreviewChars = 200;

    /// <summary>
    /// True when <paramref name="delivery"/>'s packaged payload cannot fit
    /// <paramref name="limitBytes"/>, with its exact UTF-8 byte count in
    /// <paramref name="payloadBytes"/>.
    ///
    /// <para>Cheap on the hot path, exact on the rare one. A UTF-16 char contributes at most 3
    /// UTF-8 bytes, so <c>3 × Length &lt; limit</c> is a sound O(1), allocation-free proof that the
    /// payload fits and the common case never scans.</para>
    ///
    /// <para>A payload that is not <see cref="RawJson"/> is not measured and never refused: by the
    /// time a delivery reaches a transport <c>MeshBuilder</c> has already packaged it, so RawJson is
    /// the routed shape — and guessing at the size of anything else would mean serialising it a
    /// second time on the hot path to answer a question that is almost always "no".</para>
    /// </summary>
    /// <param name="delivery">The delivery to measure; null and non-RawJson payloads are never oversized.</param>
    /// <param name="limitBytes">The transport bound to measure against.</param>
    /// <param name="payloadBytes">The exact UTF-8 byte count when oversized; 0 otherwise.</param>
    /// <returns><c>true</c> when the payload is at or over <paramref name="limitBytes"/>.</returns>
    public static bool IsOversized(IMessageDelivery? delivery, int limitBytes, out int payloadBytes)
    {
        payloadBytes = 0;
        if (delivery?.Message is not RawJson { Content: { } content })
            return false;
        if ((long)content.Length * 3 < limitBytes)
            return false;
        payloadBytes = Encoding.UTF8.GetByteCount(content);
        return payloadBytes >= limitBytes;
    }

    /// <summary>
    /// 🚨 <b>The same question, asked of a payload that has NOT been packaged yet — issue #3104.</b>
    ///
    /// <para>The overload above measures <see cref="RawJson"/> and nothing else, and its doc explains
    /// why that is right ON THE ROUTER HOT PATH: by the time a delivery reaches a transport
    /// <c>MeshBuilder</c> has packaged it, so <see cref="RawJson"/> is the routed shape, and guessing
    /// at anything else's size would mean serialising twice to answer a question that is almost
    /// always "no".</para>
    ///
    /// <para>🚨 <b>That reasoning does not transfer to the STRIP.</b>
    /// <see cref="WithoutOversizedPayload(IMessageDelivery, JsonSerializerOptions?, int)"/> runs only
    /// while a failure report is being made ready for a transport — a rare path, where an exact
    /// measurement is the correct thing to pay for. Inheriting the hot path's excuse there is what
    /// left the whole PRE-PACKAGING half of the mesh unmeasured: <c>AccessControlPipeline</c> NACKs a
    /// message that is still a CLR object (<c>[RequiresPermission]</c> is an attribute on the message
    /// TYPE — the gate cannot read a <see cref="RawJson"/>), so a denial echoed a multi-megabyte body
    /// back verbatim and the report died at the wall it was describing.</para>
    ///
    /// <para><b>The fast path is untouched.</b> A <see cref="RawJson"/> payload takes exactly the
    /// branch it always did, at exactly the same cost. Only a typed payload — and only when
    /// <paramref name="options"/> are supplied — is serialised, and then into a byte COUNTER rather
    /// than a string: see <see cref="Measure"/>. A null <paramref name="options"/> reproduces the old
    /// behaviour exactly, so a caller with nothing to serialise with is never worse off.</para>
    /// </summary>
    /// <param name="delivery">The delivery to measure; null is never oversized.</param>
    /// <param name="options">The serializer options the payload would be packaged with. Null means
    /// "cannot measure a typed payload", which degrades to the <see cref="RawJson"/>-only rule.</param>
    /// <param name="limitBytes">The transport bound to measure against.</param>
    /// <param name="payloadBytes">The exact UTF-8 byte count when oversized; 0 otherwise.</param>
    /// <returns><c>true</c> when the payload is at or over <paramref name="limitBytes"/>.</returns>
    public static bool IsOversized(
        IMessageDelivery? delivery, JsonSerializerOptions? options, int limitBytes, out int payloadBytes)
    {
        payloadBytes = 0;
        if (delivery is null)
            return false;
        // The shape the O(1) pre-filter already handles — identical cost, identical verdict.
        if (delivery.Message is RawJson)
            return IsOversized(delivery, limitBytes, out payloadBytes);
        if (options is null)
            return false;
        if (!Measure(delivery.Message, options, out payloadBytes))
        {
            payloadBytes = 0;
            return false;
        }

        return payloadBytes >= limitBytes;
    }

    /// <summary>
    /// The exact UTF-8 size <paramref name="message"/> would occupy once packaged, counted rather
    /// than rendered.
    ///
    /// <para>🚨 <b>Counting, not rendering, is the whole point.</b> The failure this measurement
    /// serves is an ALLOCATION failure: #3049's <c>OutOfMemoryException</c> came out of
    /// <c>Utf8JsonWriter.TranscodeAndWriteRawValue</c> → <c>SharedArrayPool.Rent</c> →
    /// <c>GC.AllocateNewArray</c>, on a pod that was already in trouble. A measurement that
    /// materialised the JSON to read its <c>Length</c> would therefore reproduce the exact defect it
    /// exists to prevent, at the exact moment the process can least afford it.
    /// <see cref="ByteCountingBufferWriter"/> keeps a running total and throws every byte away.</para>
    ///
    /// <para>🚨 <b>What that does and does not bound — measured, not assumed.</b>
    /// <c>Utf8JsonWriter</c> asks its <see cref="IBufferWriter{T}"/> for a span large enough for the
    /// token it is about to write, so a document of many small tokens is counted inside the 4 KB
    /// scratch buffer, while ONE giant string value still asks for a span the size of its escaped
    /// self. So this is not O(1) in the payload — it is one buffer, reused, whose peak is the
    /// largest single token. That is still strictly cheaper than the path it replaces, which
    /// materialised the whole document as a UTF-16 <see cref="string"/> (two bytes per char) AND
    /// paid the three-bytes-per-char transcode rent on top; and it is the LAST allocation of that
    /// size in the operation, because a payload found oversized here is then replaced by a marker of
    /// a few hundred bytes and never serialised at all. Do not read this as "measuring cannot
    /// OOM" — read it as "measuring costs less than the packaging it is deciding".</para>
    ///
    /// <para><b>Serialised as <see cref="object"/>, deliberately</b> — that is what
    /// <c>MessageDelivery.Package</c> does, so the polymorphic converter contributes its
    /// <c>$type</c> discriminator here exactly as it would on the wire. Measuring the runtime type
    /// instead would quietly under-count every payload by the size of its discriminator.</para>
    ///
    /// <para>🚨 <b>An unmeasurable payload is reported as NOT oversized, and that is not a swallow.</b>
    /// The serializer's declared failures — an unregistered type, a cycle, a converter that refuses —
    /// mean the size is unknown, and the honest response to "unknown" is the behaviour that was in
    /// place before this measurement existed: echo the payload. The alternative, treating
    /// "could not measure" as "too big", would silently destroy the diagnostic content of every NACK
    /// whose payload happens to be awkward to serialise — a fail-closed default that forges
    /// correct-looking bugs. Note also that a payload that cannot be serialised here cannot be
    /// packaged either, so it never reaches the transport wall this bound is about.</para>
    /// </summary>
    /// <param name="message">The unpackaged payload.</param>
    /// <param name="options">The options it would be packaged with.</param>
    /// <param name="bytes">The exact UTF-8 byte count, when it could be determined.</param>
    /// <returns><c>true</c> when <paramref name="bytes"/> holds a real measurement.</returns>
    private static bool Measure(object message, JsonSerializerOptions options, out int bytes)
    {
        bytes = 0;
        try
        {
            var counter = new ByteCountingBufferWriter();
            using (var writer = new Utf8JsonWriter(counter))
            {
                JsonSerializer.Serialize(writer, message, typeof(object), options);
            }

            bytes = counter.Count >= int.MaxValue ? int.MaxValue : (int)counter.Count;
            return true;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// An <see cref="IBufferWriter{T}"/> that counts what is written to it and keeps none of it. The
    /// same span is handed out for every request and overwritten each time, so the peak footprint is
    /// the largest single write the serializer asks for rather than the size of the whole document.
    ///
    /// <para>Deliberately NOT an <c>ArrayPool</c> rental. The pool is exactly what
    /// <c>SharedArrayPool.Rent</c> → <c>GC.AllocateNewArray</c> threw out of in #3049, and a
    /// measurement that competes for the same pool on the error path would be contending for the
    /// resource whose exhaustion it is there to prevent. A 4 KB array that the GC reclaims is the
    /// cheaper and more predictable trade.</para>
    /// </summary>
    private sealed class ByteCountingBufferWriter : IBufferWriter<byte>
    {
        private byte[] scratch = new byte[4096];

        /// <summary>Total bytes written, as a <see cref="long"/> so a payload larger than
        /// <see cref="int.MaxValue"/> saturates rather than wrapping to a small (and passing) count.</summary>
        public long Count { get; private set; }

        public void Advance(int count) => Count += count;

        public Memory<byte> GetMemory(int sizeHint = 0) => Grow(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => Grow(sizeHint).Span;

        private Memory<byte> Grow(int sizeHint)
        {
            if (sizeHint > scratch.Length)
                scratch = new byte[sizeHint];
            return scratch;
        }
    }

    /// <summary>
    /// 🚨 <b>A NACK about an oversized message must not BE one.</b>
    /// <see cref="DeliveryFailure"/> embeds the ORIGINAL delivery — payload and all — and travels
    /// the SAME transport back to the sender, so a failure report about a 142 MB message is itself a
    /// 142 MB message and dies at exactly the wall it is reporting on, silently. Replacing the
    /// payload with a description of itself keeps the report deliverable; the sender correlates a
    /// <see cref="DeliveryFailure"/> on <c>RequestId</c>, never on the echoed payload.
    ///
    /// <para>🚨 <b>This is applied by <see cref="DeliveryFailure"/>'s own constructor, not by its
    /// callers.</b> That is the whole point of it living here. The rule was introduced (#1890) at
    /// <c>RoutingGrain.PostFailure</c>, extended (#2885) to
    /// <c>OrleansRoutingService.SendDeliveryFailure</c> — and the site that actually took a
    /// production pod down, <c>MessageService.ReportFailure</c>, was neither of them (#3044/#3049).
    /// There are around twenty <c>new DeliveryFailure(delivery)</c> sites in this repository and any
    /// new one is written by someone who has never read this page, so "remember to strip" is not a
    /// control. Making it the record's invariant is.</para>
    ///
    /// <para>Returns <paramref name="delivery"/> unchanged when its payload fits — the echo is
    /// stripped only when carrying it is what would lose the report. The default bound is the
    /// TIGHTER of the two transports (the 1 MiB memory-stream block), so one call protects a NACK
    /// regardless of which transport it will take back to the sender.</para>
    /// </summary>
    /// <param name="delivery">The delivery a failure report is about to echo.</param>
    /// <param name="limitBytes">The bound the report itself must survive.</param>
    /// <returns>The delivery, with an undeliverable payload replaced by a description of it.</returns>
    public static IMessageDelivery WithoutOversizedPayload(
        IMessageDelivery delivery, int limitBytes = MemoryStreamBlockBytes)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!IsOversized(delivery, limitBytes, out var payloadBytes))
            return delivery;
        return delivery.WithMessage(Omission(delivery, payloadBytes, limitBytes));
    }

    /// <summary>
    /// 🚨 <b>The same strip, for a payload that is still a CLR object — issue #3104.</b>
    ///
    /// <para>The overload above is <see cref="DeliveryFailure"/>'s construction invariant and covers
    /// every site structurally, but only for a payload that has already been packaged. A NACK raised
    /// BEFORE packaging — every in-process one, and <c>AccessControlPipeline</c>'s permission denials
    /// above all, since a permission check must see the message's CLR type — was measured by nothing
    /// and stripped by nothing, so the report carried the whole body into
    /// <c>MessageDelivery.Package</c> and out onto the transport that could not take it.</para>
    ///
    /// <para><b>Where this is applied.</b> At the packaging seam itself
    /// (<c>MessageDelivery.Package</c>), which is the one place in the mesh that turns a delivery
    /// into its wire form and the one place the hub's <see cref="JsonSerializerOptions"/> are in
    /// hand. That is deliberate and it is the lesson of #3056: the rule was hand-applied at two of
    /// ~25 construction sites for two years, and the site that took a production pod down was a third
    /// one that had never been told. A rule enforced at ONE structural seam covers the sites nobody
    /// has enumerated yet — including the ones a future PR adds.</para>
    ///
    /// <para><b>And only there.</b> A report that never crosses a boundary keeps its full echo: it
    /// costs nothing to carry in-process and is the better diagnostic. The strip exists to keep a
    /// report DELIVERABLE, not to redact it.</para>
    /// </summary>
    /// <param name="delivery">The delivery a failure report is about to echo across a transport.</param>
    /// <param name="options">The serializer options the payload would be packaged with. Null keeps
    /// the <see cref="RawJson"/>-only behaviour of the overload above.</param>
    /// <param name="limitBytes">The bound the report itself must survive.</param>
    /// <returns>The delivery, with an undeliverable payload replaced by a description of it.</returns>
    public static IMessageDelivery WithoutOversizedPayload(
        IMessageDelivery delivery, JsonSerializerOptions? options, int limitBytes = MemoryStreamBlockBytes)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!IsOversized(delivery, options, limitBytes, out var payloadBytes))
            return delivery;
        return delivery.WithMessage(Omission(delivery, payloadBytes, limitBytes));
    }

    /// <summary>
    /// What replaces an undeliverable payload: how far over the bound it was, and enough of it to
    /// identify the producer. Shared by both strips so the two can never describe the same omission
    /// differently.
    ///
    /// <para>The head is the front of the JSON for an already-packaged payload — where the
    /// <c>$type</c> discriminator and first fields sit — and the CLR type name for one that is still
    /// an object, which is the same fact by another route. A refusal nobody can attribute is not a
    /// refusal.</para>
    /// </summary>
    private static RawJson Omission(IMessageDelivery delivery, int payloadBytes, int limitBytes) =>
        new($"{{\"payloadOmitted\":\"{payloadBytes} bytes exceeded the {limitBytes}-byte "
            + "memory-stream limit; the failure report would not have been deliverable with it "
            + $"attached\",\"bytes\":{payloadBytes},\"head\":{Quote(Head(delivery))}}}");

    /// <summary>
    /// 🚨 <b>AN ACKNOWLEDGEMENT IS NOT AN ECHO — issue #3045.</b>
    ///
    /// <para>Every one of the mesh's three Orleans delivery legs — <c>IRoutingGrain.RouteMessage</c>,
    /// <c>IPodHubGrain.Deliver</c> and <c>IMessageHubGrain.DeliverMessage</c> — is declared
    /// <c>Task&lt;IMessageDelivery&gt;</c> and RETURNS THE DELIVERY IT WAS GIVEN, body included. And
    /// not one caller reads that body: <c>BuildPodHubRoute</c> discards the result outright
    /// (<c>.Select(_ =&gt; Unit.Default)</c>), while <c>BuildGrainRoute</c> and
    /// <c>OrleansRoutingService.DispatchObservable</c> read only <c>State</c>,
    /// <c>SenderWasNacked</c> and <c>GetFailureMessage()</c> — the state and the properties, never
    /// <c>Message</c>.</para>
    ///
    /// <para><b>So the payload made the trip twice.</b> Orleans copies a call's ARGUMENTS and its
    /// RESULT with the same <c>JsonCodec</c>, so an <i>n</i>-byte body cost <i>n</i> bytes outbound
    /// and <i>n</i> bytes inbound on every hop, to deliver a verdict that fits in a hundred. On
    /// 2026-09-02 the return half is what failed: <c>PooledResponseCopier</c> →
    /// <c>JsonCodec.DeepCopy</c> → <c>OutOfMemoryException</c> inside
    /// <c>InsideRuntimeClient.SafeSendResponse</c>, i.e. the callee could not even send its answer.
    /// The forward leg was guarded (#2897, #2885); the way back never was, because nobody had asked
    /// what the way back was carrying.</para>
    ///
    /// <para>🚨 <b>Unconditional, not bound-conditional.</b> The other rule on this page strips only
    /// what a transport provably cannot carry, because a NACK's echoed payload is at least
    /// <i>arguably</i> diagnostic. This one has no such excuse: the acknowledgement's body is read
    /// by nobody at any size, so making the strip conditional on a bound would keep paying the exact
    /// cost this exists to remove for every payload just under it — which is precisely the band the
    /// production incident sat in.</para>
    ///
    /// <para>State, id, sender, target, access context and all properties survive; only the body is
    /// replaced, by a marker that says so rather than by null, so a log or a debugger shows what
    /// happened instead of an unexplained empty message.</para>
    /// </summary>
    /// <param name="delivery">The delivery a grain is about to return as its acknowledgement.</param>
    /// <returns>The same delivery, carrying the verdict and not the body.</returns>
    public static IMessageDelivery WithoutEchoedPayload(IMessageDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return delivery.WithMessage(AcknowledgementMarker);
    }

    /// <summary>
    /// The body an acknowledgement carries instead of the message. A single shared instance: it is
    /// an immutable record with constant content, so this is a read-only constant lookup and not
    /// mutable static state.
    /// </summary>
    private static readonly RawJson AcknowledgementMarker = new(
        "{\"acknowledgement\":\"The delivery was accepted; its body is deliberately not echoed "
        + "back. Callers read State and Properties only — see "
        + "DeliveryPayloadBounds.WithoutEchoedPayload (#3045).\"}");

    /// <summary>
    /// The head of the payload, for identification. 🚨 The caller JSON-quotes it before it reaches a
    /// log line or a report: this is attacker-influenced content (it is a message payload), and a
    /// raw newline or quote in it would break a red burst apart in the log pipeline — a burst is
    /// re-assembled from its indented continuation lines, so an embedded newline can forge a new
    /// log record.
    /// </summary>
    /// <param name="delivery">The delivery whose payload head is wanted.</param>
    /// <returns>The first <see cref="PayloadPreviewChars"/> characters, with an ellipsis when truncated.</returns>
    public static string Preview(IMessageDelivery delivery) =>
        delivery.Message is RawJson { Content: { } content }
            ? Head(content) is var head && head.Length < content.Length ? head + "…" : head
            : delivery.Message?.GetType().Name ?? "<null>";

    /// <summary>
    /// The identifying head of a payload: the front of the JSON when it is already packaged (where
    /// the <c>$type</c> discriminator lives), and the CLR type name when it is still an object.
    /// Before #3104 the second case returned <see cref="string.Empty"/> — correct while only
    /// packaged payloads could ever be stripped, and an unattributable omission the moment a typed
    /// one could be.
    /// </summary>
    private static string Head(IMessageDelivery delivery) =>
        delivery.Message is RawJson { Content: { } content }
            ? Head(content)
            : delivery.Message?.GetType().Name ?? "<null>";

    private static string Head(string content) =>
        content.Length <= PayloadPreviewChars ? content : content[..PayloadPreviewChars];

    /// <summary>
    /// JSON-quotes a value so it is safe to embed in a single log line or a JSON report body.
    /// </summary>
    /// <param name="value">The raw, possibly attacker-influenced text.</param>
    /// <returns>The value as a JSON string literal, quotes included.</returns>
    public static string Quote(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
