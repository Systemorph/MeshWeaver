using System.Text;

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
        return delivery.WithMessage(new RawJson(
            $"{{\"payloadOmitted\":\"{payloadBytes} bytes exceeded the {limitBytes}-byte "
            + "memory-stream limit; the failure report would not have been deliverable with it "
            + $"attached\",\"bytes\":{payloadBytes},\"head\":{Quote(Head(delivery))}}}"));
    }

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

    private static string Head(IMessageDelivery delivery) =>
        delivery.Message is RawJson { Content: { } content } ? Head(content) : string.Empty;

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
