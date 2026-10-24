using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.MailCopy;

/// <summary>
/// One step of a provider's incremental feed: the messages added or changed (already as copies — the engine
/// stamps their generation and time), the ids removed, and where the next step resumes.
/// </summary>
/// <param name="Changed">The messages inside the window that changed.</param>
/// <param name="Removed">The ids no longer in the mailbox's inbox.</param>
/// <param name="Cursor">The provider's continuation (null: none to give — start over).</param>
/// <param name="Done">Whether the feed reached the present (the backfill is complete).</param>
public sealed record MailFeedStep(ImmutableList<MailMessageCopy> Changed, ImmutableList<string> Removed, string? Cursor, bool Done);

/// <summary>Raised by a feed when the provider refuses the stored cursor for good (expired history); the copy is rebuilt as a new generation.</summary>
public sealed class MailFeedResyncRequiredException() : InvalidOperationException("The provider no longer accepts the stored sync cursor; the copy is rebuilt.");

/// <summary>
/// A provider's side of the generic copy engine: its connected account, its incremental feed over the inbox
/// and the attachments of its messages. Gmail implements one (history-id based); the engine does everything
/// else the same way for every provider — generations, the lease, the window, tombstones, attachments, the
/// switch-off.
/// </summary>
public interface IMailCopyFeed
{
    /// <summary>The provider segment the copies are filed under (<c>gmail</c>).</summary>
    string Provider { get; }

    /// <summary>The provider's id of the account connected NOW for <paramref name="owner"/>; errors when none is connected. Cold; emits once.</summary>
    IObservable<string> AccountId(string owner);

    /// <summary>The owner's connected accounts with a label for each, for the switch. Cold; emits once.</summary>
    IObservable<ImmutableList<(string AccountId, string Label)>> Accounts(string owner);

    /// <summary>One step of the feed from <paramref name="cursor"/> (null: the start). Cold; emits once.</summary>
    IObservable<MailFeedStep> Step(string owner, string? cursor, DateTimeOffset windowStart);

    /// <summary>The attachments of the account connected now. Cold; emits once.</summary>
    IObservable<IMailAttachmentSource> Attachments(string owner);
}

/// <summary>
/// The opt-in copy of ONE provider account, generic over its <see cref="IMailCopyFeed"/>. It carries the
/// guarantees of the Outlook copy that do not depend on the provider: a copy exists only when the person
/// switched it on (<see cref="Enable"/>); every switch-on starts a new generation; one sync per owner holds
/// the lease; a copy is written create-or-replace by (generation, message id) so a re-sync overwrites;
/// removals are tombstones that delete the message's attachment files first; switching off deletes the
/// source, every copy, the files and the index.
/// </summary>
public sealed class MailCopyEngine(
    IMailCopyFeed feed, IMailCopyStore store, TimeProvider? clock = null, ILogger? logger = null, IMailAttachmentStore? attachments = null,
    AccessService? access = null)
{
    // 🚨 The sync is BACKGROUND work: it carries out the owner's own switch-on, copying the owner's own
    // account into the owner's own copy, and runs after provider hops that keep no ambient identity. So it
    // never relies on one: every store call it makes is DECLARED System on the owner's behalf (the stores
    // refuse a call with no identity). Switching on, off and reading go through the caller's own identity.
    private readonly IMailCopyStore copies = new BackgroundMailCopyStore(store, access);
    private readonly IMailAttachmentStore? fileStore = attachments is null ? null : new BackgroundMailAttachmentStore(attachments, access);

    /// <summary>The most feed steps one sync takes before it yields.</summary>
    public const int MaxStepsPerSync = 10;

    /// <summary>The least time between two syncs nobody explicitly asked for.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(2);

    /// <summary>How long a lease holds without being renewed.</summary>
    public static readonly TimeSpan LeaseTime = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(10);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly string holder = Guid.NewGuid().ToString("N");
    private ImmutableHashSet<string> running = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    private ImmutableDictionary<string, DateTimeOffset> lastStarted = ImmutableDictionary<string, DateTimeOffset>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>The provider segment of this engine's copies.</summary>
    public string Provider => feed.Provider;

    /// <summary>The owners this process has synced or been asked to.</summary>
    public IReadOnlyCollection<string> KnownOwners => lastStarted.Keys.ToImmutableList();

    /// <summary>Whether a copy exists for <paramref name="owner"/> on this installation. Cold; emits once.</summary>
    public IObservable<bool> IsOn(string owner) => store.Source(owner, Provider).Take(1).DefaultIfEmpty(null).Select(source => source is not null);

    /// <summary>The copy's source, or null while it is off. Cold; emits once.</summary>
    public IObservable<MailCopySource?> Source(string owner) => store.Source(owner, Provider).Take(1).DefaultIfEmpty(null);

    /// <summary>
    /// Switches the copy ON (a no-op when it is on) and starts the backfill. Cold. The switch-on is judged as
    /// the identity ambient when this is CALLED — never as whoever later subscribes the result — while every
    /// subscription still mints its own generation. The backfill runs as System on the owner's behalf, so the
    /// CODE that calls this is judged by the in-mesh impersonation guard at the call, exactly as for
    /// <see cref="Kick"/>.
    /// </summary>
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<MailCopySource> Enable(string owner, int windowDays)
        => EnableFrom(Assembly.GetCallingAssembly(), owner, windowDays, nameof(Enable));

    /// <summary>
    /// <see cref="Enable"/> on behalf of <paramref name="callerCode"/> — the assembly that called the PUBLIC
    /// surface (<see cref="Enable"/>, <see cref="MailFeedAccountCopy.Enable"/> or <see cref="MailCopySwitch.Enable"/>).
    /// 🚨 That assembly is checked HERE, at the call, and it is the one the backfill's kick is judged as —
    /// never this platform assembly, which would pass any caller and run a caller-supplied store's callbacks
    /// as System.
    /// </summary>
    internal IObservable<MailCopySource> EnableFrom(Assembly callerCode, string owner, int windowDays, string surface)
    {
        CheckCaller(callerCode, surface, owner);
        // 🚨 Read HERE, at the call: the Defer below runs at subscribe, possibly after the caller's scope has
        // ended and under somebody else's identity (a System or admin subscriber must not launder the call).
        var caller = access is null ? null : access.Context ?? access.CircuitContext;
        return Observable.Defer(() =>
            {
                if (access is not null && caller is null)
                    return Observable.Throw<MailCopySource>(new UnauthorizedAccessException(
                        $"Switching on the mail copy of '{owner}' needs a signed-in person; this call carries no identity."));
                var now = time.GetUtcNow();
                var days = windowDays >= 1 ? windowDays : MailCopyNodeTypes.WindowDays;
                using (access?.SwitchAccessContext(caller))
                    return store.Create(owner, new MailCopySource
                    {
                        Provider = Provider,
                        Generation = MailCopyNodeTypes.NewGeneration(),
                        Projection = MailCopySource.CurrentProjection,
                        EnabledAt = now,
                        WindowStart = now.AddDays(-days),
                    });
            })
            .Do(_ => KickFrom(callerCode, owner, force: true, surface));
    }

    /// <summary>Switches the copy OFF: the source, every copy, their attachment files and index go. Cold; true when something was removed.</summary>
    public IObservable<bool> Disable(string owner) => store.Disable(owner, Provider);

    /// <summary>
    /// Starts a sync unless one runs in this process or, unless <paramref name="force"/>, one started lately.
    /// Fire and forget. The sync runs as System on the owner's behalf, so the CODE that asks is judged by the
    /// in-mesh impersonation guard exactly as if it had called <c>RunAsSystemFor</c> itself.
    /// </summary>
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Kick(string owner, bool force = false)
        => KickFrom(Assembly.GetCallingAssembly(), owner, force, nameof(Kick));

    /// <summary>
    /// <see cref="Kick"/> on behalf of <paramref name="caller"/> — the assembly that called the PUBLIC surface
    /// (<see cref="Kick"/>, or <see cref="MailFeedAccountCopy.KickAll"/>), which the guard judges; never this
    /// platform assembly, which would pass any caller.
    /// </summary>
    internal void KickFrom(Assembly caller, string owner, bool force = false, string surface = nameof(Kick))
    {
        CheckCaller(caller, surface, owner);
        var now = time.GetUtcNow();
        if (!force && lastStarted.TryGetValue(owner, out var last) && now - last < MinInterval)
            return;
        if (!TryEnter(owner))
            return;
        ImmutableInterlocked.AddOrUpdate(ref lastStarted, owner, now, (_, _) => now);
        SyncCore(owner)
            .Timeout(TimeSpan.FromMinutes(30))
            .Finally(() => Exit(owner))
            .Subscribe(_ => { }, ex => logger?.LogWarning(ex, "{Provider} copy: the sync of {Owner} failed", Provider, owner));
    }

    /// <summary>
    /// One sync: the lease taken, then feed steps from the stored cursor — each step's writes first, then its
    /// cursor committed under the lease — and the lease released. For a copy that is OFF it only sweeps what a
    /// late write left behind: it reads one node and writes nothing else. Cold. One sync per owner runs in this
    /// engine: a subscription made while another (or a <see cref="Kick"/>) runs emits null at once and does
    /// nothing; the gate is released when the running one completes, fails or is disposed.
    /// </summary>
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<MailCopySource?> Sync(string owner)
    {
        CheckCaller(Assembly.GetCallingAssembly(), nameof(Sync), owner);
        return Observable.Defer(() => TryEnter(owner)
            ? SyncCore(owner).Finally(() => Exit(owner))
            : Observable.Return<MailCopySource?>(null));
    }

    // The per-owner single-flight gate: every sync of this engine shares one lease holder, so two at once
    // would both pass Acquire, step the same cursor and release each other's lease.
    private bool TryEnter(string owner) => ImmutableInterlocked.Update(ref running, static (set, o) => set.Add(o), owner);

    private void Exit(string owner) => ImmutableInterlocked.Update(ref running, static (set, o) => set.Remove(o), owner);

    private void CheckCaller(Assembly caller, string surface, string owner)
        => access?.ImpersonationGuard.Check(caller, $"{nameof(MailCopyEngine)}.{surface}", new AccessContext
        {
            ObjectId = AccessService.SystemObjectId,
            Name = AccessService.SystemObjectId,
            OnBehalfOf = owner,
        });

    private IObservable<MailCopySource?> SyncCore(string owner)
        => copies.Source(owner, Provider).Take(1).Timeout(ReadBudget).SelectMany(source => source is null
            ? Sweep(owner).Select(_ => (MailCopySource?)null)
            : Acquire(owner, source).SelectMany(leased => leased is null
                ? Observable.Return<MailCopySource?>(null)
                : (MailCopyNodeTypes.IsValidGeneration(leased.Generation) && leased.Projection >= MailCopySource.CurrentProjection
                    ? Observable.Return<MailCopySource?>(leased)
                    : Rebuild(owner, leased))
                    .SelectMany(valid => valid is null
                        ? Observable.Return<MailCopySource?>(null)
                        : Bind(owner, valid).SelectMany(bound => bound is null
                            ? Observable.Return<MailCopySource?>(null)
                            : Sweep(owner).SelectMany(_ => feed.Attachments(owner)).SelectMany(files => Step(owner, bound, 1, files))))
                    .Catch((Exception ex) => Release(owner, ex.Message).SelectMany(_ => Observable.Throw<MailCopySource?>(ex)))
                    .SelectMany(done => Release(owner, null).Select(released => done is null ? null : released ?? done))));

    private IObservable<MailCopySource?> Acquire(string owner, MailCopySource source)
        => copies.Commit(owner, Provider, source.Generation, current =>
            current.LeaseHolder is null || current.LeaseHolder == holder || current.LeaseUntil is not { } until || until <= time.GetUtcNow()
                ? current with { LeaseHolder = holder, LeaseUntil = time.GetUtcNow() + LeaseTime }
                : null);

    private Func<MailCopySource, MailCopySource?> UnderLease(Func<MailCopySource, MailCopySource> change)
        => current => current.LeaseHolder == holder
            ? change(current) with { LeaseHolder = holder, LeaseUntil = time.GetUtcNow() + LeaseTime }
            : null;

    private IObservable<MailCopySource?> Release(string owner, string? error)
        => copies.Source(owner, Provider).Take(1).DefaultIfEmpty(null)
            .SelectMany(current => current is null || current.LeaseHolder != holder
                ? Observable.Return<MailCopySource?>(null)
                : copies.Commit(owner, Provider, current.Generation, there => there.LeaseHolder == holder
                    ? there with
                    {
                        LeaseHolder = null,
                        LeaseUntil = null,
                        LastError = error ?? there.LastError,
                        LastSyncAt = error is null ? there.LastSyncAt : time.GetUtcNow(),
                    }
                    : null))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "{Provider} copy: the sync lease of {Owner} could not be released", Provider, owner);
                return Observable.Return<MailCopySource?>(null);
            });

    // The copy is bound to the account connected when it was made: another account connected since means the
    // copy is rebuilt from it, never served under the first one's label.
    private IObservable<MailCopySource?> Bind(string owner, MailCopySource source)
        => feed.AccountId(owner).Take(1).Timeout(ReadBudget).SelectMany(id =>
            source.MailboxId == id
                ? Observable.Return<MailCopySource?>(source)
                : source.MailboxId is null
                    ? copies.Commit(owner, Provider, source.Generation, UnderLease(current => current with { MailboxId = id }))
                    : Rebuild(owner, source, id));

    private IObservable<MailCopySource?> Rebuild(string owner, MailCopySource source, string? mailboxId = null)
    {
        var generation = MailCopyNodeTypes.NewGeneration();
        logger?.LogInformation("{Provider} copy: rebuilding {Owner}'s copy as generation {Generation}", Provider, owner, generation);
        return copies.Commit(owner, Provider, source.Generation, UnderLease(current => current with
            {
                Generation = generation,
                Projection = MailCopySource.CurrentProjection,
                MailboxId = mailboxId ?? current.MailboxId,
                Cursor = null,
                Backfilled = false,
                LastSyncAt = time.GetUtcNow(),
                LastError = null,
            }))
            .SelectMany(rebuilt => rebuilt is null
                ? Observable.Return<MailCopySource?>(null)
                : (MailCopyNodeTypes.IsValidGeneration(source.Generation)
                    ? copies.RemoveGeneration(owner, Provider, source.Generation)
                    : Sweep(owner)).Select(_ => (MailCopySource?)rebuilt));
    }

    // Removes the copies a late write left behind — of generations PROVEN obsolete. Emits how many. 🚨 Private:
    // it deletes through the System-backed store, so it runs only inside the checked Sync / Kick.
    private IObservable<int> Sweep(string owner)
        => copies.Source(owner, Provider).Take(1).DefaultIfEmpty(null)
            .SelectMany(before => copies.List(owner, Provider, MeshMailCopyStore.MaxPage, before?.Generation))
            .SelectMany(listed => listed.Count == 0
                ? Observable.Return(0)
                : copies.Source(owner, Provider).Take(1).DefaultIfEmpty(null).SelectMany(current =>
                {
                    var obsolete = listed.Where(copy => current is null || copy.Generation != current.Generation).ToList();
                    return obsolete.Count == 0
                        ? Observable.Return(0)
                        : obsolete.Select(copy => copies.Remove(owner, Provider, copy.Generation, copy.MessageId)).MergeBounded(4).Count();
                }));

    private IObservable<MailCopySource?> Step(string owner, MailCopySource source, int step, IMailAttachmentSource files)
        => feed.Step(owner, source.Cursor, source.WindowStart).Take(1)
            .SelectMany(feedStep => copies.Commit(owner, Provider, source.Generation, UnderLease(current => current))
                .SelectMany(renewed => renewed is null
                    ? Lost(owner, source).Select(_ => (Committed: (MailCopySource?)null, Feed: (MailFeedStep?)null))
                    : Apply(owner, source, feedStep, files).SelectMany(failures => failures > 0
                        ? Observable.Throw<(MailCopySource? Committed, MailFeedStep? Feed)>(new InvalidOperationException(
                            $"{failures} of this step's copy writes failed; the step is repeated"))
                        : CommitOrClean(owner, source, UnderLease(current => current with
                        {
                            Cursor = feedStep.Cursor,
                            Backfilled = current.Backfilled || feedStep.Done,
                            LastSyncAt = time.GetUtcNow(),
                            LastError = null,
                        })).Select(committed => (Committed: committed, Feed: (MailFeedStep?)feedStep)))))
            .Catch((Exception ex) => ex is MailFeedResyncRequiredException && step < MaxStepsPerSync
                ? Rebuild(owner, source).Select(rebuilt => (Committed: rebuilt, Feed: (MailFeedStep?)null))
                : Failed(owner, source, ex))
            .SelectMany(result => result.Committed is { } committed
                                  && (result.Feed is null || !result.Feed.Done && committed.Cursor is not null)
                                  && step < MaxStepsPerSync
                ? Step(owner, committed, step + 1, files)
                : Observable.Return(result.Committed));

    private IObservable<(MailCopySource? Committed, MailFeedStep? Feed)> Failed(string owner, MailCopySource source, Exception ex)
        => CommitOrClean(owner, source, UnderLease(current => current with { LastError = ex.Message, LastSyncAt = time.GetUtcNow() }))
            .Catch((Exception _) => Observable.Return<MailCopySource?>(null))
            .SelectMany(_ => Observable.Throw<(MailCopySource? Committed, MailFeedStep? Feed)>(ex));

    private IObservable<MailCopySource?> CommitOrClean(string owner, MailCopySource started, Func<MailCopySource, MailCopySource?> change)
        => copies.Commit(owner, Provider, started.Generation, change)
            .SelectMany(committed => committed is not null
                ? Observable.Return<MailCopySource?>(committed)
                : Lost(owner, started).Select(_ => (MailCopySource?)null));

    // The lease was lost or the copy switched off meanwhile: what this sync wrote is removed by ITS generation.
    private IObservable<int> Lost(string owner, MailCopySource source)
        => copies.Source(owner, Provider).Take(1).DefaultIfEmpty(null).SelectMany(current => current is null || current.Generation != source.Generation
            ? copies.RemoveGeneration(owner, Provider, source.Generation)
            : Observable.Return(0));

    private IObservable<int> Apply(string owner, MailCopySource source, MailFeedStep step, IMailAttachmentSource files)
    {
        var at = time.GetUtcNow();
        IObservable<int> Counted(IObservable<bool> write) => write.Take(1).Select(_ => 0).DefaultIfEmpty(0)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "{Provider} copy: a copy write for {Owner} failed", Provider, owner);
                return Observable.Return(1);
            });
        var writes = step.Removed.Select(id => Counted(Tombstoned(owner, source.Generation, id, at)))
            .Concat(step.Changed.Where(m => m.ReceivedAt is { } received && received >= source.WindowStart)
                .Select(message => Counted(Copied(owner, source, message with
                {
                    Provider = Provider,
                    Generation = source.Generation,
                    CopiedAt = at,
                }, files))))
            .ToList();
        return writes.Count == 0 ? Observable.Return(0) : writes.MergeBounded(4).Sum();
    }

    private IObservable<MailMessageCopy?> Prior(string owner, string generation, string messageId)
        => fileStore is null
            ? Observable.Return<MailMessageCopy?>(null)
            : copies.Read(owner, Provider, generation, messageId).Take(1).DefaultIfEmpty(null);

    private IObservable<bool> Copied(string owner, MailCopySource source, MailMessageCopy message, IMailAttachmentSource files)
        => Prior(owner, source.Generation, message.MessageId).SelectMany(prior =>
        {
            // The decision already stored is carried over (never fetched again); a message without attachments
            // is decided empty; one the provider did not say anything about stays undecided.
            var known = fileStore is null ? null
                : prior?.Attachments is { } existing && existing.Any(a => a.StoredPath is not null) ? existing
                : message.HasAttachments == false ? ImmutableList<MailAttachmentCopy>.Empty
                : prior?.Attachments;
            // 🚨 A marker that said "has attachments" is never overwritten by "none": an interrupted save leaves
            // files with no list, and removal finds them through the marker.
            var copy = message with { Attachments = known, HasAttachments = message.HasAttachments == false && prior?.HasAttachments == true ? true : message.HasAttachments };
            return copies.WriteIfNewer(owner, copy).SelectMany(wrote => wrote
                ? Attach(owner, copy, files).Select(_ => true)
                : Observable.Return(false));
        });

    private IObservable<Unit> Attach(string owner, MailMessageCopy written, IMailAttachmentSource files)
    {
        if (fileStore is null || written.HasAttachments != true || written.Attachments is not null)
            return Observable.Return(Unit.Default);
        var copyPath = MailCopyNodeTypes.CopyPath(owner, Provider, written.Generation, written.MessageId);
        return MailAttachments.Collect(files, fileStore, owner, copyPath, written.MessageId, logger)
            .SelectMany(decided => copies.SetAttachments(owner, Provider, written.Generation, written.MessageId, decided))
            .SelectMany(recorded => recorded
                ? Observable.Return(Unit.Default)
                : copies.Read(owner, Provider, written.Generation, written.MessageId).Take(1).DefaultIfEmpty(null)
                    .SelectMany(current => current is null ? fileStore.DeleteAll(owner, copyPath) : Observable.Return(Unit.Default)))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "{Provider} copy: the attachments of {Copy} could not be copied", Provider, copyPath);
                return Observable.Return(Unit.Default);
            });
    }

    // The files go BEFORE the tombstone, and only when it will be accepted; what the tombstone replaced is
    // cleaned too (read atomically with the write). A failed cleanup fails the step, which repeats.
    // 🚨 The tombstone itself carries the "may hold files" marker (Tombstone): the copy's inventory is gone
    // the moment it lands, and Read never answers a tombstone, so a repeat that finds no prior must still
    // learn from the tombstone it replaces that its cleanup is owed.
    private IObservable<bool> Tombstoned(string owner, string generation, string messageId, DateTimeOffset at)
        => Prior(owner, generation, messageId).SelectMany(prior =>
            (prior is null || prior.CopiedAt > at ? Observable.Return(Unit.Default) : DropFiles(owner, generation, messageId, prior))
                .SelectMany(_ => copies.ReplaceIfNewer(owner, Tombstone(generation, messageId, at)))
                .SelectMany(result => result.Wrote && CleansAfterTombstone(prior, at, result.Replaced)
                    ? DropFiles(owner, generation, messageId, result.Replaced).Select(_ => true)
                    : Observable.Return(result.Wrote)));

    /// <summary>
    /// The tombstone this engine writes: with an attachment store, it keeps the "may hold files" marker
    /// (<see cref="MailMessageCopy.HasAttachments"/>) whatever it replaces — until the node is deleted, the
    /// marker is the only record that files may still be there, and every later removal of the node
    /// (<see cref="MailAttachments.MayHoldFiles"/>) empties the collection by enumerating it. Pure.
    /// </summary>
    public MailMessageCopy Tombstone(string generation, string messageId, DateTimeOffset at)
    {
        var tombstone = MailCopyNodeTypes.Tombstone(Provider, generation, messageId, at);
        return fileStore is null ? tombstone : tombstone with { HasAttachments = true };
    }

    /// <summary>
    /// Whether the files of <paramref name="replaced"/> — what a tombstone written at <paramref name="at"/>
    /// replaced — must be deleted after the write: whenever it may hold files, unless the cleanup before the
    /// write already covered exactly that state (<paramref name="prior"/>, read before, was the same copy and
    /// was cleaned). 🚨 A <paramref name="replaced"/> that is itself a marked tombstone is a cleanup a
    /// previous attempt still owes, and is always cleaned. Pure.
    /// </summary>
    public static bool CleansAfterTombstone(MailMessageCopy? prior, DateTimeOffset at, MailMessageCopy? replaced)
    {
        if (!MailAttachments.MayHoldFiles(replaced))
            return false;
        var cleanedBefore = prior is not null && prior.CopiedAt <= at && MailAttachments.MayHoldFiles(prior);
        return !cleanedBefore
               || replaced!.Removed
               || replaced.HasAttachments != prior!.HasAttachments
               || !(replaced.Attachments is null
                   ? prior.Attachments is null
                   : prior.Attachments is not null && replaced.Attachments.SequenceEqual(prior.Attachments));
    }

    private IObservable<Unit> DropFiles(string owner, string generation, string messageId, MailMessageCopy? prior)
        => fileStore is not null && MailAttachments.MayHoldFiles(prior)
            ? fileStore.DeleteAll(owner, MailCopyNodeTypes.CopyPath(owner, Provider, generation, messageId))
            : Observable.Return(Unit.Default);
}

/// <summary>A provider's per-account switch over the generic engine: one account per feed today.</summary>
/// <param name="engine">The engine of the provider's copy.</param>
/// <param name="feed">The feed whose accounts are offered.</param>
public sealed class MailFeedAccountCopy(MailCopyEngine engine, IMailCopyFeed feed) : IMailAccountCopy
{
    /// <inheritdoc />
    public string Provider => engine.Provider;

    /// <inheritdoc />
    public IReadOnlyCollection<string> KnownOwners => engine.KnownOwners;

    // 🚨 Each wrapper COMPOSES its cold engine operations at entry, under the caller's identity, and only
    // SUBSCRIBES them after its account check. The engine and the stores read the identity when they are
    // CALLED; composed inside a SelectMany instead, they would read whatever identity the upstream emitted
    // under — a privileged subscriber's, or none after a provider hop — and be judged as that.

    /// <inheritdoc />
    public IObservable<ImmutableList<MailCopyAccount>> Accounts(string owner)
    {
        var isOn = engine.IsOn(owner);
        return feed.Accounts(owner).Take(1).SelectMany(accounts => accounts.Count == 0
            ? Observable.Return(ImmutableList<MailCopyAccount>.Empty)
            : isOn.Select(on => accounts.Select(a => new MailCopyAccount(Provider, a.AccountId, a.Label, on)).ToImmutableList()));
    }

    /// <inheritdoc />
    /// <remarks>Starts a System backfill on the owner's behalf, so the CODE that calls it is judged by the in-mesh
    /// impersonation guard — this assembly calling <see cref="MailCopyEngine.Enable"/> would pass any caller.</remarks>
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<bool> Enable(string owner, string accountId, int windowDays)
        => EnableFrom(Assembly.GetCallingAssembly(), owner, accountId, windowDays, nameof(Enable));

    /// <summary><see cref="Enable"/> on behalf of <paramref name="callerCode"/>, the assembly that called the public surface.</summary>
    internal IObservable<bool> EnableFrom(Assembly callerCode, string owner, string accountId, int windowDays, string surface)
    {
        var enable = engine.EnableFrom(callerCode, owner, windowDays, surface);
        return feed.Accounts(owner).Take(1).SelectMany(accounts => accounts.Any(a => a.AccountId == accountId)
            ? enable.Select(_ => true)
            : Observable.Return(false));
    }

    /// <inheritdoc />
    public IObservable<bool> Disable(string owner, string accountId)
    {
        var source = engine.Source(owner);
        var disable = engine.Disable(owner);
        // Only the copy OF THIS ACCOUNT: a different account id deletes nothing.
        return source.SelectMany(found => found is null || (found.MailboxId is { } bound && bound != accountId)
            ? Observable.Return(false)
            : disable);
    }

    /// <inheritdoc />
    /// <remarks>Starts a System sync on the owner's behalf, so the CODE that calls it is judged by the in-mesh
    /// impersonation guard — this assembly calling <see cref="MailCopyEngine.Kick"/> would pass any caller.</remarks>
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void KickAll(string owner) => engine.KickFrom(Assembly.GetCallingAssembly(), owner, surface: nameof(KickAll));
}
