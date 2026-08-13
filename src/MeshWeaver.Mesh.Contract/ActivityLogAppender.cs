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
            .SelectMany(updated => FlushClaimedSeal(hub, activityPath, stream, updated, options, logger));
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
