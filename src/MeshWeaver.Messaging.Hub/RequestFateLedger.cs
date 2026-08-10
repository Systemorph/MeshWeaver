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
/// disposed), so its size is bounded by the number of in-flight requests, and each entry keeps at
/// most <see cref="HeadStages"/> + <see cref="TailStages"/> stages so a delivery that bounces
/// between hubs cannot grow it. The
/// per-delivery cost on the message hot path is one <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// lookup that short-circuits on <see cref="ConcurrentDictionary{TKey,TValue}.IsEmpty"/>; stage
/// strings are only formatted once a lookup has HIT, so an untracked delivery allocates nothing.</para>
/// </summary>
internal sealed class RequestFateLedger
{
    /// <summary>
    /// Stages kept from the START of a trail — how the delivery entered the mesh and where it was
    /// routed. A request that legitimately completes records a handful of stages; one that bounces
    /// between hubs could otherwise append without bound while its caller waits.
    /// </summary>
    private const int HeadStages = 10;

    /// <summary>
    /// Stages kept from the END of a trail.
    ///
    /// <para>🚨 A head-only cap would be actively harmful here: the stages that decide the VERDICT
    /// — <c>HANDLER_EXIT</c>, <c>RESPONSE_POSTED</c>, <c>*_COMPLETED_EMPTY</c>, a fault — are always
    /// the LAST ones, and a cross-hub request already spends ~12 stages on routing hops before a
    /// handler is entered at all. Truncating the tail would drop exactly the evidence the trail
    /// exists to carry, and would do it silently. So the MIDDLE is what gets suppressed.</para>
    /// </summary>
    private const int TailStages = 8;

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
    {
        var fate = new RequestFate(requestType, requester, target);
        // Seed the trail with the registration itself, so a trail can NEVER be empty. An empty
        // trail was ambiguous in exactly the way this ledger exists to remove: it could mean the
        // delivery was never posted, OR that the callback was registered after the delivery had
        // already traversed the pipeline (the `Observe(delivery)` overload registers post-hoc), and
        // the render could not tell those apart. With this stage present, "AWAITING and nothing
        // else" is an unambiguous statement: the hub started awaiting a reply under this id and no
        // delivery carrying it ever entered the pipeline.
        if (tracked.TryAdd(messageId, fate))
            fate.Add($"AWAITING {requestType}→{target?.ToString() ?? "<unset>"}", requester);
    }

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
    /// Renders the handler-side trail for <paramref name="messageId"/> on ONE line (see
    /// <see cref="RequestFate.Render"/> for why single-line is a requirement, not a preference), or
    /// an explicit "not tracked here" verdict.
    ///
    /// <para>A tracked id ALWAYS has at least the seed stage <see cref="Track"/> writes, so the
    /// fallback below means something specific: this ledger never tracked the id at all — the
    /// awaiting hub belongs to a different tree, or the callback resolved and was untracked between
    /// the pending-callback snapshot and this render.</para>
    /// </summary>
    /// <param name="messageId">The request delivery's id.</param>
    /// <returns>A human-readable, single-line trail.</returns>
    public string Describe(string messageId)
        => Find(messageId)?.Render()
           ?? "<not tracked by this hub tree: the awaiting hub belongs to another tree, or the "
              + "callback resolved between the pending-callback snapshot and this render>";

    /// <summary>
    /// One awaited request's ordered stage trail. Appended from the hub action blocks of every hub
    /// the delivery passes through, so it is guarded by its own lock rather than relying on the
    /// single-threaded turn loop (a request crossing hubs is touched by several).
    /// </summary>
    internal sealed class RequestFate
    {
        private readonly Lock gate = new();
        private readonly long startedTicks = Stopwatch.GetTimestamp();
        private ImmutableList<string> head = ImmutableList<string>.Empty;
        private ImmutableList<string> tail = ImmutableList<string>.Empty;
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
            var entry = $"{stage}@{hub}(+{elapsedMs}ms)";
            lock (gate)
            {
                if (head.Count < HeadStages)
                {
                    head = head.Add(entry);
                    return;
                }
                // Sliding tail: the newest TailStages stages always survive, so the terminal
                // evidence the verdict reads can never be the part that gets dropped.
                tail = tail.Add(entry);
                if (tail.Count > TailStages)
                {
                    tail = tail.RemoveAt(0);
                    dropped++;
                }
            }
        }

        /// <summary>
        /// Renders the ordered trail and its verdict as ONE line — no embedded newline.
        ///
        /// <para>🚨 Single-line is a hard requirement, not a formatting preference. These records
        /// are read out of <c>/tmp/meshweaver-test-trace.log</c>, a shared append-only file that
        /// every test host in a CI shard writes to and that is collected as the one artifact
        /// surviving a killed run. The access method there is <c>grep</c>. A newline before the
        /// verdict splits one record into two lines, so <c>grep &lt;messageId&gt;</c> returns the
        /// stages WITHOUT the verdict — i.e. it hands the reader the evidence and hides the
        /// conclusion, which is the exact failure mode this instrumentation exists to fix. Human
        /// readability of a long line loses to a diagnostic that greps out whole.</para>
        /// </summary>
        /// <returns>A single-line trail ending in <c>⇒ &lt;verdict&gt;</c>.</returns>
        public string Render()
        {
            ImmutableList<string> headSnapshot, tailSnapshot;
            int truncated;
            lock (gate)
            {
                headSnapshot = head;
                tailSnapshot = tail;
                truncated = dropped;
            }
            var line = string.Join(" → ", headSnapshot);
            if (truncated > 0)
                line += $" → …(+{truncated} middle stage(s) suppressed)…";
            if (tailSnapshot.Count > 0)
                line += " → " + string.Join(" → ", tailSnapshot);
            // The verdict reads head AND tail: the terminal stages it keys on live in the tail.
            return $"{line}  ⇒ {Verdict(headSnapshot.AddRange(tailSnapshot))}";
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

            // Ahead of RESPONSE_POSTED on purpose: a refused reply always carries BOTH stages (the
            // post is recorded, then the teardown guard refuses it), and "the responder's own
            // shutdown ate the reply" is a strictly sharper answer than "it was lost in transit".
            if (Has("RESPONSE_REFUSED_SHUTTING_DOWN"))
                return "the handler REPLIED but the responding hub was already past "
                     + "DisposeHostedHubs, so its own teardown guard refused the post and the "
                     + "caller burns its whole RequestTimeout un-NACKed — chase what "
                     + "recycled/deleted the RESPONDER's address mid-handler, not the requester";
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
            if (snapshot.Any(s => s.StartsWith("ROUTED", StringComparison.Ordinal)
                                  && s.Contains("state=Failed", StringComparison.Ordinal)))
                return "ROUTING FAILED and the failure was never reported back — the delivery left "
                     + "its origin hub in state=Failed and nothing answered the requester. Look at "
                     + "the not-on-target return path in MessageService.NotifyAsync, which drops a "
                     + "post-routing Failed state without calling ReportFailure.";
            if (Has("RECEIVED"))
                return "the delivery reached a hub but no handler was ever entered — it is still "
                     + "being routed, or it was accepted and never executed.";
            if (Has("POSTED"))
                return "the delivery was posted and no hub in this tree ever received it — it was "
                     + "lost between the post pipeline and routing, or its target lives outside "
                     + "this tree.";
            if (Has("POST_THREW"))
                return "the Post THREW after the callback was already registered — the request was "
                     + "never dispatched and the pending entry leaked. The stage carries the "
                     + "exception; fix that, not any handler.";
            if (Has("REGISTERED_AFTER_POST"))
                return "the callback was registered AFTER its delivery was posted (the "
                     + "Observe(delivery) overload), so the stages before registration were not "
                     + "recorded. This trail cannot say where the delivery went — re-run with the "
                     + "caller switched to Observe(request, options), which registers first.";
            // Only the seed stage, and the two post-hoc cases above are excluded.
            return "the hub registered a callback and NO delivery carrying this id ever entered the "
                 + "pipeline — nothing was posted under this correlation, so no reply was ever "
                 + "possible. The gap is in the CALLER between Observe and Post (a throw, an early "
                 + "return, a swallowed post), not in any handler.";
        }
    }
}
