using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// An approval request in the inbox is the APPROVAL, not the notification that announced it. The
/// "approval required" notification every approval flow sends its approver stays in the approver's
/// partition after the request is decided — by the inbox's Approve, on the approval's own page, anywhere —
/// and listing it for ever turned the inbox into a log of decisions long taken (owner, 2026-10-23:
/// "something announcing i had an approval?! … i should just see approvals which are not approved yet").
/// So the Approvals source keeps a request only while the node it names (<see cref="InboxRow.Target"/>) still
/// awaits a decision, and a click on the row opens that node.
///
/// <para>The inbox knows no approval kind: a target is DECIDED when its content says so in the
/// vocabulary every approval record shares — a <c>status</c> of approved, rejected or expired, or a stamped
/// decision (<see cref="DecisionStamps"/>). Anything else is still open.</para>
/// </summary>
internal static class InboxApprovalTargets
{
    /// <summary>How long one target read may take before its request is kept (and the read logged): an unreadable target must not hide a request.</summary>
    internal static readonly TimeSpan ReadWithin = TimeSpan.FromSeconds(5);

    /// <summary>How many targets are read at once.</summary>
    private const int Parallel = 8;

    /// <summary>The content properties whose presence says the approval was decided (an approver id or a decision time).</summary>
    internal static readonly ImmutableArray<string> DecisionStamps =
        ["approvedAt", "approvedBy", "approvalDate", "rejectedAt", "rejectedBy", "decidedAt", "decidedBy"];

    /// <summary>
    /// Whether <paramref name="target"/> still awaits a decision. Pure; unreadable content counts as open
    /// (a request is never dropped on what could not be read).
    /// </summary>
    /// <param name="target">The node the request names.</param>
    /// <param name="options">The hub's serializer options.</param>
    internal static bool IsPending(MeshNode target, JsonSerializerOptions? options)
    {
        var content = InboxRows.ContentOf(target, options);
        if (InboxState.FromStatus(InboxRows.StringOf(content, "status")) is InboxState.Approved or InboxState.Rejected or InboxState.Expired)
            return false;
        return !DecisionStamps.Any(stamp => !string.IsNullOrWhiteSpace(InboxRows.StringOf(content, stamp)));
    }

    /// <summary>
    /// The approval a NOTIFICATION node announces — its target path — or null when it announces none.
    /// What the reading view uses to take the viewer to the approval instead of showing the announcement. Pure.
    /// </summary>
    /// <param name="node">The node the address names.</param>
    /// <param name="options">The hub's serializer options.</param>
    internal static string? TargetOf(MeshNode? node, JsonSerializerOptions? options)
        => node is null ? null
            : InboxRows.ApprovalTargetOf(InboxRows.ContentOf(node, options)) is { Length: > 0 } target ? target : null;

    /// <summary>
    /// <paramref name="rows"/> without the approval requests whose target is no longer awaiting a decision,
    /// read as <paramref name="owner"/>. A request whose target cannot be read in time, or whose read
    /// fails, is KEPT (logged); one whose target answers COMPLETELY with nothing — gone, or not the
    /// approver's to see — has nothing left to decide and is dropped. Rows that announce nothing pass.
    /// </summary>
    /// <param name="services">Resolves the mesh service and the logger.</param>
    /// <param name="rows">One leg's rows.</param>
    /// <param name="owner">The inbox's owner — whose identity the reads run as.</param>
    /// <param name="options">The hub's serializer options.</param>
    internal static IObservable<ImmutableList<InboxRow>> OnlyPending(
        IServiceProvider services, ImmutableList<InboxRow> rows, string owner, JsonSerializerOptions? options)
    {
        var targets = rows.Select(r => r.Target).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToImmutableArray();
        if (targets.IsEmpty)
            return Observable.Return(rows);
        var mesh = services.GetRequiredService<IMeshService>();
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(InboxApprovalTargets));
        IObservable<(string Target, bool Pending)> Read(string target) =>
            mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{target} select:path,nodeType,content", owner) with { Limit = 1 })
                .Take(1)
                .Select(change => change.Items.Count > 0
                    ? IsPending(change.Items[0], options)
                    // Only a COMPLETE empty answer says the target is gone; an incomplete one is no answer.
                    : change.SnapshotIncomplete || change.SilentProviders is { Count: > 0 })
                .DefaultIfEmpty(true)
                .Timeout(ReadWithin)
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex, "Inbox: could not tell whether the approval {Target} is still open; keeping its request", target);
                    return Observable.Return(true);
                })
                .Select(pending => (target, pending));
        return targets.ToObservable().Select(Read).Merge(Parallel).ToList()
            .Select(read =>
            {
                var pending = read.ToDictionary(r => r.Target, r => r.Pending, StringComparer.Ordinal);
                return rows.Where(r => r.Target.Length == 0 || pending.GetValueOrDefault(r.Target, true)).ToImmutableList();
            });
    }
}
