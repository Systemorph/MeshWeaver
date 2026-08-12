namespace MeshWeaver.Messaging;

/// <summary>
/// Opt-in on a request message: a stable sub-key the pending-callback diagnostic prints alongside
/// the request type, so a pile of unanswered callbacks can be told apart from ONE unanswered thing
/// asked repeatedly.
///
/// <para>🚨 Why this exists. memex-cloud 2026-08-12 logged
/// <c>[STALE-CALLBACK] cache/…: 167 callback(s) pending &gt; 30000ms</c> — 167 <c>SubscribeRequest</c>s
/// to ONE activity node, every one showing a posted-but-undelivered ack. Type and target were
/// identical across all 167, so the log could not distinguish the two mechanisms that produce that
/// shape, and they need opposite fixes:</para>
/// <list type="bullet">
///   <item>167 DISTINCT keys ⇒ 167 separate streams for one path — a fan-out (a missing dedupe, or a
///     writer opening its own stream per write).</item>
///   <item>ONE key repeated ⇒ a single stream re-asking — a retry/resubscribe loop.</item>
/// </list>
/// <para>The evidence needed to choose was destroyed with the pod (Loki was itself down through the
/// incident window), so the next occurrence has to be self-describing. Keep the key CHEAP and
/// ALREADY-COMPUTED — it is read on every request registration.</para>
///
/// <para>🚨 The key is ALSO load-bearing, not merely diagnostic: the hub-ingestion
/// <c>MessageStormBreaker</c> keys its per-second rate counters on
/// <c>(sender, target, type, DiagnosticKey)</c>. Without the fourth component the two mechanisms
/// above are indistinguishable to the breaker too — and it does not merely mislabel them, it
/// DROPS the fan-out, because N legitimate writes to N distinct things fold into one bucket and
/// cross the per-key bar. Every message type whose traffic can be wide (one sender → one target,
/// many things) must carry this. See <c>MessageStormBreaker.ShouldDrop</c>.</para>
/// </summary>
public interface IDiagnosticKeyed
{
    /// <summary>
    /// Delivery-property name under which <c>MessageDelivery.Package(...)</c> stamps
    /// <see cref="DiagnosticKey"/> onto the ENVELOPE, immediately before the payload type is erased
    /// to <see cref="RawJson"/> for transport.
    ///
    /// <para>🚨 This is what keeps the key readable after a hub hop. A cross-hub delivery reaches
    /// the receiving hub's ingestion gate UNDESERIALIZED — the storm breaker sees
    /// <c>type=RawJson</c> and cannot look inside without parsing the payload on the hottest path
    /// in the hub. Stamping at <c>Package</c> costs one interface check next to a full JSON
    /// serialize that is already happening, and the envelope's properties survive both the
    /// in-process router hop and the gRPC/SignalR wire (the same mechanism that carries
    /// <c>PostOptions.RequestId</c> for response correlation).</para>
    /// </summary>
    public const string DeliveryProperty = "DiagnosticKey";

    /// <summary>
    /// A short, stable identifier for what this request is about — the stream id, the target path,
    /// the entity id. Never a fresh guid per post: the whole point is that two posts about the same
    /// thing share it.
    /// </summary>
    string DiagnosticKey { get; }
}
