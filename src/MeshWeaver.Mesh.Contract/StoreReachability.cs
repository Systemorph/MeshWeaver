using MeshWeaver.Data;

namespace MeshWeaver.Mesh;

/// <summary>
/// Tells <b>"the store could not be REACHED"</b> apart from <b>"the operation was REFUSED"</b> — the
/// second distinction a node-operation handler's catch-all cannot make on its own, and the sibling of
/// <see cref="CancellationClassifier"/>.
///
/// <para>🚨 <b>Why this is not a log-level preference</b> (#3050/#3051). A transient database-connect
/// timeout arriving at a create handler's terminal error arm was answered
/// <see cref="NodeCreationRejectionReason.Unknown"/> under the sentence <c>"Unexpected error during
/// node creation"</c>. Both halves are wrong in the same direction, and they are wrong about
/// DIFFERENT audiences:</para>
///
/// <list type="bullet">
///   <item><b>The operator</b> reads "unexpected error during node creation" and goes hunting for a
///     defect in the create path. The create path was fine; a database was unreachable for a few
///     seconds. This is the same wording defect #2876 fixed one layer up, where an area whose store
///     was unreachable rendered the driver's own text under <c>"Rendering failed for area Catalog"</c>
///     — naming the innocent component as the thing that failed.</item>
///   <item><b>The caller</b> reads a rejection reason that is indistinguishable from a verdict. That
///     matters because the two demand opposite responses: a create that was REFUSED must not be
///     retried, while a create that was never ATTEMPTED must be — with the SAME node id. A caller
///     that reads "refused" and mints a fresh id on its next attempt writes a DUPLICATE, which is
///     #2229's shape exactly.</item>
/// </list>
///
/// <para><b>What this is deliberately NOT: a retry.</b> The bounded retry already exists and already
/// ran — <c>TransientStorageFaults.RetryTransientConnect</c> (#2521) wraps the storage-backed
/// observables at 250 → 500 → 1000 ms before the last error is surfaced. A fault that reaches a
/// handler's terminal arm is one whose budget is honestly spent, so retrying it HERE would be a
/// second retry aimed at the resource that is already the bottleneck — the same non-choice #3031
/// states for the render path, and the band-aid AGENTS.md forbids. The answer to a spent budget is to
/// report the condition accurately, not to spend more of someone else's.</para>
///
/// <para><b>One rule, three consumers.</b> The predicate forwards to
/// <see cref="StorageFaults.IsTransientConnectFault"/> in <c>MeshWeaver.Data.Contract</c> — the one
/// assembly the query fan-in, the layout renderer and the node-operation handlers can all see. A
/// second copy would drift silently in either direction: a fault the fan-in retries but a create
/// reports as a defect, or an outage a create excuses that the fan-in never retried.</para>
/// </summary>
public static class StoreReachability
{
    /// <summary>
    /// True when <paramref name="exception"/> means the data store could not be REACHED — a transient
    /// driver connect/timeout fault, so the operation was never evaluated and nothing was written.
    ///
    /// <para>The matched class is <see cref="StorageFaults.IsTransientConnectFault"/>'s: a
    /// <see cref="System.Data.Common.DbException"/> carrying a connection-class SQLSTATE, or one
    /// wrapping a network-level <see cref="TimeoutException"/> /
    /// <see cref="System.Net.Sockets.SocketException"/> / <see cref="IOException"/> — the shape of
    /// Npgsql's <c>"Failed to connect to 10.42.18.4:5432 ---&gt; TimeoutException: Timeout during
    /// connection attempt"</c>. A real query/schema error (<c>42P01</c>, <c>23505</c>) is NOT matched
    /// and keeps its loud "unexpected" treatment: excusing a defect as an outage would hide it, which
    /// is the mirror image of the bug this classifier fixes.</para>
    /// </summary>
    /// <param name="exception">The exception a handler's error branch received; may be null.</param>
    public static bool IsStoreUnreachable(Exception? exception)
        => StorageFaults.IsTransientConnectFault(exception);

    /// <summary>
    /// The ONE sentence every node operation answers with when <see cref="IsStoreUnreachable"/> is
    /// true — so a user, an agent and a test all read the same words, and the bulk sibling cannot
    /// drift from the singular one.
    ///
    /// <para>It says three things on purpose: the operation was <b>not attempted</b>, <b>nothing was
    /// written</b>, and <b>retrying the same request is meaningful</b>. The last clause is the one
    /// that keeps a caller from minting a new id for its next attempt (#2229).</para>
    /// </summary>
    /// <param name="operation">The operation, as a sentence subject — e.g. <c>"Node creation at 'x/y'"</c>.</param>
    /// <returns>A message safe to hand to a caller, a log line and an activity alike.</returns>
    public static string DescribeNotAttempted(string operation)
        => $"{operation} could not be attempted: the data store was unreachable. Nothing was written — "
           + "this is an availability failure, not a refusal, so retrying the same request "
           + "(with the same node id) is meaningful.";

    /// <summary>
    /// The same availability verdict for a write that had ALREADY STARTED when the store became
    /// unreachable — so part of the batch may have landed.
    ///
    /// <para>🚨 Why this is a separate sentence rather than a nuance in the one above.
    /// "Nothing was written" is a claim ABOUT THE DATA, and it is only true when the failure
    /// preceded the write. Saying it after a partial landing tells a caller the store is in a state
    /// it is not — and the natural next step, retrying the whole batch, then double-writes whatever
    /// did land. A wrong claim about durability is worse than an vague one, so when we do not know,
    /// we say we do not know.</para>
    /// </summary>
    /// <param name="operation">The operation, as a sentence subject.</param>
    /// <returns>A message safe to hand to a caller, a log line and an activity alike.</returns>
    public static string DescribeMayHavePartiallyLanded(string operation)
        => $"{operation} failed part-way: the data store became unreachable after the write had "
           + "started, so an UNKNOWN subset may already be durable. This is an availability failure, "
           + "not a refusal — but read the current state before retrying, because re-sending the "
           + "whole request could duplicate whatever landed.";
}
