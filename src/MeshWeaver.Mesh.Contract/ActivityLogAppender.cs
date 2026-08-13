using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// The ONE way to write log messages onto a persisted activity MeshNode. Appends through the
/// canonical <c>GetMeshNodeStream(path).Update(...)</c>, keeps <see cref="ActivityLog.Messages"/>
/// bounded, and flushes whatever scrolls out of the window into an
/// <see cref="ActivityLogSegment"/> satellite under <c>{activityPath}/_Log</c>.
///
/// <para><b>Why bounding is the whole point.</b> Every <c>stream.Update</c> re-serialises the entire
/// <c>MeshNode.Content</c> to compute its patch, so N appends onto one growing list cost O(N²) in CPU
/// and allocation — measured at ~719 MB of serialisation for a single memex-cloud import activity
/// (5,239 writes over a 141 kB node), and the dominant term in that pod's CFS throttling. A delta
/// field cannot fix it: the cross-hub path ships an RFC 7396 merge patch, which clones a changed array
/// whole, and the three-way merge's base extraction clones the previous array as well. With the head
/// bounded, each write is O(window) = O(1) and the activity as a whole is O(N).</para>
///
/// <para><b>Below the window nothing changes.</b> An activity that never exceeds
/// <see cref="ActivityLog.MessageWindowLimit"/> takes exactly the same single write per append it
/// always did, with byte-identical content — so short activities (nearly all of them) are untouched
/// and only the long ones, the ones that actually hurt, take the new path.</para>
///
/// <para><b>The flush is lock-free and loses nothing.</b> A seal is CLAIMED inside the head's update
/// lambda (<see cref="ActivityLog.ClaimSeal"/>), which the owning hub serialises — so exactly one
/// appender can claim each slice, and no two claims overlap. The claimed messages stay on the head
/// until the segment write succeeds; only then are they trimmed
/// (<see cref="ActivityLog.CompleteSeal"/>). A crash or a failed segment write therefore loses nothing
/// and needs no watchdog: the next append re-attempts the same slice, because the claim is still
/// standing and the messages are still there.</para>
///
/// <para>Stateless static helpers — no DI service, no per-path queue, no lock. Per
/// <c>Doc/Architecture/AsynchronousCalls.md</c> → "Static handlers compose".</para>
/// </summary>
public static class ActivityLogAppender
{
    /// <summary>The namespace segment holding an activity's sealed log segments.</summary>
    public const string SegmentNamespaceSegment = "_Log";

    /// <summary>The node type of a sealed log segment satellite.</summary>
    public const string SegmentNodeType = "ActivityLogSegment";

    /// <summary>The namespace an activity's <see cref="ActivityLogSegment"/> satellites live in.</summary>
    /// <param name="activityPath">Path of the activity node.</param>
    /// <returns><c>{activityPath}/_Log</c>.</returns>
    public static string SegmentNamespace(string activityPath) =>
        $"{activityPath}/{SegmentNamespaceSegment}";

    /// <summary>
    /// Appends <paramref name="messages"/> to the activity at <paramref name="activityPath"/>, applying
    /// <paramref name="mutate"/> (terminal status, End, ReturnValue, …) in the SAME write so a reader
    /// can never observe the terminal status before the lines that explain it.
    ///
    /// <para>Cold — the write runs on Subscribe. Emits the updated activity node once, then completes.
    /// When the append overflows the window the returned observable also covers the segment write and
    /// the trim, so a subscriber that waits for completion knows the flush has landed.</para>
    /// </summary>
    /// <param name="hub">The hub performing the write.</param>
    /// <param name="activityPath">Path of the activity node.</param>
    /// <param name="messages">Messages to append; may be empty when <paramref name="mutate"/> carries the change.</param>
    /// <param name="mutate">Optional additional change to the log, applied after the append in the same write.</param>
    /// <param name="logger">Logger for best-effort diagnostics.</param>
    /// <returns>A cold observable emitting the updated activity node.</returns>
    public static IObservable<MeshNode> Append(
        IMessageHub hub,
        string activityPath,
        IReadOnlyList<LogMessage> messages,
        Func<ActivityLog, ActivityLog>? mutate = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentException.ThrowIfNullOrEmpty(activityPath);

        var workspace = hub.GetWorkspace();
        var options = hub.JsonSerializerOptions;
        var stream = workspace.GetMeshNodeStream(activityPath);

        // 🚨 stream.Update(...) is invoked EAGERLY, here, and it must stay that way — do NOT wrap
        // this in Observable.Defer. UpdateRemote captures the caller's AccessContext AT THE .Update()
        // CALL, not at subscribe (MeshNodeStreamExtensions: `LastModifiedBy = capturedContextAtEntry`),
        // and every activity producer calls Append inside a scope that has deliberately re-established
        // the activity's OWNER (ActivityRunner + AccessContextScope.FromNode). Deferring construction
        // moves the capture to whatever ambient identity happens to be on the subscribing thread — a
        // pooled hop carrying someone else's context in production, which is precisely the
        // identity-confusion defect ActivityOwnerCredentialTest exists to pin. It caught this.
        //
        // `settled` is therefore per Append CALL rather than per subscription, which is what this
        // method's contract already implies: an Append is one write, subscribed once. Two subscribers
        // to the same cold Append would each re-run the write against the same path, and the release
        // reads only Status — so the shared local is harmless even then.
        MeshNode? settled = null;
        return stream
            .Update(node =>
            {
                // ContentAs, never `is ActivityLog`: a cross-hub update lambda diffs against a LOCAL
                // mirror whose Content can be a degraded JsonElement — a plain type test would be
                // null, the lambda would no-op, and the patch would never be sent.
                if (node.ContentAs<ActivityLog>(options, logger) is not { } log) return node;
                var updated = log.Append(messages);
                if (mutate is not null) updated = mutate(updated);
                // Claim AFTER the append + mutate so the decision sees the final window size.
                return node with { Content = updated.ClaimSeal() };
            })
            .SelectMany(updated => FlushClaimedSeal(hub, activityPath, stream, updated, options, logger))
            // 🚨 onCompleted, NOT onNext. Releasing tears the path's upstream sync streams down, and
            // this write is still in flight when its value is emitted — cutting the stream out from
            // under it would strand the write's own terminal and leave the per-path queue to advance
            // on its 5 s backstop instead. After completion there is nothing left to strand, and for
            // a terminal activity there is no next write by definition.
            .Do(node => settled = node,
                () =>
                {
                    if (settled is not null)
                        ReleaseMirrorWhenFinal(hub, activityPath, settled, options, logger);
                });
    }

    /// <summary>
    /// An activity that has reached a terminal status will never be written again — so the warm
    /// mirror the write path opened for it is not a cache, it is a leak with a keep-alive attached.
    ///
    /// <para><b>The mechanism, and why "nothing reclaims it" was the wrong diagnosis (#1324).</b>
    /// A cross-hub write resolves a shared <c>IMeshNodeStreamCache</c> entry for the path, and that
    /// entry's mirror posts a <c>HeartBeatEvent</c> to the owning hub every 45 s for the express
    /// purpose of keeping its grain alive. Two reclaimers exist and BOTH are defeated by it: the
    /// cache's own idle sweep needs the entry untouched for ten minutes, and Orleans' idle
    /// collection (like <c>KernelContainer.DisposeOnTimeout</c>) is re-armed by every inbound
    /// message. So a finished compile or import does not merely wait to be collected — it actively
    /// prevents its own collection, pinning its <c>_Activity/…</c> node hub and the sync sub-hubs on
    /// both sides of the mirror. Measured on <c>NodeTypeRecompileAlcLeakTest</c>, matched runs from
    /// an identical baseline: <b>6.5 → 5.0 retained hubs per compile activity</b> (8.7 → 6.0 per
    /// recompile overall), with the mirror-side hubs going <b>3 → 0</b>.</para>
    ///
    /// <para>Releasing here is an EVENT, not a shorter timer: the terminal status is proof, supplied
    /// by the writer, that the heuristic the sweep is waiting out has already been decided. A reader
    /// still watching the finished activity keeps it — <c>ReleaseIfUnwatched</c> makes the same
    /// atomic zero-subscriber check the sweep makes, and simply declines when someone is attached.
    /// Best-effort by construction: if the release does not happen the idle sweep still gets there,
    /// so nothing here may fail the write that reported the status.</para>
    /// </summary>
    private static void ReleaseMirrorWhenFinal(
        IMessageHub hub,
        string activityPath,
        MeshNode final,
        System.Text.Json.JsonSerializerOptions options,
        ILogger? logger)
    {
        if (final.ContentAs<ActivityLog>(options, logger) is not { } log || !log.Status.IsTerminal())
            return;
        var cache = hub.ServiceProvider.GetService<IMeshNodeStreamCache>();
        if (cache is null)
            return;
        if (cache.ReleaseIfUnwatched(activityPath))
            logger?.LogDebug(
                "Activity {Path}: reached {Status} — released its shared mirror", activityPath, log.Status);
    }

    /// <summary>
    /// Writes the currently claimed slice into its segment satellite and trims it off the head. A
    /// no-op — passing the node straight through — when no seal is claimed, which is the common case.
    ///
    /// <para>Reads the claim off the node the append just produced rather than off a captured local, so
    /// it also picks up a claim left standing by an earlier append whose segment write failed. That is
    /// the entire recovery mechanism: no timer, no retry loop, no watchdog.</para>
    /// </summary>
    private static IObservable<MeshNode> FlushClaimedSeal(
        IMessageHub hub,
        string activityPath,
        MeshNodeStreamHandle stream,
        MeshNode updated,
        System.Text.Json.JsonSerializerOptions options,
        ILogger? logger)
    {
        if (updated.ContentAs<ActivityLog>(options, logger) is not { SealingCount: > 0 } log)
            return Observable.Return(updated);

        var sealedMessages = log.SealedMessages;
        if (sealedMessages.Count == 0)
            return Observable.Return(updated);

        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
        {
            // No mesh service (a bare test hub): the claim stays standing and the messages stay on
            // the head — degraded to the pre-window behaviour, never lost.
            logger?.LogDebug(
                "Activity {Path}: no IMeshService, cannot seal {Count} messages into a segment",
                activityPath, sealedMessages.Count);
            return Observable.Return(updated);
        }

        // The index IS the segment count, so a retried flush re-writes the same path with the same
        // content rather than minting a duplicate. CreateOrUpdateNode makes that idempotent.
        var index = log.SegmentCount;
        var segmentId = index.ToString("D6");
        var segmentNode = new MeshNode(segmentId, SegmentNamespace(activityPath))
        {
            Name = $"Log {log.SealedFirstOrdinal}-{log.SealedFirstOrdinal + sealedMessages.Count - 1}",
            NodeType = SegmentNodeType,
            // A satellite delegates access to the content entity its OWNER delegates to — never to the
            // activity node (itself a satellite), whose path carries no permissions of its own.
            MainNode = string.IsNullOrEmpty(updated.MainNode) ? activityPath : updated.MainNode,
            State = MeshNodeState.Active,
            Content = new ActivityLogSegment
            {
                Id = segmentId,
                FirstOrdinal = log.SealedFirstOrdinal,
                ActivityPath = activityPath,
                Messages = sealedMessages,
            },
        };

        return meshService.CreateOrUpdateNode(segmentNode)
            .SelectMany(_ => stream.Update(node =>
                node.ContentAs<ActivityLog>(options, logger) is { SealingCount: > 0 } current
                    ? node with { Content = current.CompleteSeal() }
                    : node))
            // A failed segment write must never fail the work being logged. The claim stays standing
            // and the messages stay on the head, so the next append retries the identical slice.
            .Catch<MeshNode, Exception>(ex =>
            {
                logger?.LogDebug(ex,
                    "Activity {Path}: sealing {Count} messages into segment {Index} failed; "
                    + "they stay on the head and the next append retries",
                    activityPath, sealedMessages.Count, index);
                return Observable.Return(updated);
            });
    }
}
