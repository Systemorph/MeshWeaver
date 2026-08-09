using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;

namespace MeshWeaver.Messaging;

/// <summary>
/// The HANDLER-side trail for the requests a hub tree is CURRENTLY awaiting a reply to.
///
/// <para>🚨 Why this exists. Every capture of the "callbacks still pending past the teardown
/// quiescing budget" failure (issue #981) is CALLER-ONLY: <c>SnapshotPendingCallbacks</c> names the
/// request type, its target and its age, and nothing else. That says a request was posted and its
/// callback never completed — it says nothing about what happened to the delivery at the RECEIVING
/// end, which is where the defect must be. Two independent occurrences (2 738 ms and 6 153 ms
/// against a 2 s budget, both a <c>CreateNodeRequest</c> addressed to the mesh hub itself, both with
/// every queue empty) left the mechanism unnamed for exactly that reason.</para>
///
/// <para>So this ledger records, per awaited request id, the stages the delivery actually reached:
/// posted → received by a hub → routed → deferred / dropped / shed → handler entered → handler
/// exited with a state → a response posted for it. At the quiescing timeout the trail is folded into
/// <c>MessageHub.QuiescingTimeoutDetail</c>, so the failure distinguishes:
/// <list type="bullet">
///   <item>never delivered (no <c>RECEIVED</c> stage at all) — a routing / post-pipeline problem;</item>
///   <item>delivered but never handled (<c>DEFERRED</c>, <c>DROPPED_*</c>, or no <c>HANDLER_ENTER</c>);</item>
///   <item>handled but unanswered (<c>HANDLER_EXIT</c> with no <c>RESPONSE_POSTED</c>) — the handler
///     ran and produced no reply for this correlation;</item>
///   <item>answered but the reply never landed (<c>RESPONSE_POSTED</c> with the callback still
///     pending) — the response was lost between the responder and the requester.</item>
/// </list></para>
///
/// <para><b>Lifetime + cost.</b> One ledger per hub TREE: the root hub creates it and every hosted
/// hub inherits the parent's instance through the <c>MessageHub</c> constructor, so it is an
/// instance owned by the mesh and dies with it — no static state, no <c>Clear()</c> for test
/// isolation. It holds ONLY ids with a live <c>Observe</c> callback (tracked when the response
/// subject is registered, untracked the moment it resolves, is cancelled, or its subscription is
/// disposed), so its size is bounded by the number of in-flight requests, and each entry is capped
/// at <see cref="MaxStagesPerRequest"/> stages so a delivery that bounces cannot grow it. The
/// per-delivery cost on the message hot path is one <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// lookup that short-circuits on <see cref="ConcurrentDictionary{TKey,TValue}.IsEmpty"/>; stage
/// strings are only formatted once a lookup has HIT, so an untracked delivery allocates nothing.</para>
/// </summary>
internal sealed class RequestFateLedger
{
    /// <summary>
    /// Per-request stage cap. A request that legitimately completes records a handful of stages;
    /// a delivery that bounces between hubs could otherwise append without bound while its caller
    /// waits. Past the cap the trail keeps its HEAD (the stages that explain how the delivery
    /// started out) and records that it was truncated — the head is what names the mechanism.
    /// </summary>
    private const int MaxStagesPerRequest = 16;

    private readonly ConcurrentDictionary<string, RequestFate> tracked = new();

    /// <summary>
    /// Starts a trail for <paramref name="messageId"/>. Called from the ONE place a hub registers a
    /// pending response callback, so "tracked" and "awaited" are the same set by construction.
    /// </summary>
    /// <param name="messageId">The request delivery's id — the same id the response correlates to.</param>
    /// <param name="requester">The hub that issued the request.</param>
    /// <param name="requestType">The request's type name.</param>
    /// <param name="target">The address the request was addressed to, when known.</param>
    public void Track(string messageId, Address requester, string requestType, Address? target)
        => tracked.TryAdd(messageId, new RequestFate(requestType, requester, target));

    /// <summary>
    /// Drops the trail for <paramref name="messageId"/> — the callback resolved, was cancelled, or
    /// its subscription was disposed. Keeping resolved ids would turn a bounded ledger into a leak.
    /// </summary>
    /// <param name="messageId">The request delivery's id.</param>
    public void Untrack(string messageId) => tracked.TryRemove(messageId, out _);

    /// <summary>
    /// The trail for <paramref name="messageId"/>, or <c>null</c> when nothing is awaiting it.
    /// Callers MUST null-check before formatting a stage string — that is what keeps the message hot
    /// path allocation-free for the overwhelming majority of deliveries, which nobody is awaiting.
    /// </summary>
    /// <param name="messageId">The delivery id to look up.</param>
    /// <returns>The live trail, or <c>null</c>.</returns>
    public RequestFate? Find(string? messageId)
    {
        if (tracked.IsEmpty || messageId is not { Length: > 0 })
            return null;
        return tracked.TryGetValue(messageId, out var fate) ? fate : null;
    }

    /// <summary>
    /// Renders the handler-side trail for <paramref name="messageId"/> as one line, or an explicit
    /// "no trail" verdict — which is itself evidence: it means no hub in this tree ever saw the
    /// request, so it was lost before routing or is being handled outside this tree.
    /// </summary>
    /// <param name="messageId">The request delivery's id.</param>
    /// <returns>A human-readable, single-line trail.</returns>
    public string Describe(string messageId)
        => Find(messageId)?.Render()
           ?? "<no trail: no hub in this tree ever recorded this request — it was never posted "
              + "into the pipeline, or its owner hub belongs to another tree>";

    /// <summary>
    /// One awaited request's ordered stage trail. Appended from the hub action blocks of every hub
    /// the delivery passes through, so it is guarded by its own lock rather than relying on the
    /// single-threaded turn loop (a request crossing hubs is touched by several).
    /// </summary>
    internal sealed class RequestFate
    {
        private readonly Lock gate = new();
        private readonly long startedTicks = Stopwatch.GetTimestamp();
        private ImmutableList<string> stages = ImmutableList<string>.Empty;
        private int dropped;

        internal RequestFate(string requestType, Address requester, Address? target)
        {
            RequestType = requestType;
            Requester = requester;
            Target = target;
        }

        /// <summary>The request's type name, captured when the callback was registered.</summary>
        public string RequestType { get; }
        /// <summary>The hub that issued the request and is awaiting the reply.</summary>
        public Address Requester { get; }
        /// <summary>The address the request was addressed to, when the caller supplied one.</summary>
        public Address? Target { get; }

        /// <summary>
        /// Appends one stage, stamped with the elapsed time since the callback was registered — the
        /// same clock the pending-callback age uses, so a trail lines up with "…(6153ms)" directly.
        /// </summary>
        /// <param name="stage">The stage description (e.g. <c>HANDLER_EXIT state=Processed</c>).</param>
        /// <param name="hub">The hub the stage happened on.</param>
        public void Add(string stage, Address hub)
        {
            var elapsedMs = (long)((Stopwatch.GetTimestamp() - startedTicks) * 1000.0 / Stopwatch.Frequency);
            lock (gate)
            {
                if (stages.Count >= MaxStagesPerRequest)
                {
                    dropped++;
                    return;
                }
                stages = stages.Add($"{stage}@{hub}(+{elapsedMs}ms)");
            }
        }

        /// <summary>Renders the ordered trail as one arrow-separated line, plus a verdict.</summary>
        /// <returns>The trail, or a marker when no stage was ever recorded.</returns>
        public string Render()
        {
            ImmutableList<string> snapshot;
            int truncated;
            lock (gate)
            {
                snapshot = stages;
                truncated = dropped;
            }
            if (snapshot.Count == 0)
                return "<never reached any hub in this tree: posted but no RECEIVED stage was recorded>";
            var line = string.Join(" → ", snapshot);
            if (truncated > 0)
                line += $" … (+{truncated} more stage(s) suppressed)";
            return $"{line}{Environment.NewLine}      ⇒ {Verdict(snapshot)}";
        }

        /// <summary>
        /// Reduces the stage trail to the ONE sentence a reader needs — which of the mutually
        /// exclusive failure shapes this is.
        ///
        /// <para>Raw stages alone reproduce the ambiguity this instrumentation exists to remove: a
        /// reader who sees <c>HANDLER_EXIT state=Processed</c> and nothing after it still has to
        /// know that the canonical mesh handlers reply from a DETACHED observable before they can
        /// tell "still working" from "terminated silently". The verdict states it.</para>
        ///
        /// <para>🚨 It reports what was OBSERVED and nothing more. When the reply is owed by code
        /// that records no terminal stage of its own, the verdict says exactly that rather than
        /// guessing which of the two it was.</para>
        /// </summary>
        /// <param name="snapshot">The recorded stages, in order.</param>
        /// <returns>A single-sentence verdict.</returns>
        private static string Verdict(ImmutableList<string> snapshot)
        {
            bool Has(string token) => snapshot.Any(s => s.StartsWith(token, StringComparison.Ordinal));

            if (Has("RESPONSE_POSTED"))
                return "a reply WAS posted for this correlation and the callback is STILL pending — "
                     + "the reply was lost between the responder and the requester, so chase the "
                     + "response delivery, not the handler.";
            if (snapshot.Any(s => s.Contains("_ERROR", StringComparison.Ordinal)
                                  || s.StartsWith("HANDLER_FAULT", StringComparison.Ordinal)))
                return "the chain FAULTED and no reply was posted — the fault is the cause; find "
                     + "why its error arm does not answer the requester.";
            if (snapshot.Any(s => s.Contains("COMPLETED_EMPTY", StringComparison.Ordinal))
                || Has("HANDLER_COMPLETED_WITHOUT_DELIVERY"))
                return "the chain COMPLETED WITHOUT PRODUCING A REPLY — nothing will ever answer "
                     + "this request. This is a terminated chain, NOT a slow one: look for a "
                     + "filtering operator (a Where that dropped the only element, an "
                     + "Observable.Empty branch) upstream of the post.";
            if (Has("NO_HANDLER_MATCHED"))
                return "the delivery reached its target hub and NO handler matched its type.";
            if (Has("HANDLER_ENTER"))
                return "a handler was entered and no reply, completion or fault has been recorded "
                     + "since — the work that owes the reply is either STILL RUNNING or terminated "
                     + "inside code that records no stage. Add hub.NoteRequestStage(request.Id, …) "
                     + "at that handler's terminal arms to split the two.";
            if (snapshot.Any(s => s.StartsWith("DROPPED", StringComparison.Ordinal)
                                  || s.StartsWith("SHED_", StringComparison.Ordinal)
                                  || s.StartsWith("DEFERRED", StringComparison.Ordinal)))
                return "the receiving hub took the delivery and then PARKED or DISCARDED it — the "
                     + "last stage names the gate, breaker or shutdown check responsible.";
            if (Has("RECEIVED"))
                return "the delivery reached a hub but no handler was ever entered — it is still "
                     + "being routed, or it was accepted and never executed.";
            return "the delivery never reached any hub in this tree — it was lost before routing, "
                 + "or its target lives outside this tree.";
        }
    }
}
