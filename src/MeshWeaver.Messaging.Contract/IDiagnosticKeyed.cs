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
/// </summary>
public interface IDiagnosticKeyed
{
    /// <summary>
    /// A short, stable identifier for what this request is about — the stream id, the target path,
    /// the entity id. Never a fresh guid per post: the whole point is that two posts about the same
    /// thing share it.
    /// </summary>
    string DiagnosticKey { get; }
}
