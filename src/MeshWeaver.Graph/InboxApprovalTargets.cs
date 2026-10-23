using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
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
    /// <summary>How long a target read may stay silent before its request is kept: an unreadable target must not hide a request.</summary>
    internal static readonly TimeSpan ReadWithin = TimeSpan.FromSeconds(5);

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
            : InboxRows.ApprovalTargetOf(node.NodeType, InboxRows.ContentOf(node, options)) is { Length: > 0 } target ? target : null;

    /// <summary>
    /// Whether a node exists at <paramref name="path"/>, as <paramref name="owner"/> sees it — a LIVE exact-path
    /// listing (empty when the node is absent: never a point read of a maybe-absent path, which would mint a
    /// NotFound that opens the stream cache's storm-breaker on the path). Three answers, never conflated:
    /// <c>true</c> — a matching row was OBSERVED; <c>false</c> — a COMPLETE empty answer; <c>null</c> — an INCOMPLETE
    /// empty one (a silent provider, an unfinished storage read), which says nothing. A caller opens the node's own
    /// stream only on <c>true</c> and leaves <c>null</c> to its own fallback.
    /// </summary>
    /// <param name="mesh">The mesh service.</param>
    /// <param name="path">The node's path.</param>
    /// <param name="owner">The identity the listing runs as.</param>
    internal static IObservable<bool?> Exists(IMeshService mesh, string path, string owner)
        => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(ExistsQuery(path), owner) with { Limit = 1 })
            .Scan((bool?)false, Fold);

    /// <summary>
    /// The exact-path listing that finds <paramref name="path"/>. The path is a quoted LITERAL
    /// (<see cref="QueryParser.QuoteLiteral"/>): a path with a space or a query metacharacter stays ONE path
    /// instead of becoming a path and a free-text term. Pure.
    /// </summary>
    /// <param name="path">The node's path.</param>
    internal static string ExistsQuery(string path)
        => $"path:{QueryParser.QuoteLiteral(path)} select:path,id,nodeType";

    /// <summary>One step of <see cref="Exists"/>: what <paramref name="change"/> says about presence. Pure.</summary>
    /// <param name="known">What was known before.</param>
    /// <param name="change">The listing's next answer.</param>
    internal static bool? Fold(bool? known, QueryResultChange<MeshNode> change)
        => change.ChangeType switch
        {
            QueryChangeType.Removed => false,
            QueryChangeType.Added or QueryChangeType.Updated => true,
            _ => change.Items.Count > 0 ? true
                : change.SnapshotIncomplete || change.SilentProviders is { Count: > 0 } ? null
                : false,
        };

    /// <summary>
    /// The approval an item ANNOUNCES, once it is known: the target of a notification that announces an approval,
    /// read from the item's authoritative stream after a listing observed it — nothing for any other item, and
    /// nothing while its presence is uncertain (the caller keeps showing the item itself). Failures end quietly
    /// (logged): a redirect is an improvement on the item's page, never a condition of it.
    /// </summary>
    /// <param name="hub">The hub the inbox renders on.</param>
    /// <param name="viewer">The viewer's identity, re-established around the read.</param>
    /// <param name="owner">The inbox's owner.</param>
    /// <param name="path">The item's path.</param>
    internal static IObservable<string> AnnouncedTarget(IMessageHub hub, AccessContext? viewer, string owner, string path)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InboxApprovalTargets));
        return Exists(hub.ServiceProvider.GetRequiredService<IMeshService>(), path, owner)
            .Where(exists => exists == true)
            .Take(1)
            .CarryAccessContext(hub.ServiceProvider, viewer)
            .SelectMany(_ => hub.GetMeshNodeStream(path).Take(1))
            .Select(node => TargetOf(node, hub.JsonSerializerOptions))
            .Where(target => target is not null)
            .Select(target => target!)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Inbox: could not tell whether {Path} announces an approval; showing the item itself", path);
                return Observable.Empty<string>();
            });
    }

    /// <summary>
    /// Whether the approval at <paramref name="target"/> still awaits a decision, LIVE: existence is a listing,
    /// the content is the node's own authoritative stream (an index snapshot can trail a decision), so a decision
    /// taken on the approval's own page — which never touches the notification — drops the request from an open
    /// inbox. Gone ⇒ nothing left to decide (false). A stream that fails keeps the request (logged), and so does a
    /// target that answers nothing within <see cref="ReadWithin"/>; later answers still replace that fallback.
    /// </summary>
    /// <param name="hub">The hub the inbox renders on.</param>
    /// <param name="viewer">The viewer's identity, re-established around each answer (the read runs on another turn).</param>
    /// <param name="target">The approval's path.</param>
    /// <param name="owner">The inbox's owner.</param>
    /// <param name="options">The hub's serializer options.</param>
    internal static IObservable<bool> Awaits(IMessageHub hub, AccessContext? viewer, string target, string owner, JsonSerializerOptions? options)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InboxApprovalTargets));
        var observed = Exists(hub.ServiceProvider.GetRequiredService<IMeshService>(), target, owner)
            // An uncertain answer says nothing: the request keeps showing (the fallback) until a definite one.
            .Where(exists => exists is not null)
            .Select(exists => exists!.Value)
            .DistinctUntilChanged()
            .CarryAccessContext(hub.ServiceProvider, viewer)
            .Select(exists => exists
                ? hub.GetMeshNodeStream(target)
                    .Select(node => IsPending(node, options))
                    .Catch((Exception ex) =>
                    {
                        // A node deleted while observed has nothing left to decide; any other failure keeps the request.
                        if (ex is MeshNodeReadEndedWithDeleteException)
                            return Observable.Return(false);
                        logger?.LogWarning(ex, "Inbox: could not read the approval {Target}; keeping its request", target);
                        return Observable.Return(true);
                    })
                : Observable.Return(false))
            .Switch()
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Inbox: could not tell whether the approval {Target} is still open; keeping its request", target);
                return Observable.Return(true);
            });
        return InboxLayoutArea.FirstAnswerWithin(observed, ReadWithin, true);
    }

    /// <summary>
    /// <paramref name="rows"/> without the approval requests whose target is no longer awaiting a decision, LIVE
    /// (<see cref="Awaits"/>): each emission is the answer for the rows it was asked about, and the caller
    /// SWITCHES to the latest — an obsolete batch never replaces a newer row set. Rows that announce nothing pass.
    /// </summary>
    /// <param name="hub">The hub the inbox renders on.</param>
    /// <param name="viewer">The viewer's identity.</param>
    /// <param name="rows">One leg's rows.</param>
    /// <param name="owner">The inbox's owner — whose identity the listings run as.</param>
    /// <param name="options">The hub's serializer options.</param>
    internal static IObservable<ImmutableList<InboxRow>> OnlyPending(
        IMessageHub hub, AccessContext? viewer, ImmutableList<InboxRow> rows, string owner, JsonSerializerOptions? options)
    {
        var targets = rows.Select(r => r.Target).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToImmutableArray();
        if (targets.IsEmpty)
            return Observable.Return(rows);
        return targets
            .Select(target => Awaits(hub, viewer, target, owner, options).Select(pending => (Target: target, Pending: pending)))
            .CombineLatest()
            .Select(read =>
            {
                var pending = read.ToDictionary(r => r.Target, r => r.Pending, StringComparer.Ordinal);
                return rows.Where(r => r.Target.Length == 0 || pending.GetValueOrDefault(r.Target, true)).ToImmutableList();
            });
    }
}
