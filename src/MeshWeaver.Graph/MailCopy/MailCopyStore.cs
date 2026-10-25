using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Graph.Configuration;
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
    /// Moves a copy still filed at the LEGACY place, <c>{owner}/_MailCopy/{provider}</c> in the owner's home
    /// partition, into the owner's copy partition (<see cref="MailCopyNodeTypes.NamespaceFor"/>): the source
    /// with its generation and cursor (so nothing is downloaded again), every copy, and every attachment file,
    /// each copy's stored paths rewritten — and then removes the legacy one, files first. Emits how many copies
    /// it moved; 0 when there was nothing to move. A store without a legacy place moves nothing.
    /// </summary>
    IObservable<int> MoveLegacy(string owner, string provider) => Observable.Return(0);

    /// <summary>
    /// The source of a copy still filed at the LEGACY place (<c>{owner}/_MailCopy/{provider}</c>), or null — what the
    /// account switch reads, beside <see cref="Source"/>, to show a not-yet-moved copy as on and to let it be switched
    /// off (whose <see cref="Disable"/> moves it first). Never what the inbox serves from. A store without a legacy
    /// place answers null.
    /// </summary>
    IObservable<MailCopySource?> LegacySource(string owner, string provider) => Observable.Return<MailCopySource?>(null);

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
        // 🚨 A copy still at the legacy place is MOVED first: switched on again, it keeps its generation and cursor
        // instead of getting a fresh source the move would then have to reconcile.
        => Gated(Assembly.GetCallingAssembly(), nameof(Create), owner, Permission.Update, () => EnsurePartition(owner)
            .SelectMany(_ => MoveLegacyCore(owner, source.Provider))
            .SelectMany(_ => Write(owner, m => m.Mesh.CreateNode(MailCopyNodeTypes.BuildSource(owner, source))))
            .Select(_ => source)
            // Already there — switched on by another click or another replica: keep the first.
            // Read from its STREAM: the conflict just proved it exists, and the index can trail a move that made it.
            .Catch((Exception ex) => MailCopyNodeTypes.IsAlreadyExists(ex)
                ? LiveSource(owner, MailCopyNodeTypes.SourcePath(owner, source.Provider)).Select(existing => existing ?? throw ex)
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
            : CommitAt(owner, MailCopyNodeTypes.SourcePath(owner, provider), generation, change));

    // The generation-checked commit at the owning hub of the source node at PATH — the copy partition's, or the
    // legacy one a move fences. The caller has established that the node exists (its stream never answers otherwise).
    private IObservable<MailCopySource?> CommitAt(string owner, string path, string generation, Func<MailCopySource, MailCopySource?> change)
        => Mesh().SelectMany(m =>
            {
                // What the owner's (last) application of the change decided — the lambda may run again
                // on a write conflict, so the last decision is the one that landed.
                MailCopySource? applied = null;
                return m.Access
                    .RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => m.Hub
                        .GetMeshNodeStream(path)
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
            });

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
        // 🚨 A copy still at the legacy place is MOVED first and then switched off like any other: left behind,
        // the next sync's move would bring a copy the person switched off back to life.
        => Gated(Assembly.GetCallingAssembly(), nameof(Disable), owner, Permission.Delete, () => MoveLegacyCore(owner, provider)
            .SelectMany(_ => attachments is null ? Observable.Return(0) : Observable.Defer(() => RotatedThenCleaned(owner, provider)))
            .SelectMany(_ => Write(owner, m => m.Mesh.DeleteNode(MailCopyNodeTypes.SourcePath(owner, provider)))));

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<int> MoveLegacy(string owner, string provider)
        => Gated(Assembly.GetCallingAssembly(), nameof(MoveLegacy), owner, Permission.Update, () => MoveLegacyCore(owner, provider));

    /// <summary>How long a move holds the legacy copy fenced before another replica may take it over.</summary>
    public static readonly TimeSpan MoveLease = TimeSpan.FromHours(2);

    /// <summary>
    /// The move of one legacy copy (<see cref="MoveLegacy"/>). 🚨 It is FENCED, because replicas still running the
    /// previous image keep syncing and SERVING the legacy place during a roll, and a Disable, a switch-on or a second
    /// mover may race it. Every ATTEMPT is its own lease holder (<c>move:{guid}</c>), so two overlapping moves — in one
    /// process or two — never both pass the fence:
    /// <list type="number">
    ///   <item><description>The legacy source is read from its STREAM (the index can trail a cursor) and then FENCED: a
    ///   generation-checked commit at its owning hub takes its lease for this attempt AND marks it not backfilled. A
    ///   sync of the legacy place — of either image — stands down on the lease, and a reader of either image reads the
    ///   inbox live rather than serve a copy that is being drained (readers honor <c>Backfilled</c>, not the lease). A
    ///   lease held already refuses this attempt; the next sync repeats it.</description></item>
    ///   <item><description>The destination source is created with the legacy generation and cursor but NOT backfilled
    ///   and leased to this attempt, so nothing serves or syncs a half-moved copy. One that exists is read from its
    ///   STREAM: of the same generation it is taken over the same way, of another it is not this move's.</description></item>
    ///   <item><description>The copies are moved, from complete listing frames only; then, while this attempt still holds
    ///   its fence, the legacy source is deleted — and only when that delete DID delete it is the destination released,
    ///   with the backfill state the legacy source had before this attempt fenced it (a retry that only sees the fenced
    ///   state releases it not backfilled, and the next sync completes the backfill from the cursor).</description></item>
    ///   <item><description>When the fence is LOST — or the legacy source vanished before this attempt's delete — this
    ///   attempt tells a takeover from a switch-off: a legacy source that still exists was taken over (stop, touch
    ///   nothing); one that is gone was switched off, and only if the destination still carries THIS attempt's lease is
    ///   it rotated and cleaned, in one generation-checked commit at its owning hub.</description></item>
    ///   <item><description>However the attempt ends — an error, a timeout, the caller disposing it — it releases the
    ///   leases IT took, on the nodes it took them on (never gated on what the index shows), so the next sync can repeat
    ///   the move at once instead of waiting out <see cref="MoveLease"/>. After a success there is nothing left to
    ///   release.</description></item>
    /// </list>
    /// </summary>
    private IObservable<int> MoveLegacyCore(string owner, string provider)
        => Observable.Defer(() =>
        {
            var attempt = $"move:{Guid.NewGuid():N}";
            var legacyPath = MailCopyNodeTypes.LegacySourcePath(owner, provider);
            var sourcePath = MailCopyNodeTypes.SourcePath(owner, provider);
            // What this attempt holds, for the release: written once each as the attempt takes it.
            var held = new MoveHold();
            return LegacySourceCore(owner, provider).SelectMany(legacy => legacy is null
                    ? Observable.Return(0)
                    : CommitAt(owner, legacyPath, legacy.Generation, current => Fenced(current, attempt) is { } fenced ? fenced with { Backfilled = false } : null)
                        .SelectMany(fenced =>
                        {
                            if (fenced is null)
                                return Observable.Throw<int>(new InvalidOperationException(
                                    $"The legacy mail copy '{legacyPath}' is being synced or moved elsewhere; the next sync repeats the move."));
                            held.Legacy = fenced.Generation;
                            return EnsurePartition(owner)
                                .SelectMany(_ => Destination(owner, provider, fenced, attempt))
                                .Do(owns => { if (owns) held.Destination = fenced.Generation; })
                                .SelectMany(owns => MovePasses(owner, provider, 1, 0)
                                    .SelectMany(moved => Fence(owner, legacyPath, fenced.Generation, attempt)
                                        .SelectMany(fence => fence == FenceState.Held
                                            ? Write(owner, m => m.Mesh.DeleteNode(legacyPath))
                                                .Select(deleted => deleted is true)
                                                .Do(deleted => { if (deleted) held.Legacy = null; })
                                                // 🚨 Deleted by someone else between the fence check and this delete: an
                                                // old-image switch-off — never release the destination over it.
                                                .SelectMany(deleted => deleted ? Finished(owner, sourcePath, fenced.Generation, legacy.Backfilled, attempt, owns, held, moved)
                                                    : Cancelled(owner, provider, fenced.Generation, attempt, owns, held))
                                            : fence == FenceState.TakenOver
                                                ? Observable.Throw<int>(new InvalidOperationException(
                                                    $"The move of '{legacyPath}' was taken over by another attempt; this one stops."))
                                                : Cancelled(owner, provider, fenced.Generation, attempt, owns, held))));
                        }))
                .Do(moved => logger?.LogInformation(
                    "Mail copy: moved {Count} {Provider} copies of {Owner} into their own partition {Partition}",
                    moved, provider, owner, MailCopyNodeTypes.NamespaceFor(owner)))
                // Error, timeout or disposal alike: what this attempt still holds is released (fire and forget —
                // the release is not the outcome, and it must run even when nobody listens any more).
                .Finally(() => Release(owner, legacyPath, sourcePath, attempt, held));
        });

    // What one move attempt holds: the generation of the legacy source it fenced, and of the destination it took —
    // each null once released by the attempt itself (deleted, or handed back).
    private sealed class MoveHold
    {
        public string? Legacy { get; set; }
        public string? Destination { get; set; }
    }

    // The fence: the attempt's lease, taken only when no lease is held (or it is this attempt's, or it lapsed). Pure.
    private static MailCopySource? Fenced(MailCopySource current, string attempt)
        => current.LeaseHolder is null || current.LeaseHolder == attempt || current.LeaseUntil is not { } until || until <= DateTimeOffset.UtcNow
            ? current with { LeaseHolder = attempt, LeaseUntil = DateTimeOffset.UtcNow + MoveLease }
            : null;

    private enum FenceState { Held, TakenOver, Gone }

    // Where the legacy fence stands at the end of a move: still this attempt's, taken over (the legacy source exists
    // and carries another lease), or gone (the legacy source was deleted — a switch-off). Read by a no-op commit at
    // its owning hub, after an existence read (an absent node's stream never answers).
    private IObservable<FenceState> Fence(string owner, string legacyPath, string generation, string attempt)
        => LegacyExists(owner, legacyPath).SelectMany(exists => exists
            ? CommitAt(owner, legacyPath, generation, current => current.LeaseHolder == attempt ? current : null)
                .Select(applied => applied is not null ? FenceState.Held : FenceState.TakenOver)
            : Observable.Return(FenceState.Gone));

    // The legacy source is deleted: the destination is released with the legacy source's pre-fence backfill state.
    private IObservable<int> Finished(string owner, string sourcePath, string generation, bool backfilled, string attempt, bool owns, MoveHold held, int moved)
        => owns
            ? CommitAt(owner, sourcePath, generation, current => current.LeaseHolder == attempt
                    ? current with { Backfilled = backfilled, LeaseHolder = null, LeaseUntil = null }
                    : null)
                .Do(_ => held.Destination = null)
                .Select(_ => moved)
            : Observable.Return(moved);

    // Switched off while this attempt moved: the destination is rotated off — in ONE commit that applies only while it
    // still carries this attempt's lease — and then cleaned and deleted. A destination this attempt no longer holds (a
    // successor took over and finished) is left exactly as it is.
    private IObservable<int> Cancelled(string owner, string provider, string generation, string attempt, bool owns, MoveHold held)
        => !owns
            ? Observable.Return(0)
            : CommitAt(owner, MailCopyNodeTypes.SourcePath(owner, provider), generation, current => current.LeaseHolder == attempt
                    ? RotatedForSwitchOff(current)
                    : null)
                .Do(rotated => { if (rotated is not null) held.Destination = null; })
                .SelectMany(rotated => rotated is null
                    ? Observable.Return(0)
                    : DisablePasses(owner, provider, 1, 0)
                        .SelectMany(_ => Write(owner, m => m.Mesh.DeleteNode(MailCopyNodeTypes.SourcePath(owner, provider))))
                        .Select(_ => 0));

    // Releases what the attempt still holds, through the node it took it on (each commit applies only while the lease
    // is this attempt's). Fire and forget: a release that cannot be made is logged, and the lease lapses.
    private void Release(string owner, string legacyPath, string sourcePath, string attempt, MoveHold held)
    {
        MailCopySource? Release(MailCopySource current) => current.LeaseHolder == attempt ? current with { LeaseHolder = null, LeaseUntil = null } : null;
        var releases = new List<IObservable<MailCopySource?>>();
        if (held.Legacy is { } legacyGeneration)
            releases.Add(CommitAt(owner, legacyPath, legacyGeneration, Release));
        if (held.Destination is { } destinationGeneration)
            releases.Add(CommitAt(owner, sourcePath, destinationGeneration, Release));
        if (releases.Count == 0)
            return;
        releases.Select(r => r.Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Mail copy: releasing a move lease of {Owner} failed; it lapses after {Lease}", owner, MoveLease);
                return Observable.Return<MailCopySource?>(null);
            }))
            .Concat()
            .Subscribe(_ => { }, _ => { });
    }

    // The destination source of a move: created as the fenced legacy one, not backfilled and leased to this attempt —
    // or, when one exists, READ FROM ITS STREAM (the create just proved it exists; the index may not show it yet): of
    // the SAME generation (an interrupted move) it is taken over the same way, of another generation it is not this
    // move's (false); one held by another live attempt refuses the move, and one whose content cannot be read fails it.
    private IObservable<bool> Destination(string owner, string provider, MailCopySource fenced, string attempt)
        => Write(owner, m => m.Mesh.CreateNode(MailCopyNodeTypes.BuildSource(owner, fenced with { Backfilled = false })))
            .Select(_ => true)
            .Catch((Exception ex) => !MailCopyNodeTypes.IsAlreadyExists(ex)
                ? Observable.Throw<bool>(ex)
                : LiveSource(owner, MailCopyNodeTypes.SourcePath(owner, provider))
                    .SelectMany(existing => existing is null
                        ? Observable.Throw<bool>(new InvalidOperationException(
                            $"The mail copy '{MailCopyNodeTypes.SourcePath(owner, provider)}' exists but could not be read; the move stops."))
                        : existing.Generation != fenced.Generation
                            ? Observable.Return(false)
                            : CommitAt(owner, MailCopyNodeTypes.SourcePath(owner, provider), fenced.Generation,
                                    current => Fenced(current, attempt) is { } taken ? taken with { Backfilled = false } : null)
                                .SelectMany(taken => taken is null
                                    ? Observable.Throw<bool>(new InvalidOperationException(
                                        $"The mail copy '{MailCopyNodeTypes.SourcePath(owner, provider)}' is being moved elsewhere; the next sync repeats the move."))
                                    : Observable.Return(true))));

    // A source node known to EXIST (a create just said so), read from its stream rather than the index.
    private IObservable<MailCopySource?> LiveSource(string owner, string path)
        => Live(owner, path).SelectMany(node => Mesh().Select(m => node.ContentAs<MailCopySource>(m.Options, logger)));

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<MailCopySource?> LegacySource(string owner, string provider)
        => Gated(Assembly.GetCallingAssembly(), nameof(LegacySource), owner, Permission.Read, () => LegacySourceCore(owner, provider));

    // The legacy source: FOUND through a complete listing frame (an incomplete one is a fault, never "none"), then
    // READ from its stream, which the index can trail. As System: the move runs in the background with no viewer left.
    private IObservable<MailCopySource?> LegacySourceCore(string owner, string provider)
    {
        var legacyPath = MailCopyNodeTypes.LegacySourcePath(owner, provider);
        return LegacyExists(owner, legacyPath).SelectMany(exists => exists
            ? Live(owner, legacyPath).SelectMany(node => Mesh().Select(m => node.ContentAs<MailCopySource>(m.Options, logger)))
            : Observable.Return<MailCopySource?>(null));
    }

    private IObservable<bool> LegacyExists(string owner, string legacyPath)
        => Mesh().SelectMany(m => m.Access
            .RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => m.Mesh
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{legacyPath}", owner) with { Limit = 1 })
                .Take(1))
            .Take(1)
            .SelectMany(change => RejectIncompleteListing(change, nameof(MoveLegacy)))
            .Select(change => change.Items.Any(node => string.Equals(node.NodeType, MailCopyNodeTypes.SourceNodeType, StringComparison.Ordinal)
                                                  && string.Equals(node.Path, legacyPath, StringComparison.Ordinal))));

    private IObservable<int> MovePasses(string owner, string provider, int pass, int moved)
        => Mesh().SelectMany(m => m.Access
            .RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => m.Mesh
                .Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{MailCopyNodeTypes.LegacySourcePath(owner, provider)} nodeType:{MailCopyNodeTypes.MessageNodeType} limit:{RemovePass}", owner) with { Limit = RemovePass })
                .Take(1))
            .Take(1)
            // 🚨 An empty page proves the legacy place empty only when every provider answered in full: the legacy
            // source is deleted next, and with it whatever an incomplete frame hid.
            .SelectMany(change => RejectIncompleteListing(change, nameof(MoveLegacy)))
            .Select(change => change.Items
                .Where(node => string.Equals(node.NodeType, MailCopyNodeTypes.MessageNodeType, StringComparison.Ordinal)
                               && string.Equals(node.Namespace, MailCopyNodeTypes.LegacySourcePath(owner, provider), StringComparison.Ordinal))
                .ToImmutableList())
            .SelectMany(nodes => nodes.Count == 0
                ? Observable.Return(moved)
                : nodes.Select(node => MoveCopy(owner, provider, node, m.Options)).MergeBounded(4).Count()
                    // 🚨 At the pass limit with copies left the move FAILS and the legacy source stays, so the next
                    // sync repeats it — it never deletes the legacy source over copies it did not move.
                    .SelectMany(count => pass < MaxRemovePasses
                        ? MovePasses(owner, provider, pass + 1, moved + count)
                        : Observable.Throw<int>(new InvalidOperationException(
                            "The legacy copies were not all moved within the pass limit; the legacy source is kept so the move can be repeated.")))));

    /// <summary>
    /// One legacy copy: written at its new path FIRST (its attachments need the node they belong to), its files
    /// copied, and only then its legacy files and node deleted. 🚨 Every step is a no-op or an overwrite when
    /// repeated, so a move interrupted anywhere finishes on the next sync; a failure stops before the legacy
    /// files go, and the legacy node keeps the only handle on them.
    /// </summary>
    private IObservable<bool> MoveCopy(string owner, string provider, MeshNode node, System.Text.Json.JsonSerializerOptions options)
        => Live(owner, node.Path).SelectMany(live =>
        {
            if (live.ContentAs<MailMessageCopy>(options, logger) is not { } copy)
                return Observable.Throw<bool>(new InvalidOperationException($"The legacy copy '{node.Path}' could not be read; it is left where it is."));
            var target = $"{MailCopyNodeTypes.SourcePath(owner, provider)}/{node.Id}";
            var moved = MailCopyNodeTypes.Relocated(copy, node.Path, target);
            var withFiles = MailAttachments.MayHoldFiles(copy);
            if (withFiles && attachments is null)
                return Observable.Throw<bool>(new InvalidOperationException($"The legacy copy '{node.Path}' may hold files, and this host cannot move them."));
            return Write(owner, m => m.Mesh.CreateNode(new MeshNode(node.Id, MailCopyNodeTypes.SourcePath(owner, provider))
                    {
                        Name = live.Name,
                        NodeType = MailCopyNodeTypes.MessageNodeType,
                        Icon = MailCopyNodeTypes.Icon,
                        State = MeshNodeState.Active,
                        Content = moved,
                    }).Select(_ => true))
                .Catch((Exception ex) => MailCopyNodeTypes.IsAlreadyExists(ex) ? Observable.Return(true) : Observable.Throw<bool>(ex))
                .SelectMany(_ => withFiles
                    ? Mesh().SelectMany(m => m.Access.RunAsSystemFor(governedBy: null, onBehalfOf: owner,
                            () => attachments!.CopyLegacy(owner, node.Path, target)))
                        .SelectMany(_ => Mesh().SelectMany(m => m.Access.RunAsSystemFor(governedBy: null, onBehalfOf: owner,
                            () => attachments!.DeleteLegacy(owner, node.Path))))
                        .Select(_ => true)
                    : Observable.Return(true))
                .SelectMany(_ => Write(owner, m => m.Mesh.DeleteNode(node.Path)))
                .Select(_ => true);
        });

    /// <summary>
    /// Makes the owner's copy partition exist — the storage schema, the <c>Admin/Partition</c> definition that
    /// routes it, its root node and the owner's Viewer grant — as System, each step create-if-absent, so a repeat
    /// (another click, another replica) changes nothing. 🚨 "No partition, no write": the store refuses a write
    /// to a partition nobody provisioned, so this runs before the first one.
    /// </summary>
    private IObservable<System.Reactive.Unit> EnsurePartition(string owner)
        => Mesh().SelectMany(m =>
        {
            var partition = MailCopyNodeTypes.NamespaceFor(owner);
            var root = MailCopyNodeTypes.BuildPartitionRoot(owner);
            var providers = m.Hub.ServiceProvider.GetServices<IPartitionStorageProvider>().ToArray();
            var provisioned = providers.Length == 0
                ? Observable.Return(System.Reactive.Unit.Default)
                : providers.Select(p => p.EnsurePartitionProvisioned(partition)).Merge()
                    .DefaultIfEmpty(System.Reactive.Unit.Default).LastAsync();
            return provisioned
                .SelectMany(_ => ForeignRoot(m, owner, partition))
                .SelectMany(_ => CreateIfAbsent(owner, PartitionOwnership.PartitionDefinitionNode(root, partition)))
                .SelectMany(_ => CreateIfAbsent(owner, root))
                .SelectMany(_ => CreateIfAbsent(owner, MailCopyNodeTypes.OwnerViewerGrant(owner)));
        });

    // 🚨 Fails CLOSED when a partition of that name exists and is NOT a copy partition (a Space or a home called
    // "jane_MailCopy"): its grants would expose the mail, and the owner's Viewer grant its content.
    private IObservable<System.Reactive.Unit> ForeignRoot(Portal m, string owner, string partition)
        => m.Access
            .RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => m.Mesh
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{partition}") with { Limit = 1 })
                .Take(1))
            .Take(1)
            .SelectMany(change => RejectIncompleteListing(change, nameof(EnsurePartition)))
            .SelectMany(change => change.Items.FirstOrDefault(node => string.Equals(node.Path, partition, StringComparison.Ordinal)) is { } existing
                                  && !string.Equals(existing.NodeType, MailCopyNodeTypes.PartitionNodeType, StringComparison.Ordinal)
                ? Observable.Throw<System.Reactive.Unit>(new InvalidOperationException(
                    $"'{partition}' exists and is not a mail copy partition (it is a {existing.NodeType}); the mail copy of '{owner}' is not written there."))
                : Observable.Return(System.Reactive.Unit.Default));

    private IObservable<System.Reactive.Unit> CreateIfAbsent(string owner, MeshNode node)
        => Write(owner, m => m.Mesh.CreateNode(node))
            .Select(_ => System.Reactive.Unit.Default)
            .Catch((Exception ex) => MailCopyNodeTypes.IsAlreadyExists(ex)
                ? Observable.Return(System.Reactive.Unit.Default)
                : Observable.Throw<System.Reactive.Unit>(ex));

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
                    : Live(owner, found.Path).Select(live => Copies([live], provider, generation, m.Options).FirstOrDefault()))));

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
        // 🚨 Both places: a copy not yet moved out of the home partition (MoveLegacy) is still on, and must still be
        // listed — and deletable — like any other.
        => Gated(Assembly.GetCallingAssembly(), nameof(SourceKeys), owner, Permission.Read, () => Keys(owner, MailCopyNodeTypes.NamespaceFor(owner))
            .Zip(Keys(owner, MailCopyNodeTypes.LegacyNamespaceFor(owner)), (current, legacy) => current.Union(legacy).ToImmutableList()));

    private IObservable<ImmutableList<string>> Keys(string owner, string @namespace)
        => Mesh().SelectMany(m => m.Mesh
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{@namespace} nodeType:{MailCopyNodeTypes.SourceNodeType}", owner)
                // An ENUMERATION: a key clipped off this list is a copy the owner can no longer see or delete.
                .Complete())
            .Take(1)
            .SelectMany(change => RejectIncompleteListing(change, nameof(SourceKeys)))
            .Select(change => change.Items
                .Where(node => string.Equals(node.NodeType, MailCopyNodeTypes.SourceNodeType, StringComparison.Ordinal)
                               && string.Equals(node.Namespace, @namespace, StringComparison.Ordinal))
                .Select(node => node.Id)
                .ToImmutableList()));

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
                        .Where(node => string.Equals(node.NodeType, MailCopyNodeTypes.SourceNodeType, StringComparison.Ordinal))
                        // Both places: resuming a copy still at the legacy place is what moves it (MoveLegacy).
                        .Select(node => MailCopyNodeTypes.OwnerOfSourceNamespace(node.Namespace) is { } owner
                            ? new MailCopySourceRef(owner, node.Id)
                            : null)
                        .OfType<MailCopySourceRef>()
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
