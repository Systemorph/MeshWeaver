using System.Text;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// 🚨 The producer-side bound on a delivery that provably cannot be carried — issues #1890
/// (Orleans MEMORY STREAMS), #2897 (Orleans GRAIN CALLS) and #2885 (the ROUTER's OWN grain call).
///
/// <para><b>Why this type lives in <c>MeshWeaver.Messaging.Hub</c> and not beside the router.</b>
/// It began in <c>MeshWeaver.Hosting.Orleans</c>, next to the three legs #1890 and #2897 guard. But
/// #2885 died on a FOURTH leg — <c>OrleansRoutingService.DispatchObservable</c>'s
/// <c>IRoutingGrain.RouteMessage</c> call — which lives in <c>MeshWeaver.Connection.Orleans</c>, and
/// <c>MeshWeaver.Hosting.Orleans</c> REFERENCES that assembly, so the guard was unreachable from the
/// one site that needed it next. Duplicating the check there is precisely the shape this codebase
/// has already paid for twice ("a fix landed on one site and missed the other" — #2346), so the
/// guard moved DOWN to the assembly both routers already reference, beside
/// <see cref="RawJsonConverter"/>, whose <c>WriteRawValue</c> is the allocation that fails.</para>
///
/// <para>🚨 <b>And it is PUBLIC, not internal + <c>InternalsVisibleTo</c>, for the reason
/// <see cref="MessageStormBreaker"/> already documents.</b> An assembly-wide
/// <c>InternalsVisibleTo</c> on THIS assembly makes the internal
/// <c>IMessageHub.Observe(object, Func&lt;PostOptions, PostOptions&gt;)</c> visible as an INSTANCE
/// method, which then beats the public generic <c>Observe&lt;TResponse&gt;</c> extension in overload
/// resolution for every call site in the granted assembly — so <c>response.Message</c> silently
/// becomes <see cref="object"/> and the whole test project stops compiling. Measured here on
/// 2026-09-02: granting <c>MeshWeaver.Hosting.Orleans.Test</c> reddened 20+ untouched files with
/// <c>CS1061 'object' does not contain a definition for 'Success'</c>. A framework utility that two
/// assemblies must share is made public; it is not app API, and nobody constructs it.</para>
///
/// <para><b>What happened, twice.</b> #1890: a ~37 MB delivery (37,483,597 bytes) was posted to
/// <c>memory-7-0xE0000007</c>. The publish SUCCEEDED. Minutes later, on the consuming side,
/// <c>PersistentStreamPullingAgent.ReadFromQueue</c> threw
/// <c>ArgumentOutOfRangeException: Message size is too big</c> out of
/// <c>MemoryPooledCache.AddToCache</c> and retried — a retry that cannot succeed, because the size
/// is a property of the message, not of the attempt. The message was never delivered and the
/// PRODUCER was never told: it had already been handed a successful publish, so its
/// <c>Observe(...)</c> simply waited out its timeout. The only trace was a queue id, which names
/// nothing an operator can act on — not the target hub, not the message type, not the delivery.</para>
///
/// <para>#2897 is the SAME defect on the other transport, and it is worse. A 149,199,409-byte
/// (~142 MB) delivery was dispatched to <c>messagehub/AgenticEngineering</c> through
/// <c>IMessageHubGrain.DeliverMessage</c>. Orleans refuses the FRAME, not the message:
/// <c>MessageSerializer.ThrowInvalidBodyLength</c> throws
/// <c>InvalidMessageFrameException: Invalid body size: 149199409 (max configured value is
/// 104857600, see MaxMessageBodySize)</c> from inside <c>Connection.ProcessOutgoing</c> — the
/// silo-to-silo connection's WRITE LOOP. A serializer fault there is not recoverable per-message,
/// so Orleans tears the whole connection down. Every occurrence in the incident carries the
/// identical body size, ~5 s apart, each from a NEW local port: one undeliverable message, in a
/// reconnect-and-retry loop, destroying a shared silo-to-silo connection over and over.</para>
///
/// <para><b>Why this is not just one lost message.</b> On the stream transport an oversized payload
/// loses ITSELF. On the grain transport it takes the CONNECTION with it, so every unrelated
/// delivery queued on that connection at that moment is collateral — which is why the blast radius
/// of a single oversized payload is a partition that stops answering rather than one request that
/// times out.</para>
///
/// <para><b>Why the bound is not ours to raise.</b> For memory streams, Orleans caches messages in a
/// pool of fixed <b>1 MiB</b> blocks (<c>MemoryAdapterFactory.CreateBufferPoolIfNotCreatedYet</c>:
/// <c>var oneMb = 1 &lt;&lt; 20; new ObjectPool&lt;FixedSizeBuffer&gt;(() =&gt; new
/// FixedSizeBuffer(oneMb))</c> — hard-coded, with no configuration surface), and a cached message
/// must fit ENTIRELY in one clean block. For grain calls the bound is
/// <c>SiloMessagingOptions.MaxMessageBodySize</c>, which IS configurable — and raising it is
/// explicitly the wrong move (#2897: it "would mask the symptom and make 142 MB frames normal
/// traffic"). What IS ours is the third option — <b>do not hand the transport a message it provably
/// cannot carry</b>, and say so loudly, at the producer, where every fact needed to find the
/// producer is still in hand.</para>
///
/// <para><b>This can only turn a silent loss into a loud one.</b> Both bounds are the transport's
/// own: a payload at or above them cannot be delivered TODAY, so nothing that works is newly
/// refused. It deliberately does not try to be an exact admission test — the on-wire form carries an
/// envelope this cannot see, so a payload just under the bound can still be rejected inside Orleans.
/// That residual band is unchanged by this guard; the gross case (37× / 1.4× over) is what it
/// exists to attribute.</para>
///
/// <para>Pure and static — no hub, no Orleans types — so the decision and its wording are asserted
/// directly, without a cluster.</para>
/// </summary>
public static class MessageSizeGuard
{
    /// <summary>
    /// Orleans' memory-stream block size, and therefore the hard ceiling on one memory-stream
    /// message: <c>1 &lt;&lt; 20</c> = 1,048,576 bytes. Hard-coded in
    /// <c>MemoryAdapterFactory</c> (Microsoft.Orleans.Streaming 10.2.x) with no configuration
    /// surface — <c>OversizedStreamMessageRefusedTest
    /// .The_orleans_block_size_is_what_the_guard_is_calibrated_against</c> pins this constant
    /// against the real <c>FixedSizeBuffer</c>, so an Orleans upgrade that moved the block size
    /// fails a test instead of silently mis-tuning the guard.
    /// </summary>
    public const int MemoryStreamBlockBytes = 1 << 20;

    /// <summary>
    /// The FALLBACK ceiling on one Orleans grain-call body: Orleans'
    /// <c>SiloMessagingOptions.MaxMessageBodySize</c> default, 104,857,600 bytes (100 MiB) — the
    /// exact value the #2897 incident reported as "max configured value". Used only when the
    /// configured option cannot be resolved; the router prefers the LIVE value so a deployment that
    /// tuned the limit is measured against what its transport actually enforces rather than against
    /// a constant compiled in here. <c>OversizedGrainDeliveryRefusedTest
    /// .The_orleans_body_size_default_is_what_the_fallback_is_calibrated_against</c> pins it against
    /// the real <c>SiloMessagingOptions</c>, so an Orleans upgrade that moved the default fails a
    /// test instead of silently mis-tuning the fallback.
    /// </summary>
    public const int DefaultGrainTransportBodyBytes = 104_857_600;

    /// <summary>
    /// How much of an oversized payload the refusal quotes back. Enough to recognise the message —
    /// its <c>$type</c> discriminator and first fields sit at the front of the JSON — without
    /// pasting a multi-megabyte blob into a log line that is itself size-capped.
    /// </summary>
    public const int PayloadPreviewChars = 200;

    /// <summary>
    /// True when <paramref name="delivery"/>'s packaged payload cannot fit
    /// <paramref name="limitBytes"/>, with its exact UTF-8 byte count in
    /// <paramref name="payloadBytes"/>.
    ///
    /// <para>Cheap on the hot path, exact on the rare one. A UTF-16 char contributes at most 3
    /// UTF-8 bytes (a surrogate PAIR is 4 bytes for 2 chars, i.e. 2 per char), so
    /// <c>3 × Length &lt; limit</c> is a sound O(1) proof that the payload fits and the common
    /// case never scans. Only a delivery that could plausibly be over the line pays for the exact
    /// count.</para>
    ///
    /// <para>A payload that is not <see cref="RawJson"/> is not measured and never refused: by the
    /// time a delivery reaches the router <c>MeshBuilder</c> has already packaged it
    /// (<c>delivery.Package(...)</c>), so RawJson is the routed shape — and guessing at the size of
    /// anything else would mean serialising it a second time on the hot path to answer a question
    /// that is almost always "no".</para>
    /// </summary>
    public static bool IsOversized(
        IMessageDelivery? delivery, int limitBytes, out int payloadBytes)
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
    /// The refusal an operator and the sender both read: WHAT was rejected (target, delivery id,
    /// and the head of the payload, which carries its <c>$type</c>), HOW BIG it was, and against
    /// WHICH limit — the three facts the Orleans-side <c>ArgumentOutOfRangeException</c> could not
    /// supply, since it knows only a queue id.
    /// </summary>
    public static string Describe(
        IMessageDelivery delivery, string addressPath, int payloadBytes, int limitBytes)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return $"Refused to publish delivery '{delivery.Id}' to memory-stream '{addressPath}': its "
            + $"payload is {payloadBytes:N0} bytes, at or over the {limitBytes:N0}-byte Orleans "
            + "memory-stream limit (one fixed 1 MiB pooled-cache block per message), so the "
            + "stream's pulling agent would reject it with 'Message size is too big' and retry "
            + "forever while the message was silently never delivered. Sender "
            + $"'{delivery.Sender}'. Payload head: {Quote(Preview(delivery))}";
    }

    /// <summary>
    /// The grain-transport refusal — issue #2897. Same three facts as <see cref="Describe"/>, and
    /// one more that only this transport has: the frame is refused inside the CONNECTION's write
    /// loop, so dispatching it does not merely lose this delivery, it tears down the silo-to-silo
    /// connection and takes every unrelated message queued on it along. That is the sentence an
    /// operator needs in order to stop reading the incident as "one slow request".
    /// </summary>
    public static string DescribeGrainDispatch(
        IMessageDelivery delivery, string addressPath, int payloadBytes, int limitBytes)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return $"Refused to dispatch delivery '{delivery.Id}' to grain '{addressPath}': its payload "
            + $"is {payloadBytes:N0} bytes, at or over the {limitBytes:N0}-byte Orleans "
            + "MaxMessageBodySize, so Orleans would refuse the frame in "
            + "MessageSerializer.ThrowInvalidBodyLength and tear down the silo-to-silo connection "
            + "from Connection.ProcessOutgoing — losing this delivery AND every unrelated message "
            + "queued on that connection, then reconnecting and repeating. Sender "
            + $"'{delivery.Sender}'. Payload head: {Quote(Preview(delivery))}";
    }

    /// <summary>
    /// The ROUTER-leg refusal — issue #2885. The same bound as
    /// <see cref="DescribeGrainDispatch"/>, but a different failure to describe, because this leg
    /// fails one step EARLIER and therefore louder.
    ///
    /// <para><b>What happened.</b> <c>OrleansRoutingService</c> hands the delivery to
    /// <c>IRoutingGrain.RouteMessage</c>, and Orleans serialises that ARGUMENT with the mesh's own
    /// System.Text.Json options (<c>AddJsonSerializer(_ =&gt; true, …)</c> claims every type). So the
    /// packaged <see cref="RawJson"/> goes through <see cref="RawJsonConverter"/>'s
    /// <c>writer.WriteRawValue(value.Content)</c> — a <see cref="string"/>, i.e. UTF-16 — and
    /// <c>Utf8JsonWriter.TranscodeAndWriteRawValue</c> rents up to <b>3 bytes per char</b> from
    /// <c>SharedArrayPool</c> to transcode it. On 2026-08-31 that rent threw
    /// <c>OutOfMemoryException</c> at <c>GC.AllocateNewArray</c> while routing to
    /// <c>import/xDAfkqsVUE-OMBHb0mVtSg</c>, the bulk-import hub. The delivery was lost with no
    /// size, no target and no producer recoverable from the stack — and an OOM in the routing path
    /// endangers every other allocation in the pod, not just this one.</para>
    ///
    /// <para><b>Why the #2897 guard could not see it.</b> That guard runs INSIDE
    /// <c>RoutingGrain</c>, on the two FORWARD legs. This leg is how a delivery REACHES the routing
    /// grain, so it is strictly upstream of all three guarded sites: the payload OOMs on the way in
    /// and the guarded code never executes. The bound itself was never the problem — its PLACEMENT
    /// was.</para>
    ///
    /// <para>🚨 <b>What this does NOT claim.</b> The transcode peaks at ~3× the payload, while the
    /// bound is the frame limit, so a payload comfortably under the limit can still exhaust a
    /// memory-pressured pod. This refuses what the transport provably cannot carry and names the
    /// producer; it does not make the router allocation-safe at any size. The remaining work is on
    /// the PRODUCER — an import that batches instead of building one delivery whole.</para>
    /// </summary>
    public static string DescribeRouterDispatch(
        IMessageDelivery delivery, string addressPath, int payloadBytes, int limitBytes)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return $"Refused to route delivery '{delivery.Id}' to '{addressPath}': its payload is "
            + $"{payloadBytes:N0} bytes, at or over the {limitBytes:N0}-byte Orleans "
            + "MaxMessageBodySize, so handing it to IRoutingGrain.RouteMessage would serialise it "
            + "through RawJsonConverter.WriteRawValue, whose UTF-16→UTF-8 transcode rents up to 3 "
            + "bytes per char from the shared array pool — the allocation that threw "
            + "OutOfMemoryException in production (#2885) — and Orleans would refuse the resulting "
            + "frame in any case. Sender "
            + $"'{delivery.Sender}'. Payload head: {Quote(Preview(delivery))}";
    }

    /// <summary>
    /// 🚨 A NACK about an oversized message must not BE one.
    /// <see cref="DeliveryFailure"/> embeds the ORIGINAL delivery — payload and all — and travels
    /// the SAME transport back to the sender, so a failure report about a 37 MB message is
    /// itself a 37 MB message and dies at exactly the wall it is reporting on, silently. Replacing
    /// the payload with a description of it keeps the report deliverable; the sender correlates a
    /// <see cref="DeliveryFailure"/> on <c>RequestId</c>, never on the echoed payload.
    ///
    /// <para>Returns <paramref name="delivery"/> unchanged when its payload fits — the echo is
    /// stripped only when carrying it is what would lose the report. The default bound is the
    /// TIGHTER of the two transports (the 1 MiB memory-stream block), so one call protects a NACK
    /// regardless of which transport it will take back to the sender.</para>
    /// </summary>
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
    /// The head of the payload, for identification. 🚨 The CALLER JSON-quotes it before it reaches
    /// a log line: this is attacker-influenced content (it is a message payload), and a raw
    /// newline or quote in it would break the burst apart in the log pipeline — a red burst is
    /// re-assembled from its indented continuation lines, so an embedded newline can forge a new
    /// log record. Quoting also keeps the refusal a single parseable line.
    /// </summary>
    private static string Preview(IMessageDelivery delivery) =>
        delivery.Message is RawJson { Content: { } content }
            ? Head(content) is var head && head.Length < content.Length ? head + "…" : head
            : delivery.Message?.GetType().Name ?? "<null>";

    private static string Head(IMessageDelivery delivery) =>
        delivery.Message is RawJson { Content: { } content } ? Head(content) : string.Empty;

    private static string Head(string content) =>
        content.Length <= PayloadPreviewChars ? content : content[..PayloadPreviewChars];

    private static string Quote(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
