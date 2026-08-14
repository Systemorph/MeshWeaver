namespace MeshWeaver.Messaging;

/// <summary>
/// The routing layer's ANSWER-ONCE contract: may a path that has given up on a delivery answer its
/// sender with a <see cref="DeliveryFailure"/> at all? Stated ONCE here so every such path applies
/// it identically, and — the reason this type exists — so the answer survives the TYPE ERASURE that
/// <c>MessageDelivery.Package</c> performs on the way into the router.
///
/// <para><b>Two payloads must never be answered.</b> A <see cref="DeliveryFailure"/>, because the
/// answer is itself a <see cref="DeliveryFailure"/> and answering one loops. A
/// <see cref="CanBeIgnoredAttribute"/> message (<c>HeartBeatEvent</c>, <c>ShutdownRequest</c>,
/// <c>DisposeRequest</c>), because it is fire-and-forget — there is NO awaiting <c>Observe</c>
/// callback to fail, so the NACK is pure added traffic, and for a permanently-gone owner that is
/// heart-beaten every interval it is re-posted forever, which IS the NotFound storm. Both matter
/// most on exactly the paths that give up — shutdown and dispatch failure — when the volume of
/// control traffic is highest and the process has the least capacity to carry it.</para>
///
/// <para>🚨 <b>Why a delivery PROPERTY and not a CLR-type test (issue #1485).</b> Every mesh
/// delivery is handed to <c>IRoutingService.DeliverMessage</c> as
/// <c>delivery.Package(hub.JsonSerializerOptions)</c>, which replaces the payload with
/// <see cref="RawJson"/>. Guards written as <c>delivery.Message is DeliveryFailure</c> /
/// <c>…HasAttribute&lt;CanBeIgnoredAttribute&gt;()</c> therefore inspect <c>RawJson</c> on the routed
/// path and CANNOT match — five such guards, in both routers, were dead code, and the routers whose
/// comments said they "both agree" agreed only in being uniformly dead. The cure is the one
/// <c>Package</c> already uses for <see cref="IDiagnosticKeyed"/>: stamp the answer onto the
/// ENVELOPE before the payload type is gone, and have the guards read the envelope.</para>
///
/// <para>🚨 <b>The FACT is stamped, never the type name.</b> A general-purpose "what was this?"
/// oracle on the envelope invites downstream code to make parsing decisions from it; the single
/// question the routing layer asks is "may I answer this", so that is the only thing recorded. And
/// only the SUPPRESSED case is stamped: absence means "answerable", which keeps the stamp off the
/// hot path for ordinary traffic and makes an unstamped delivery (one that never went through
/// <c>Package</c>, or arrived pre-serialised from an external client) degrade to exactly the
/// pre-#1485 CLR-type behaviour — never to "answer something you must not".</para>
/// </summary>
public static class AnswerPolicy
{
    /// <summary>
    /// Delivery-property key under which <c>MessageDelivery.Package(...)</c> records that this
    /// delivery's ORIGINAL payload must not be answered, immediately before that payload's type is
    /// erased to <see cref="RawJson"/>.
    ///
    /// <para>🚨 The VALUE is never read — only the key's presence is. That is deliberate: a
    /// delivery property crosses the gRPC/SignalR wire as JSON and comes back as a
    /// <c>JsonElement</c> rather than the <see cref="bool"/> that was stamped (the same round-trip
    /// <c>MessageStormBreaker.ResolvePayloadKey</c> documents for <see cref="IDiagnosticKeyed"/>),
    /// so a value comparison would silently stop matching after a wire hop while a presence check
    /// cannot.</para>
    /// </summary>
    public const string SuppressedProperty = "AnswerSuppressed";

    /// <summary>
    /// Whether a <see cref="DeliveryFailure"/> must NEVER be sent for this PAYLOAD. Answers from
    /// the CLR type, so it is meaningful only BEFORE packaging — call it at the packaging boundary
    /// (and as the fallback for a delivery that carries no stamp); everywhere downstream use
    /// <see cref="MayAnswer"/>.
    /// </summary>
    /// <param name="message">The message payload, as posted.</param>
    /// <returns>True when the payload is a <see cref="DeliveryFailure"/> or is marked
    /// <see cref="CanBeIgnoredAttribute"/>.</returns>
    public static bool IsAnswerSuppressed(object? message) =>
        message is DeliveryFailure
        || (message is not null
            // Attribute.IsDefined — no array allocation, and the CLR caches the attribute metadata
            // per type internally (the same call MemberInfoExtensions.HasAttribute makes).
            && Attribute.IsDefined(message.GetType(), typeof(CanBeIgnoredAttribute), inherit: true));

    /// <summary>
    /// Whether the routing layer may answer <paramref name="delivery"/>'s sender with a
    /// <see cref="DeliveryFailure"/>. Reads the stamp <c>Package</c> left on the envelope, and
    /// falls back to the payload's CLR type for a delivery that never crossed a packaging boundary.
    /// </summary>
    /// <param name="delivery">The delivery a routing path has given up on.</param>
    /// <returns>False for a <see cref="DeliveryFailure"/> or a <see cref="CanBeIgnoredAttribute"/>
    /// payload — whether or not its type has since been erased to <see cref="RawJson"/>.</returns>
    public static bool MayAnswer(this IMessageDelivery delivery) =>
        !delivery.Properties.ContainsKey(SuppressedProperty)
        && !IsAnswerSuppressed(delivery.Message);
}
