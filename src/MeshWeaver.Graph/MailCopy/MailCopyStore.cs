using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.MailCopy;

/// <summary>
/// Where the opt-in mail copies live (<see cref="MailCopyNodeTypes"/>). Every read is cold and emits
/// once. Copies are written create-or-replace at a path derived from (generation, message id), so a
/// sync that runs twice — or on two replicas at once — overwrites rather than duplicates.
///
/// <para>🚨 <b>The source is never written blind.</b> It is CREATED once, when the person switches the
/// copy on (<see cref="Create"/>), and afterwards only ever changed through <see cref="Commit"/>, which
/// runs at the owning hub against the node as it is THERE and writes nothing when the node is gone or
/// carries another generation. So no sync, on any replica, can switch a copy back on after the person
/// switched it off — a create-or-replace of the source would have recreated it.</para>
/// </summary>
public interface IMailCopyStore
{
    /// <summary><paramref name="owner"/>'s copy settings for <paramref name="provider"/>, or null while the copy is off.</summary>
    IObservable<MailCopySource?> Source(string owner, string provider);

    /// <summary>
    /// Switches the copy ON with <paramref name="source"/> — create-only: when a source exists already
    /// it is left as it is and emitted instead.
    /// </summary>
    IObservable<MailCopySource> Create(string owner, MailCopySource source);

    /// <summary>
    /// Changes the source ATOMICALLY at its owning hub: <paramref name="change"/> is applied to the
    /// source as it is there, and only when it still carries <paramref name="generation"/>; a change
    /// that answers null refuses (a lease held by another sync). Emits the source AS COMMITTED — which
    /// may carry a new generation, when the change rotated it — or null when the source is gone, carries
    /// another generation or the change refused: then nothing was written.
    /// </summary>
    IObservable<MailCopySource?> Commit(string owner, string provider, string generation, Func<MailCopySource, MailCopySource?> change);

    /// <summary>
    /// Switches the copy OFF: deletes the source node and EVERY copy under it, of every generation, in
    /// one call. Emits true when something was removed, false when the copy was already off.
    /// </summary>
    IObservable<bool> Disable(string owner, string provider);

    /// <summary>Writes one copy (create or replace — the path is derived from its generation and message id).</summary>
    IObservable<MailMessageCopy> Upsert(string owner, MailMessageCopy copy);

    /// <summary>
    /// Writes one copy unless the copy already there is NEWER — its <see cref="MailMessageCopy.CopiedAt"/>
    /// strictly after this one's — decided at the owning hub (a create when there is none, an update applied
    /// to the copy as it is there otherwise). So a write that lost a race to a newer one (an Undo's late
    /// Graph read against a sync) never replaces it, while a copy older than the write is replaced.
    /// Emits true when it wrote.
    /// </summary>
    IObservable<bool> WriteIfNewer(string owner, MailMessageCopy copy);

    /// <summary>
    /// <see cref="WriteIfNewer"/> that also emits the copy it REPLACED, read at the owner from the very
    /// node the write replaced (null when there was none). What a tombstone's cleanup relies on: the
    /// inventory of the files it is about to orphan, atomically with the write.
    /// </summary>
    IObservable<(bool Wrote, MailMessageCopy? Replaced)> ReplaceIfNewer(string owner, MailMessageCopy copy);

    /// <summary>
    /// The stored REVISION of one message's node — the node's own version, which moves on EVERY accepted
    /// write (tombstones included, and whatever <see cref="MailMessageCopy.CopiedAt"/> the write carried;
    /// that stays the ACTION order only) — or null when there is none. What a reconcile reads before it
    /// asks Graph.
    /// </summary>
    IObservable<long?> Revision(string owner, string provider, string generation, string messageId);

    /// <summary>
    /// Writes <paramref name="copy"/> only while the stored revision is still <paramref name="expected"/>
    /// (null: still none) — a compare-and-set at the owning hub. Emits false on a conflict: something
    /// wrote the message since the revision was read, and nothing was written.
    /// </summary>
    IObservable<bool> WriteIfUnchanged(string owner, MailMessageCopy copy, long? expected);

    /// <summary>
    /// Records the attachments' decision on a copy — applied at the owning hub to the node as it is THERE,
    /// and only while it is still a live copy whose attachments are undecided; nothing else of the copy is
    /// touched, so it can neither resurrect a tombstone nor replace newer fields. Emits whether it applied.
    /// </summary>
    IObservable<bool> SetAttachments(string owner, string provider, string generation, string messageId,
        ImmutableList<MailAttachmentCopy> attachments);

    /// <summary>Removes one copy of <paramref name="generation"/>; emits false when there was none.</summary>
    IObservable<bool> Remove(string owner, string provider, string generation, string messageId);

    /// <summary>
    /// Removes every copy of <paramref name="generation"/> — an earlier generation's leftovers — and
    /// nothing else. Emits how many were removed.
    /// </summary>
    IObservable<int> RemoveGeneration(string owner, string provider, string generation);

    /// <summary>
    /// Up to <paramref name="max"/> copies of any generation but <paramref name="exceptGeneration"/> (of
    /// every one when null) — what the sweep for leftovers reads before it decides, against the source as
    /// it is AFTER this read, which of them are obsolete.
    /// </summary>
    IObservable<ImmutableList<MailMessageCopy>> List(string owner, string provider, int max, string? exceptGeneration = null);

    /// <summary>One copy of <paramref name="generation"/>, or null.</summary>
    IObservable<MailMessageCopy?> Read(string owner, string provider, string generation, string messageId);

    /// <summary>
    /// One page of <paramref name="generation"/>'s copies, newest first (or oldest first), at most
    /// <paramref name="size"/>: those whose <see cref="MailMessageCopy.OrderKey"/> is at or below (at or
    /// above) <paramref name="from"/>, or from the newest (oldest) when null. Ordered by the store, so the
    /// bound keeps the right end; the caller pages on by the last copy's order key.
    /// </summary>
    IObservable<ImmutableList<MailMessageCopy>> ReadPage(
        string owner, string provider, string generation, long? from, int size, bool oldestFirst = false);

    /// <summary>
    /// The keys (<c>outlook</c>, <c>outlook.{accountId}</c>, <c>gmail</c>) of the copies <paramref name="owner"/> has
    /// ON, whether or not their account is still connected — so a copy whose account is gone can still be listed
    /// and deleted. A store that cannot list answers none.
    /// </summary>
    IObservable<ImmutableList<string>> SourceKeys(string owner) => Observable.Return(ImmutableList<string>.Empty);

    /// <summary>
    /// Every copy that is ON on this installation, across owners — what the keeper resumes after a restart.
    /// Platform scope: refused to in-mesh code. A store that cannot list answers none.
    /// </summary>
    IObservable<ImmutableList<MailCopySourceRef>> EnabledSources() => Observable.Return(ImmutableList<MailCopySourceRef>.Empty);
}

/// <summary>One copy that is on: whose it is and its key.</summary>
/// <param name="Owner">The user id that owns the copy.</param>
/// <param name="Key">The copy's key (<see cref="MailCopySource.Provider"/>).</param>
public sealed record MailCopySourceRef(string Owner, string Key);

/// <summary>
/// The copies as mesh nodes in the OWNER's partition.
///
/// <para><b>Reads run as the owner</b> — the query names them as its viewer, so nobody else's copy can
/// answer. <b>Writes run as System on the owner's behalf</b>, scoped to the owner's own
/// <c>_MailCopy</c> namespace: the sync runs in the background, after the request that started it has
/// gone, so there is no viewer context left to write with. Both are the triage store's arrangement
/// (the triage verdict store of the Mail module).</para>
/// </summary>
/// <param name="hub">The portal hub, resolved lazily (DI builds this before the portal exists).</param>
/// <param name="logger">Where a failed read is reported.</param>
/// <param name="attachments">
/// Where the copies' attachment files live. 🚨 Nothing deletes a node's content-collection files when the
/// node is deleted, so every removal of a copy here deletes its files (and their index) FIRST; with none
/// the copies hold no attachments.
/// </param>
public sealed class MeshMailCopyStore(Func<IMessageHub?> hub, ILogger? logger = null, IMailAttachmentStore? attachments = null) : IMailCopyStore
{
    /// <summary>The most copies one page returns.</summary>
    public const int MaxPage = 500;

    /// <summary>The most copies one <see cref="RemoveGeneration"/> pass deletes before it reads again.</summary>
    public const int RemovePass = 200;

    /// <summary>The most passes one <see cref="RemoveGeneration"/> makes — 100,000 copies.</summary>
    public const int MaxRemovePasses = 500;

    /// <summary>How long a <see cref="Commit"/> waits for the owning hub; an absent node never answers.</summary>
    public static readonly TimeSpan CommitBudget = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<MailCopySource?> Source(string owner, string provider)
        => Gated(Assembly.GetCallingAssembly(), nameof(Source), owner, Permission.Read, () => SourceCore(owner, provider));

    private IObservable<MailCopySource?> SourceCore(string owner, string provider)
        => Mesh().SelectMany(m => m.Mesh
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{MailCopyNodeTypes.SourcePath(owner, provider)}", owner) with { Limit = 1 })
            .Take(1)
            .Select(change => change.Items
                .Where(node => string.Equals(node.NodeType, MailCopyNodeTypes.SourceNodeType, StringComparison.Ordinal))
                .Select(node => node.ContentAs<MailCopySource>(m.Options, logger))
                .FirstOrDefault(source => source is not null)));

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<MailCopySource> Create(string owner, MailCopySource source)
        => Gated(Assembly.GetCallingAssembly(), nameof(Create), owner, Permission.Update, () => Write(owner, m => m.Mesh.CreateNode(MailCopyNodeTypes.BuildSource(owner, source)))
            .Select(_ => source)
            // Already there — switched on by another click or another replica: keep the first.
            .Catch((Exception ex) => MailCopyNodeTypes.IsAlreadyExists(ex)
                ? SourceCore(owner, source.Provider).Select(existing => existing ?? throw ex)
                : Observable.Throw<MailCopySource>(ex)));

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<MailCopySource?> Commit(string owner, string provider, string generation, Func<MailCopySource, MailCopySource?> change)
        => Gated(Assembly.GetCallingAssembly(), nameof(Commit), owner, Permission.Update, () => CommitCore(owner, provider, generation, change));

    private IObservable<MailCopySource?> CommitCore(string owner, string provider, string generation, Func<MailCopySource, MailCopySource?> change)
        // The existence read first: an absent node's stream never answers, and the bound below would
        // then spend its whole budget to say what this read says at once.
        => SourceCore(owner, provider).SelectMany(current => current is null || current.Generation != generation
            ? Observable.Return<MailCopySource?>(null)
            : Mesh().SelectMany(m =>
            {
                // What the owner's (last) application of the change decided — the lambda may run again
                // on a write conflict, so the last decision is the one that landed.
                MailCopySource? applied = null;
                return m.Access
                    .RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => m.Hub
                        .GetMeshNodeStream(MailCopyNodeTypes.SourcePath(owner, provider))
                        // Applied at the owner to the node as it is THERE: a source switched off or
                        // rebuilt since the read above, or a lease taken by another sync, is left as it is.
                        .Update(node =>
                        {
                            var (next, decided) = Committed(node, generation, change, m.Options, logger);
                            applied = decided;
                            return next;
                        }))
                    .Take(1)
                    .Timeout(CommitBudget)
                    .Select(_ => applied);
            }));

    /// <summary>
    /// What a commit makes of the source <paramref name="node"/> as it is at the owning hub:
    /// <paramref name="change"/> applied when it still carries <paramref name="generation"/> and the
    /// change does not refuse, else the node exactly as it is — a no-op, so a commit that lost a race
    /// with a switch-off, a rebuild or another sync's lease writes nothing. Also answers the source as
    /// committed, or null for "nothing was written". Pure.
    /// </summary>
    public static (MeshNode Node, MailCopySource? Applied) Committed(MeshNode node, string generation,
        Func<MailCopySource, MailCopySource?> change, System.Text.Json.JsonSerializerOptions options, ILogger? logger = null)
        => node.ContentAs<MailCopySource>(options, logger) is { } there && there.Generation == generation && change(there) is { } next
            ? (node with { Content = next }, next)
            : (node, null);

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<bool> Disable(string owner, string provider)
        // The copies' attachment files go first, one pass at a time (each pass deletes what it read, files
        // before node), so "stop and delete the copies" leaves no file and no index behind; the source then
        // takes whatever is left with it.
        => Gated(Assembly.GetCallingAssembly(), nameof(Disable), owner, Permission.Delete, () =>
            (attachments is null ? Observable.Return(0) : Observable.Defer(() => RotatedThenCleaned(owner, provider)))
            .SelectMany(_ => Write(owner, m => m.Mesh.DeleteNode(MailCopyNodeTypes.SourcePath(owner, provider)))));

    // 🚨 The source's generation is rotated FIRST: a sync still running holds the old one, so its commits
    // are refused from now on (and it removes what it wrote by that generation), instead of racing the
    // scan below with copies and files created after the last empty pass.
    private IObservable<int> RotatedThenCleaned(string owner, string provider)
        => SourceCore(owner, provider)
            .SelectMany(current => current is null
                ? Observable.Return<MailCopySource?>(null)
                : CommitCore(owner, provider, current.Generation, RotatedForSwitchOff))
            .SelectMany(_ => DisablePasses(owner, provider, 1, 0));

    /// <summary>
    /// The source as a switch-off rotates it before cleaning: a new generation, no lease — and, 🚨 like a
    /// rebuild, no delta cursor and not backfilled. A switch-off that then FAILS leaves the source alive on
    /// that new, empty generation; were the old cursor and <c>Backfilled</c> kept, the copy would be served
    /// as complete while empty and the next sync would resume the old delta instead of filling it again. Pure.
    /// </summary>
    public static MailCopySource RotatedForSwitchOff(MailCopySource source) => source with
    {
        Generation = MailCopyNodeTypes.NewGeneration(),
        // Only a copy that keeps attachments is rotated, and its fresh cursor asks for the current fields.
        Projection = MailCopySource.CurrentProjection,
        LeaseHolder = null,
        LeaseUntil = null,
        Cursor = null,
        Backfilled = false,
        // A rebuild in flight is abandoned: its copies go with the switch-off's own pass.
        PendingGeneration = null,
        PendingCursor = null,
    };

    private IObservable<int> DisablePasses(string owner, string provider, int pass, int removed)
        => Mesh().SelectMany(m => m.Mesh
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{MailCopyNodeTypes.SourcePath(owner, provider)} nodeType:{MailCopyNodeTypes.MessageNodeType} limit:{RemovePass}", owner) with { Limit = RemovePass })
            .Take(1)
            .Select(change => change.Items
                .Where(node => string.Equals(node.NodeType, MailCopyNodeTypes.MessageNodeType, StringComparison.Ordinal)
                               && string.Equals(node.Namespace, MailCopyNodeTypes.SourcePath(owner, provider), StringComparison.Ordinal))
                .ToImmutableList())
            .SelectMany(nodes => nodes.Count == 0
                ? Observable.Return(removed)
                : nodes.Select(node => DeleteCopy(owner, node, m.Options)).MergeBounded(4).Count()
                    // 🚨 At the pass limit with copies left the switch-off FAILS (the source stays, so it can
                    // be repeated) — it never reports success over copies whose files were not cleaned.
                    .SelectMany(count => pass < MaxRemovePasses
                        ? DisablePasses(owner, provider, pass + 1, removed + count)
                        : Observable.Throw<int>(new InvalidOperationException(
                            "The copies were not all removed within the pass limit; the source is kept so the switch-off can be repeated.")))));

    /// <summary>
    /// One copy's attachment files and index, then its node. 🚨 The files go first and a failure to delete
    /// them stops here: the node stays, and with it the way to try again. 🚨 Whether the copy may hold files
    /// is read from the node AS IT IS (its stream), never from the query that found it: the index trails
    /// the store, and an older indexed copy can lack the marker a newer write set.
    /// </summary>
    private IObservable<bool> DeleteCopy(string owner, MeshNode node, System.Text.Json.JsonSerializerOptions options)
        => (attachments is null
                ? Observable.Return(System.Reactive.Unit.Default)
                : Live(owner, node.Path).SelectMany(live => MailAttachments.MayHoldFiles(live.ContentAs<MailMessageCopy>(options, logger))
                    // The caller was authorized where the removal was asked for; this inner call runs after
                    // hops that keep no ambient identity, so it declares System itself.
                    ? Mesh().SelectMany(m => m.Access.RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => attachments.DeleteAll(owner, node.Path)))
                    : Observable.Return(System.Reactive.Unit.Default)))
            .SelectMany(_ => Write(owner, m => m.Mesh.DeleteNode(node.Path)));

    /// <summary>
    /// The node at <paramref name="path"/> as its stream has it — for a node a query has just shown to
    /// EXIST (CQRS: existence from the index, content from the stream; an absent node's stream never answers).
    /// </summary>
    private IObservable<MeshNode> Live(string owner, string path)
        => Mesh().SelectMany(m => m.Access
            .RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => m.Hub.GetMeshNodeStream(path).Take(1))
            .Take(1)
            .Timeout(CommitBudget));

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<MailMessageCopy> Upsert(string owner, MailMessageCopy copy)
        => Gated(Assembly.GetCallingAssembly(), nameof(Upsert), owner, Permission.Update, () =>
            Write(owner, m => m.Mesh.CreateOrUpdateNode(MailCopyNodeTypes.BuildCopy(owner, copy))).Select(_ => copy));

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<bool> WriteIfNewer(string owner, MailMessageCopy copy)
        => Gated(Assembly.GetCallingAssembly(), nameof(WriteIfNewer), owner, Permission.Update, () =>
            ReplaceIfNewerCore(owner, copy).Select(result => result.Wrote));

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<(bool Wrote, MailMessageCopy? Replaced)> ReplaceIfNewer(string owner, MailMessageCopy copy)
        => Gated(Assembly.GetCallingAssembly(), nameof(ReplaceIfNewer), owner, Permission.Update, () => ReplaceIfNewerCore(owner, copy));

    private IObservable<(bool Wrote, MailMessageCopy? Replaced)> ReplaceIfNewerCore(string owner, MailMessageCopy copy)
        => Write(owner, m => m.Mesh.CreateNode(MailCopyNodeTypes.BuildCopy(owner, copy)))
            .Select(_ => (Wrote: true, Replaced: (MailMessageCopy?)null))
            .Catch((Exception ex) => MailCopyNodeTypes.IsAlreadyExists(ex)
                ? Mesh().SelectMany(m =>
                {
                    var result = (Wrote: false, Replaced: (MailMessageCopy?)null);
                    return m.Access
                        .RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => m.Hub
                            .GetMeshNodeStream(MailCopyNodeTypes.CopyPath(owner, copy.Provider, copy.Generation, copy.MessageId))
                            // Applied at the owner to the copy as it is THERE — and what it replaced is
                            // captured there too, from the same node.
                            .Update(node =>
                            {
                                var (next, wrote) = Newer(node, copy, m.Options, logger);
                                result = (wrote, wrote ? node.ContentAs<MailMessageCopy>(m.Options, logger) : null);
                                return next;
                            }))
                        .Take(1)
                        .Timeout(CommitBudget)
                        .Select(_ => result);
                })
                : Observable.Throw<(bool, MailMessageCopy?)>(ex));

    /// <summary>
    /// What <see cref="WriteIfNewer"/> makes of the copy <paramref name="node"/> as it is at the owner:
    /// replaced by <paramref name="copy"/> when it is older, else left exactly as it is. Pure.
    /// </summary>
    public static (MeshNode Node, bool Wrote) Newer(MeshNode node, MailMessageCopy copy,
        System.Text.Json.JsonSerializerOptions options, ILogger? logger = null)
        // Refused only by a STRICTLY newer copy: at the same instant the later write wins (a Delete and
        // the Undo asked right after it, a sync's removal of what it wrote a moment before).
        => node.ContentAs<MailMessageCopy>(options, logger) is { } there && there.CopiedAt > copy.CopiedAt
            ? (node, false)
            : (node with { Content = copy, Name = MailCopyNodeTypes.BuildCopy("", copy).Name }, true);

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<long?> Revision(string owner, string provider, string generation, string messageId)
        // Existence from the index, the version from the node's stream: a revision read off the index can
        // be older than the node, and it is the precondition of the write that follows.
        => Gated(Assembly.GetCallingAssembly(), nameof(Revision), owner, Permission.Read, () =>
            Indexed(owner, MailCopyNodeTypes.CopyPath(owner, provider, generation, messageId))
                .SelectMany(found => found is null
                    ? Observable.Return<long?>(null)
                    : Live(owner, found.Path).Select(live => string.Equals(live.NodeType, MailCopyNodeTypes.MessageNodeType, StringComparison.Ordinal)
                        ? (long?)live.Version
                        : null)));

    // A copy the index lists at exactly this path, or null — an EXISTENCE answer only (the index trails
    // the store, so its content is never what a decision reads, and its miss is no proof of absence).
    private IObservable<MeshNode?> Indexed(string owner, string path)
        => Mesh().SelectMany(m => m.Mesh
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}", owner) with { Limit = 1 })
            .Take(1)
            .Select(change => change.Items.FirstOrDefault(node =>
                string.Equals(node.NodeType, MailCopyNodeTypes.MessageNodeType, StringComparison.Ordinal))));

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<bool> WriteIfUnchanged(string owner, MailMessageCopy copy, long? expected)
        => Gated(Assembly.GetCallingAssembly(), nameof(WriteIfUnchanged), owner, Permission.Update, () => expected is null
            // "Still none": create-only — a node that appeared meanwhile is the conflict.
            ? Write(owner, m => m.Mesh.CreateNode(MailCopyNodeTypes.BuildCopy(owner, copy)))
                .Select(_ => true)
                .Catch((Exception ex) => MailCopyNodeTypes.IsAlreadyExists(ex) ? Observable.Return(false) : Observable.Throw<bool>(ex))
            : Mesh().SelectMany(m =>
            {
                var applied = false;
                return m.Access
                    .RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => m.Hub
                        .GetMeshNodeStream(MailCopyNodeTypes.CopyPath(owner, copy.Provider, copy.Generation, copy.MessageId))
                        .Update(node =>
                        {
                            var (next, wrote) = Unchanged(node, copy, expected.Value, m.Options, logger);
                            applied = wrote;
                            return next;
                        }))
                    .Take(1)
                    .Timeout(CommitBudget)
                    .Select(_ => applied);
            }));

    /// <summary>
    /// What <see cref="WriteIfUnchanged"/> makes of the stored node: <paramref name="copy"/> when its
    /// version is still <paramref name="expected"/>, else the node exactly as it is. Pure.
    /// </summary>
    public static (MeshNode Node, bool Wrote) Unchanged(MeshNode node, MailMessageCopy copy, long expected,
        System.Text.Json.JsonSerializerOptions options, ILogger? logger = null)
        => node.Version == expected && node.ContentAs<MailMessageCopy>(options, logger) is not null
            ? (node with { Content = copy, Name = MailCopyNodeTypes.BuildCopy("", copy).Name }, true)
            : (node, false);

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<bool> SetAttachments(string owner, string provider, string generation, string messageId,
        ImmutableList<MailAttachmentCopy> list)
        => Gated(Assembly.GetCallingAssembly(), nameof(SetAttachments), owner, Permission.Update, () => Mesh().SelectMany(m =>
        {
            var applied = false;
            return m.Access
                .RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => m.Hub
                    .GetMeshNodeStream(MailCopyNodeTypes.CopyPath(owner, provider, generation, messageId))
                    .Update(node =>
                    {
                        var (next, ok) = Attached(node, list, m.Options, logger);
                        applied = ok;
                        return next;
                    }))
                .Take(1)
                .Timeout(CommitBudget)
                .Select(_ => applied);
        }));

    /// <summary>
    /// What <see cref="SetAttachments"/> makes of the copy <paramref name="node"/> as it is at the owner:
    /// the list recorded when it is a live copy with undecided attachments, else exactly as it is. Pure.
    /// </summary>
    public static (MeshNode Node, bool Applied) Attached(MeshNode node, ImmutableList<MailAttachmentCopy> list,
        System.Text.Json.JsonSerializerOptions options, ILogger? logger = null)
        => node.ContentAs<MailMessageCopy>(options, logger) is { Removed: false, Attachments: null } there
            ? (node with { Content = there with { Attachments = list } }, true)
            : (node, false);

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<bool> Remove(string owner, string provider, string generation, string messageId)
        => Gated(Assembly.GetCallingAssembly(), nameof(Remove), owner, Permission.Delete, () => attachments is null
            ? Write(owner, m => m.Mesh.DeleteNode(MailCopyNodeTypes.CopyPath(owner, provider, generation, messageId)))
            // 🚨 A node the index does not list yet is NOT deleted blind: that delete would skip its files,
            // and the node is the only handle on them. It is left for the next sweep, whose query will
            // list it, and this answers "none" — the sweeps are the only callers, and they re-read.
            : Mesh().SelectMany(m => Indexed(owner, MailCopyNodeTypes.CopyPath(owner, provider, generation, messageId))
                    .SelectMany(node => node is null
                        ? Observable.Return(false)
                        : DeleteCopy(owner, node, m.Options))));

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<int> RemoveGeneration(string owner, string provider, string generation)
        => Gated(Assembly.GetCallingAssembly(), nameof(RemoveGeneration), owner, Permission.Delete,
            () => RemovePasses(owner, provider, generation, 1, 0));

    /// <summary>
    /// Whether <paramref name="node"/> is one of <paramref name="owner"/>'s copies of
    /// <paramref name="provider"/> in <paramref name="generation"/>: directly under the source path, of the
    /// copy node type, carrying that generation. What a delete is allowed to touch. Pure.
    /// </summary>
    public static bool IsCopyOf(MeshNode node, string owner, string provider, string generation,
        System.Text.Json.JsonSerializerOptions options)
        => string.Equals(node.Namespace, MailCopyNodeTypes.SourcePath(owner, provider), StringComparison.Ordinal)
           && string.Equals(node.NodeType, MailCopyNodeTypes.MessageNodeType, StringComparison.Ordinal)
           && node.ContentAs<MailMessageCopy>(options) is { } copy
           && string.Equals(copy.Generation, generation, StringComparison.Ordinal);

    /// <summary>A generation spliced into a query — refused unless well-formed (<see cref="MailCopyNodeTypes.IsValidGeneration"/>).</summary>
    private static string Checked(string generation)
        => MailCopyNodeTypes.IsValidGeneration(generation)
            ? generation
            : throw new ArgumentException("The copy's generation is not a well-formed id; it is never used in a query.", nameof(generation));

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<ImmutableList<MailMessageCopy>> List(string owner, string provider, int max, string? exceptGeneration = null)
    {
        var bound = Math.Clamp(max, 1, MaxPage);
        return Gated(Assembly.GetCallingAssembly(), nameof(List), owner, Permission.Read, () => Mesh().SelectMany(m => m.Mesh
            .Query<MeshNode>(MeshQueryRequest.FromQuery(ListQuery(owner, provider, bound, exceptGeneration), owner) with { Limit = bound })
            .Take(1)
            .Select(change => change.Items
                .Where(node => string.Equals(node.NodeType, MailCopyNodeTypes.MessageNodeType, StringComparison.Ordinal))
                .Select(node => node.ContentAs<MailMessageCopy>(m.Options, logger))
                .OfType<MailMessageCopy>()
                .Where(copy => string.Equals(copy.Provider, provider, StringComparison.Ordinal) && copy.MessageId.Length > 0)
                .ToImmutableList())));
    }

    private IObservable<int> RemovePasses(string owner, string provider, string generation, int pass, int removed)
        => Mesh().SelectMany(m => m.Mesh
            .Query<MeshNode>(MeshQueryRequest.FromQuery(GenerationQuery(owner, provider, generation), owner) with { Limit = RemovePass })
            .Take(1)
            // 🚨 Every node is checked again before the privileged delete — inside this copy's own
            // namespace, a copy, of exactly this generation — whatever the query answered.
            .Select(change => change.Items
                .Where(node => IsCopyOf(node, owner, provider, generation, m.Options))
                .ToImmutableList())
            .SelectMany(nodes => nodes.Count == 0
                ? Observable.Return(removed)
                : nodes.Select(node => DeleteCopy(owner, node, m.Options)).MergeBounded(4).Count()
                    .SelectMany(count => pass < MaxRemovePasses
                        ? RemovePasses(owner, provider, generation, pass + 1, removed + count)
                        : Observable.Return(removed + count))));

    /// <summary>The query <see cref="List"/> runs. Pure.</summary>
    public static string ListQuery(string owner, string provider, int bound, string? exceptGeneration)
        => $"namespace:{MailCopyNodeTypes.SourcePath(owner, provider)} nodeType:{MailCopyNodeTypes.MessageNodeType}"
           + (exceptGeneration is { Length: > 0 } current ? $" -content.generation:{Checked(current)}" : "")
           + $" limit:{bound}";

    /// <summary>The query that finds a generation's copies. Pure.</summary>
    public static string GenerationQuery(string owner, string provider, string generation)
        => $"namespace:{MailCopyNodeTypes.SourcePath(owner, provider)} nodeType:{MailCopyNodeTypes.MessageNodeType} content.generation:{Checked(generation)}";

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<MailMessageCopy?> Read(string owner, string provider, string generation, string messageId)
        // Existence from the index, the content from the node's stream — what a caller decides a write
        // on must be the copy as it is, not as the index last saw it. An index miss answers null, which is
        // NOT proof of absence: a caller that cleans up on it must treat it as "unknown".
        => Gated(Assembly.GetCallingAssembly(), nameof(Read), owner, Permission.Read, () => Mesh().SelectMany(m =>
            Indexed(owner, MailCopyNodeTypes.CopyPath(owner, provider, generation, messageId))
                .SelectMany(found => found is null
                    ? Observable.Return<MailMessageCopy?>(null)
                    : Live(owner, found.Path).Select(live => Copies([live], provider, generation, m.Options).FirstOrDefault())
                        // Deleted between the index answer and the stream (a retired generation, a removal): gone, not a failure.
                        .Catch((Exception ex) => IsGone(ex, found.Path)
                            ? Observable.Return<MailMessageCopy?>(null)
                            : Observable.Throw<MailMessageCopy?>(ex)))));

    /// <summary>
    /// Whether <paramref name="error"/> establishes that the node at <paramref name="path"/> is ABSENT: the stream cache
    /// ended the read with that path's delete, or the router stamped the failure <c>NodeAbsent</c>. Any other delivery
    /// failure proves nothing about the node and is never read as "gone". Pure.
    /// </summary>
    public static bool IsGone(Exception error, string path)
    {
        for (var current = error; current is not null; current = current.InnerException)
            if (current is MeshNodeReadEndedWithDeleteException deleted && string.Equals(deleted.NodePath, path, StringComparison.Ordinal)
                || current is DeliveryFailureException { Failure.NodeAbsent: true })
                return true;
        return false;
    }

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<ImmutableList<MailMessageCopy>> ReadPage(
        string owner, string provider, string generation, long? from, int size, bool oldestFirst = false)
    {
        var bound = Math.Clamp(size, 1, MaxPage);
        return Gated(Assembly.GetCallingAssembly(), nameof(ReadPage), owner, Permission.Read, () => Mesh().SelectMany(m => m.Mesh
            .Query<MeshNode>(MeshQueryRequest.FromQuery(PageQuery(owner, provider, generation, from, bound, oldestFirst), owner) with { Limit = bound })
            .Take(1)
            .Select(change =>
            {
                var copies = Copies(change.Items, provider, generation, m.Options);
                // Ordered again here: the store's order decides WHICH copies the bound keeps, this one
                // the order they are answered in, whatever a store did with ties.
                var ordered = oldestFirst
                    ? copies.OrderBy(copy => copy.OrderKey).ThenBy(copy => copy.MessageId, StringComparer.Ordinal)
                    : copies.OrderByDescending(copy => copy.OrderKey).ThenByDescending(copy => copy.MessageId, StringComparer.Ordinal);
                return ordered.ToImmutableList();
            })));
    }

    /// <summary>
    /// The query <see cref="ReadPage"/> runs: the generation's copies, ordered by their order key, from
    /// <paramref name="from"/> on. Inclusive — the key is unique to the millisecond and a hash of the
    /// id, so a shared key is all but impossible, and the caller drops what it has already seen should
    /// one occur. Pure.
    /// </summary>
    public static string PageQuery(string owner, string provider, string generation, long? from, int bound, bool oldestFirst)
        => GenerationQuery(owner, provider, generation)
           + (from is { } edge ? $" content.orderKey:{(oldestFirst ? ">=" : "<=")}{edge}" : "")
           // Tombstones are left out BEFORE the limit, so a page of them never reads as the end.
           + " -content.removed:true"
           + $" sort:content.orderKey{(oldestFirst ? "" : "-desc")} limit:{bound}";

    private IEnumerable<MailMessageCopy> Copies(IEnumerable<MeshNode> nodes, string provider, string generation, System.Text.Json.JsonSerializerOptions options)
        => nodes
            .Where(node => string.Equals(node.NodeType, MailCopyNodeTypes.MessageNodeType, StringComparison.Ordinal))
            // Shape-tolerant: typed on the owning hub, a JsonElement across a hub boundary.
            .Select(node => node.ContentAs<MailMessageCopy>(options, logger))
            .OfType<MailMessageCopy>()
            .Where(copy => string.Equals(copy.Provider, provider, StringComparison.Ordinal)
                           && string.Equals(copy.Generation, generation, StringComparison.Ordinal)
                           && copy.MessageId.Length > 0
                           // A tombstone is never listed nor opened.
                           && !copy.Removed);

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<ImmutableList<string>> SourceKeys(string owner)
        => Gated(Assembly.GetCallingAssembly(), nameof(SourceKeys), owner, Permission.Read, () => Mesh().SelectMany(m => m.Mesh
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{MailCopyNodeTypes.NamespaceFor(owner)} nodeType:{MailCopyNodeTypes.SourceNodeType}", owner)
                // An ENUMERATION: a key clipped off this list is a copy the owner can no longer see or delete.
                .Complete())
            .Take(1)
            .SelectMany(change => RejectIncompleteListing(change, nameof(SourceKeys)))
            .Select(change => change.Items
                .Where(node => string.Equals(node.NodeType, MailCopyNodeTypes.SourceNodeType, StringComparison.Ordinal)
                               && string.Equals(node.Namespace, MailCopyNodeTypes.NamespaceFor(owner), StringComparison.Ordinal))
                .Select(node => node.Id)
                .ToImmutableList())));

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<ImmutableList<MailCopySourceRef>> EnabledSources()
        => MailCopyAuthority.GatedPlatform(hub(), Assembly.GetCallingAssembly(), $"{nameof(MeshMailCopyStore)}.{nameof(EnabledSources)}",
            () => Mesh().SelectMany(m => m.Access
                .RunAsSystem(() => m.Mesh
                    .Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:{MailCopyNodeTypes.SourceNodeType} partitions:all")
                        // An ENUMERATION: a source clipped off this list is a copy the keeper never resumes.
                        .Complete())
                    .Take(1)
                    .SelectMany(change => RejectIncompleteListing(change, nameof(EnabledSources)))
                    .Select(change => change.Items
                        .Where(node => string.Equals(node.NodeType, MailCopyNodeTypes.SourceNodeType, StringComparison.Ordinal)
                                       && node.Namespace is { } ns && ns.EndsWith("/" + MailCopyNodeTypes.Segment, StringComparison.Ordinal))
                        .Select(node => new MailCopySourceRef(node.Namespace![..^(MailCopyNodeTypes.Segment.Length + 1)], node.Id))
                        .Where(r => !string.IsNullOrWhiteSpace(r.Owner) && !r.Owner.Contains('/'))
                        .ToImmutableList()))));

    /// <summary>
    /// 🚨 An ENUMERATION that is only a FLOOR is a fault, never a listing: a frame whose fan-in names silent
    /// providers, or that a provider marked incomplete, may be MISSING sources — read as the whole list it would hide
    /// a disconnected account's copy from Settings, or leave an enabled copy out of restart recovery. It goes down the
    /// error channel instead. <see cref="MeshQueryRequest.Complete"/> only lifts the page bound; it says nothing about
    /// whether every provider answered (review of #7154). Pure.
    /// </summary>
    internal static IObservable<QueryResultChange<MeshNode>> RejectIncompleteListing(QueryResultChange<MeshNode> change, string listing)
    {
        if (change.SilentProviders is { Count: > 0 } silent)
            return Observable.Throw<QueryResultChange<MeshNode>>(new InvalidOperationException(
                $"The mail copy listing '{listing}' is INCOMPLETE: query provider(s) [{string.Join(", ", silent)}] did not answer."));
        if (change.SnapshotIncomplete)
            return Observable.Throw<QueryResultChange<MeshNode>>(new InvalidOperationException(
                $"The mail copy listing '{listing}' is INCOMPLETE: a provider dropped part of its read."));
        return Observable.Return(change);
    }

    private IObservable<T> Gated<T>(Assembly caller, string surface, string owner, Permission permission, Func<IObservable<T>> work)
        => MailCopyAuthority.Gated(hub(), caller, $"{nameof(MeshMailCopyStore)}.{surface}", owner, permission, work);

    private IObservable<T> Write<T>(string owner, Func<Portal, IObservable<T>> write)
        => Mesh().SelectMany(m => m.Access
            .RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => write(m))
            .Take(1));

    private IObservable<Portal> Mesh()
        => Observable.Defer(() => hub() is { } portal && portal.ServiceProvider.GetService<IMeshService>() is { } mesh
            ? Observable.Return(new Portal(portal, mesh, portal.ServiceProvider.GetService<AccessService>(), portal.JsonSerializerOptions))
            : Observable.Throw<Portal>(new InvalidOperationException("The mail copies cannot be reached: no mesh is available on this host.")));

    private sealed record Portal(IMessageHub Hub, IMeshService Mesh, AccessService? Access, System.Text.Json.JsonSerializerOptions Options);
}
