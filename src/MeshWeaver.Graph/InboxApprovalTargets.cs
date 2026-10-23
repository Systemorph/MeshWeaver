using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
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
    /// Whether <paramref name="target"/> still awaits a decision — <see cref="Read"/>'s <see cref="TargetReading.Open"/>.
    /// Pure; unreadable content counts as open (a request is never dropped on what could not be read).
    /// </summary>
    /// <param name="target">The node the request names.</param>
    /// <param name="options">The hub's serializer options.</param>
    /// <param name="stepUpRequired">Whether the instance requires a step-up receipt for every approval.</param>
    internal static bool IsPending(MeshNode target, JsonSerializerOptions? options, bool stepUpRequired = false)
        => Read(target, options, stepUpRequired).Open;

    /// <summary>The content properties that carry a target's own lifecycle word, in the order they are read.</summary>
    internal static readonly ImmutableArray<string> LifecycleProperties = ["state", "status"];

    /// <summary>The content properties whose presence says the target's life is over (a finish or close time).</summary>
    internal static readonly ImmutableArray<string> EndStamps = ["finishedAt", "closedAt", "completedAt"];

    /// <summary>
    /// What a target's own lifecycle says about its request — judged by the target's state, never by the ABSENCE of an
    /// approver. A target the system refused, retired as stale, failed or cancelled will never be approved, and one that
    /// is running or done was decided; only a target that still waits (its state says so, or it says nothing and no
    /// decision is stamped) is OPEN.
    /// <para>Order: a terminal lifecycle word (<c>state</c>, then <c>status</c>) or an end stamp closes it; otherwise the
    /// shared decision vocabulary (<see cref="DecisionStamps"/>) decides.</para>
    /// <para>🚨 A decision that is RECORDED but not yet CONFIRMED is not decided. With step-up required
    /// (<paramref name="stepUpRequired"/>, <c>Doc/Architecture/ApprovalStepUp</c>) a decision counts only once its
    /// decider's receipt is stamped on the node (<see cref="StepUpPaths.StampProperty"/>) and the owning control plane
    /// has ratified it; until then it is HELD — and an approver who walked away from the confirmation must still find
    /// the request in the inbox. So a recorded decision with no receipt stamped at all stays open while step-up is
    /// required. Pure; unreadable content reads as open.</para>
    /// </summary>
    /// <param name="target">The node the request names.</param>
    /// <param name="options">The hub's serializer options.</param>
    /// <param name="stepUpRequired">Whether the instance requires a step-up receipt for every approval.</param>
    internal static TargetReading Read(MeshNode target, JsonSerializerOptions? options, bool stepUpRequired = false)
    {
        var content = InboxRows.ContentOf(target, options);
        foreach (var property in LifecycleProperties)
            if (Terminal(InboxRows.StringOf(content, property)) is { } ended
                && ended is not (InboxState.Approved or InboxState.Rejected)) // a recorded decision is the vocabulary's below (step-up holds it)
                return Closed(ended, StaleError(content));
        if (EndStamps.Any(stamp => !string.IsNullOrWhiteSpace(InboxRows.StringOf(content, stamp))))
            return Closed(InboxState.Done, false);
        var status = InboxState.FromStatus(InboxRows.StringOf(content, "status"));
        var decided = status is InboxState.Approved or InboxState.Rejected
                      || DecisionStamps.Any(stamp => !string.IsNullOrWhiteSpace(InboxRows.StringOf(content, stamp)));
        if (decided && !(stepUpRequired && !HasReceipt(content)))
            return Closed(status == InboxState.Rejected || RejectedStamp(content) ? InboxState.Rejected : InboxState.Approved, false);
        return TargetReading.Awaiting;
    }

    private static TargetReading Closed(string word, bool staleError)
        => new(false, word is InboxState.Stale || (staleError && word is InboxState.Refused or InboxState.Failed or InboxState.Cancelled or InboxState.Expired)
                ? InboxState.Stale
                : word is InboxState.Running or InboxState.Requested ? InboxState.Approved : word, word);

    private static bool RejectedStamp(JsonElement? content)
        => InboxRows.StringOf(content, "rejectedAt") is { Length: > 0 } || InboxRows.StringOf(content, "rejectedBy") is { Length: > 0 };

    /// <summary>
    /// The outcome a lifecycle word names when it is final — or a decision already taken and being carried out
    /// (<c>running</c>, <c>requested</c>) — else null: a word that is not part of the vocabulary, or that says the
    /// target still waits, closes nothing. Pure.
    /// </summary>
    /// <param name="word">The stored word, in any case or spelling.</param>
    internal static string? Terminal(string? word)
        => new string((word ?? string.Empty).Where(char.IsLetter).ToArray()).ToLowerInvariant() switch
        {
            "refused" => InboxState.Refused,
            "failed" or "error" => InboxState.Failed,
            "cancelled" or "canceled" => InboxState.Cancelled,
            "stale" or "superseded" => InboxState.Stale,
            "expired" => InboxState.Expired,
            "approved" => InboxState.Approved,
            "rejected" or "declined" => InboxState.Rejected,
            "done" or "completed" or "succeeded" or "warning" or "closed" or "merged" => InboxState.Done,
            "running" or "inprogress" => InboxState.Running,
            "requested" or "queued" => InboxState.Requested,
            _ => null,
        };

    /// <summary>Whether the target's <c>error</c> says it went STALE (retired because what it was planned against moved on). Pure.</summary>
    private static bool StaleError(JsonElement? content)
        => InboxRows.StringOf(content, "error") is { Length: > 0 } error
           && new string(error.SkipWhile(c => !char.IsLetter(c)).Take(5).ToArray()).Equals("stale", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether any decider's step-up receipt is stamped on the content (<see cref="StepUpPaths.StampProperty"/>). Pure.</summary>
    private static bool HasReceipt(JsonElement? content)
    {
        if (content is not { } c)
            return false;
        foreach (var property in c.EnumerateObject())
            if (string.Equals(property.Name, StepUpPaths.StampProperty, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Object)
                return property.Value.EnumerateObject().Any(r => r.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(r.Value.GetString()));
        return false;
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
    /// What the approval at <paramref name="target"/> says about its request, LIVE (<see cref="Read"/>): existence is a
    /// listing, the content is the node's own authoritative stream (an index snapshot can trail a decision), so a decision
    /// taken on the approval's own page — which never touches the notification — moves the request out of an open
    /// inbox. Gone ⇒ <see cref="TargetReading.Gone"/>. A stream that fails keeps the request open (logged), and so does a
    /// target that answers nothing within <see cref="ReadWithin"/>; later answers still replace that fallback.
    /// </summary>
    /// <param name="hub">The hub the inbox renders on.</param>
    /// <param name="viewer">The viewer's identity, re-established around each answer (the read runs on another turn).</param>
    /// <param name="target">The approval's path.</param>
    /// <param name="owner">The inbox's owner.</param>
    /// <param name="options">The hub's serializer options.</param>
    internal static IObservable<TargetReading> Reading(IMessageHub hub, AccessContext? viewer, string target, string owner, JsonSerializerOptions? options)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InboxApprovalTargets));
        var observed = ObserveNode(hub, viewer, target, Exists(hub.ServiceProvider.GetRequiredService<IMeshService>(), target, owner),
                // Read live per state: the instance's step-up requirement may change while the inbox is open.
                node => node is null ? TargetReading.Gone : Read(node, options, hub.ServiceProvider.GetService<IStepUpService>()?.Options.Enabled == true),
                absent: TargetReading.Gone)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Inbox: could not read the approval {Target}; keeping its request", target);
                return Observable.Return(TargetReading.Awaiting);
            });
        return InboxLayoutArea.FirstAnswerWithin(observed, ReadWithin, TargetReading.Awaiting);
    }

    /// <summary>
    /// The projection of the node at <paramref name="path"/>, LIVE, following its whole lifecycle: the node's own
    /// authoritative stream is opened when <paramref name="presence"/> says a matching row was observed
    /// (<see cref="Exists"/>; an uncertain answer says nothing), the projection of each of its states is emitted,
    /// and <paramref name="absent"/> is emitted when it is gone — by a complete empty listing, or by the read ending
    /// on the deletion (the stream cache ends its readers with <see cref="MeshNodeReadEndedWithDeleteException"/>
    /// or a NotFound for the path, which can arrive BEFORE the listing reports it). Nothing is terminated by a
    /// deletion: a node deleted and recreated — even when a provider reports both as one Updated frame, so that
    /// presence never reads <c>false</c> in between — is picked up again by the next presence observation, and a read
    /// that is still healthy is never restarted by an ordinary update. Any other failure is propagated.
    /// <para>The stream is opened under <paramref name="viewer"/>'s identity, re-established around each presence answer.</para>
    /// </summary>
    /// <param name="hub">The hub the inbox renders on.</param>
    /// <param name="viewer">The viewer's identity.</param>
    /// <param name="path">The node's path.</param>
    /// <param name="presence">Whether the node exists (<see cref="Exists"/>); each answer is observed, not only changes.</param>
    /// <param name="project">The projection of the node's content (null while it has none).</param>
    /// <param name="absent">What the projection is while the node is gone.</param>
    internal static IObservable<T> ObserveNode<T>(IMessageHub hub, AccessContext? viewer, string path,
        IObservable<bool?> presence, Func<MeshNode?, T> project, T absent)
        => Observable.Create<T>(observer =>
        {
            var gate = new object();
            var reading = false;
            var presenceEnded = false;
            var read = new System.Reactive.Disposables.SerialDisposable();
            // The observation ends when presence has ended AND no read is open: a finite presence snapshot (a provider that
            // completes its listing) must not dispose the content read it just opened for a `true`.
            void CompleteIfDone()
            {
                if (presenceEnded && !reading)
                    observer.OnCompleted();
            }
            var watch = presence
                .Where(exists => exists is not null)
                .CarryAccessContext(hub.ServiceProvider, viewer)
                .Subscribe(
                    exists =>
                    {
                        lock (gate)
                        {
                            if (exists != true)
                            {
                                read.Disposable = System.Reactive.Disposables.Disposable.Empty;
                                reading = false;
                                observer.OnNext(absent);
                                return;
                            }
                            // A healthy read is never restarted by an ordinary update; one that ended by deletion reattaches here.
                            if (reading)
                                return;
                            reading = true;
                            read.Disposable = hub.GetMeshNodeStream(path).Subscribe(
                                node => { lock (gate) { observer.OnNext(project(node)); } },
                                error =>
                                {
                                    lock (gate)
                                    {
                                        reading = false;
                                        if (!IsGone(error, path))
                                            observer.OnError(error);
                                        else
                                        {
                                            observer.OnNext(absent);
                                            CompleteIfDone();
                                        }
                                    }
                                },
                                () => { lock (gate) { reading = false; CompleteIfDone(); } });
                        }
                    },
                    error => { lock (gate) { observer.OnError(error); } },
                    () => { lock (gate) { presenceEnded = true; CompleteIfDone(); } });
            return new System.Reactive.Disposables.CompositeDisposable(watch, read);
        });

    /// <summary>
    /// Whether <paramref name="error"/> says the node at <paramref name="path"/> is gone: its read ended by a
    /// deletion, or the router answered NotFound for exactly this path. Anything else is a fault. Pure.
    /// </summary>
    /// <param name="error">What ended the read.</param>
    /// <param name="path">The node's path.</param>
    internal static bool IsGone(Exception error, string path)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is MeshNodeReadEndedWithDeleteException deleted
                && string.Equals(deleted.NodePath, path, StringComparison.Ordinal))
                return true;
            if (current is DeliveryFailureException { Failure.ErrorType: ErrorType.NotFound } failure
                && failure.Message.StartsWith($"No node found at '{path}'", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// <paramref name="row"/> as its target's lifecycle reads: the target's outcome as the row's state (a request still
    /// waiting stays <see cref="InboxState.Awaiting"/>; one retired as stale reads <see cref="InboxState.Stale"/>; a refused, failed,
    /// cancelled, expired, approved or rejected one reads its outcome) and the detailed <see cref="InboxRow.Status"/>
    /// text. Pure.
    /// </summary>
    /// <param name="row">The request's row.</param>
    /// <param name="reading">What its target says.</param>
    /// <param name="texts">Every state's label, with its mark (<see cref="InboxLayoutArea"/> resolves them once on the render turn).</param>
    internal static InboxRow WithOutcome(InboxRow row, TargetReading reading, ImmutableDictionary<string, string> texts)
    {
        var moved = InboxLayoutArea.WithStateText(row with { State = reading.State }, texts);
        return moved with { Status = StatusText(reading, texts) };
    }

    /// <summary>
    /// The detailed status of a target: its outcome word, and what retired it — <c>Refused · stale</c>, <c>Done</c>,
    /// <c>Awaiting decision</c>. Pure.
    /// </summary>
    /// <param name="reading">What the target says.</param>
    /// <param name="texts">Every state's label, with its mark.</param>
    internal static string StatusText(TargetReading reading, ImmutableDictionary<string, string> texts)
    {
        var word = InboxLayoutArea.LabelOf(texts, reading.Word);
        return reading.State == InboxState.Stale && reading.Word != InboxState.Stale
            ? $"{word} · {InboxLayoutArea.LabelOf(texts, InboxState.Stale).ToLowerInvariant()}"
            : word;
    }

    /// <summary>
    /// <paramref name="rows"/> with each approval request carrying its target's outcome (<see cref="WithOutcome"/>), LIVE
    /// (<see cref="Reading"/>): a request whose target still awaits is open, one whose target ended is a closed row — listed only
    /// under the Done state — and one whose target is gone has nothing left to decide and is dropped. Each emission is the
    /// answer for the rows it was asked about, and the caller SWITCHES to the latest — an obsolete batch never replaces a
    /// newer row set. Rows that announce nothing pass.
    /// </summary>
    /// <param name="hub">The hub the inbox renders on.</param>
    /// <param name="viewer">The viewer's identity.</param>
    /// <param name="rows">One leg's rows.</param>
    /// <param name="owner">The inbox's owner — whose identity the listings run as.</param>
    /// <param name="options">The hub's serializer options.</param>
    /// <param name="texts">Every state's label, with its mark.</param>
    internal static IObservable<ImmutableList<InboxRow>> WithOutcomes(
        IMessageHub hub, AccessContext? viewer, ImmutableList<InboxRow> rows, string owner, JsonSerializerOptions? options,
        ImmutableDictionary<string, string> texts)
    {
        var targets = rows.Select(r => r.Target).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToImmutableArray();
        if (targets.IsEmpty)
            return Observable.Return(rows);
        return targets
            .Select(target => Reading(hub, viewer, target, owner, options).Select(reading => (Target: target, Reading: reading)))
            .CombineLatest()
            .Select(read =>
            {
                var readings = read.ToDictionary(r => r.Target, r => r.Reading, StringComparer.Ordinal);
                return rows
                    .Where(r => r.Target.Length == 0 || !readings.GetValueOrDefault(r.Target, TargetReading.Awaiting).IsGone)
                    .Select(r => r.Target.Length == 0 ? r : WithOutcome(r, readings.GetValueOrDefault(r.Target, TargetReading.Awaiting), texts))
                    .ToImmutableList();
            });
    }
}

/// <summary>What a request's target says about it (<see cref="InboxApprovalTargets.Read"/>).</summary>
/// <param name="Open">Whether the target still awaits a decision.</param>
/// <param name="State">The state the request's row shows (an <see cref="InboxState"/> constant).</param>
/// <param name="Word">The target's own outcome word (an <see cref="InboxState"/> constant) — what the detailed status says.</param>
internal sealed record TargetReading(bool Open, string State, string Word)
{
    /// <summary>The target still waits for a decision.</summary>
    internal static readonly TargetReading Awaiting = new(true, InboxState.Awaiting, InboxState.Awaiting);

    /// <summary>The target does not exist: nothing is left to decide.</summary>
    internal static readonly TargetReading Gone = new(false, string.Empty, string.Empty);

    /// <summary>Whether the target does not exist.</summary>
    internal bool IsGone => State.Length == 0;
}
