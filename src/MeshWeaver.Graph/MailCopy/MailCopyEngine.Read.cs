using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.MailCopy;

// The engine's read side and the copy's half of a local move: what a provider's inbox needs to answer from the
// copy once it is backfilled, and to follow the person's own Delete and Undo. Provider-neutral — a provider
// maps the copies to its own rows.
public sealed partial class MailCopyEngine
{
    /// <summary>How many copies the inbox reads per page while it looks for the rows it needs.</summary>
    public const int ListPage = 200;

    /// <summary>The most pages one inbox answer reads from the copy — 20,000 copies.</summary>
    public const int MaxListPages = 100;

    /// <summary>The most rows one inbox answer asks for.</summary>
    public const int MaxTop = 100;

    /// <summary>How long the inbox waits to learn whether a source is copied before reading it live.</summary>
    public static readonly TimeSpan SourceReadBudget = TimeSpan.FromSeconds(5);

    /// <summary>The most a follow-up of a message holds the next one of the same message back.</summary>
    public static readonly TimeSpan FollowQueueBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The source the inbox reads <paramref name="owner"/>'s rows from, or null to read live: only once the
    /// backfill is DONE and for the account connected NOW — a half-made copy, or one of another account, must not
    /// stand in for the mailbox. Starts a (throttled) sync whenever it is asked (also with the copy off, where the
    /// sync only sweeps leftovers). Answers within <see cref="SourceReadBudget"/>; a failed or slow read answers
    /// null (live), logged. The sync it starts runs as System on the owner's behalf, so the CODE that calls this is
    /// judged by the in-mesh impersonation guard.
    /// </summary>
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<MailCopySource?> ServesFromCopy(string owner)
        => ServesFromCopyFrom(Assembly.GetCallingAssembly(), owner);

    internal IObservable<MailCopySource?> ServesFromCopyFrom(Assembly caller, string owner)
        => ReadStore.Source(owner, Provider)
            .Take(1)
            .DefaultIfEmpty(null)
            .Do(_ => KickFrom(caller, owner))
            .SelectMany(source => source is { Backfilled: true, MailboxId: { } bound }
                ? CurrentAccount(owner).Select(id => id == bound ? source : null)
                : Observable.Return<MailCopySource?>(null))
            // ONE budget for the whole decision — the source read AND the account check.
            .Take(1)
            .Timeout(SourceReadBudget)
            .DefaultIfEmpty(null)
            .Catch((Exception ex) =>
            {
                Log?.LogWarning(ex, "{Provider} copy: whether {Owner}'s mail is copied could not be read; reading it live", Provider, owner);
                return Observable.Return<MailCopySource?>(null);
            });

    /// <summary>The account connected NOW; errors when it cannot be read within <see cref="SourceReadBudget"/> — callers then read live.</summary>
    public IObservable<string> CurrentAccount(string owner)
        => Feed.AccountId(owner).Take(1).Timeout(SourceReadBudget);

    /// <summary>
    /// The owner's stored copy of one message, or null — when the copy is off, has not reached it, is of another
    /// account, or the read fails or is slow (logged): the live read is always the fallback, never a refusal.
    /// </summary>
    public IObservable<MailMessageCopy?> StoredCopy(string owner, string messageId)
    {
        // 🚨 The caller is read ONCE, here, and restored around every later store call: those calls are composed
        // when an upstream observable EMITS, under whatever identity it emitted under, and the stores judge the
        // identity they are called with.
        var who = Caller();
        return ReadStore.Source(owner, Provider)
            .SelectMany(source => source is not { MailboxId: { } bound }
                ? Observable.Return<MailMessageCopy?>(null)
                : CurrentAccount(owner).SelectMany(id => id != bound
                    ? Observable.Return<MailMessageCopy?>(null)
                    : AsCaller(who, () => ReadStore.Read(owner, Provider, source.Generation, messageId))))
            .Take(1)
            .Timeout(SourceReadBudget)
            .DefaultIfEmpty(null)
            .Catch((Exception ex) =>
            {
                Log?.LogWarning(ex, "{Provider} copy: the stored copy of a message of {Owner} could not be read; reading it live", Provider, owner);
                return Observable.Return<MailMessageCopy?>(null);
            });
    }

    // The identity of the code that CALLED a read — the request's, else the circuit's.
    private AccessContext? Caller() => Access is null ? null : Access.Context ?? Access.CircuitContext;

    // Calls a store as <paramref name="who"/> (restored, never elevated): the store reads the identity it is called with.
    private IObservable<T> AsCaller<T>(AccessContext? who, Func<IObservable<T>> call)
    {
        using (Access?.SwitchAccessContext(who))
            return call();
    }

    /// <summary>
    /// The inbox as <paramref name="source"/>'s copy holds it: the newest (or oldest) <paramref name="top"/>
    /// copies <paramref name="accept"/> keeps, or — with <paramref name="search"/> — those whose subject, sender,
    /// preview or BODY contain every word of it. Cold; emits once.
    ///
    /// <para>🚨 <b>Matched BEFORE the bound.</b> The copy is paged (<see cref="ListPage"/> at a time, in receive
    /// order) until <paramref name="top"/> copies MATCH or the copy is exhausted — never cut at a fixed count
    /// first. Bounded by <see cref="MaxListPages"/>. Re-checked AFTER the pages: a copy rebuilt or switched off
    /// meanwhile makes the answer an error, and the caller reads live.</para>
    /// </summary>
    public IObservable<ImmutableList<MailMessageCopy>> ReadInbox(MailCopySource source, string owner, int top, string? search,
        Func<MailMessageCopy, bool>? accept = null, bool oldestFirst = false, int pageSize = ListPage)
    {
        var wanted = Math.Clamp(top, 1, MaxTop);
        var terms = SearchTerms(search);
        var who = Caller();
        bool Keeps(MailMessageCopy copy) => Matches(copy, terms) && (accept?.Invoke(copy) ?? true);
        // The page size is clamped to what the store can answer: at least two (the cursor is inclusive, so a page of
        // one is the boundary row again, for ever) and at most the store's own cap (a larger request is cut by the
        // store and would read as the end).
        return Pages(owner, source.Generation, null, ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal),
                ImmutableList<MailMessageCopy>.Empty, wanted, Keeps, oldestFirst, Math.Clamp(pageSize, 2, MeshMailCopyStore.MaxPage), 1, who)
            .SelectMany(found => AsCaller(who, () => ReadStore.Source(owner, Provider)).Select(now => now is { Backfilled: true } && now.Generation == source.Generation
                ? found
                : throw new InvalidOperationException("The copy changed while it was read; reading live instead.")));
    }

    private IObservable<ImmutableList<MailMessageCopy>> Pages(
        string owner, string generation, long? from, ImmutableHashSet<string> seen, ImmutableList<MailMessageCopy> found,
        int wanted, Func<MailMessageCopy, bool> keeps, bool oldestFirst, int pageSize, int page, AccessContext? who)
        => AsCaller(who, () => ReadStore.ReadPage(owner, Provider, generation, from, pageSize, oldestFirst)).SelectMany(copies =>
        {
            // The page boundary is inclusive (one instant may straddle it): drop what was seen.
            var fresh = copies.Where(copy => !seen.Contains(copy.MessageId)).ToImmutableList();
            var kept = found.AddRange(fresh.Where(keeps)).Take(wanted).ToImmutableList();
            var exhausted = copies.Count < pageSize || fresh.Count == 0;
            return kept.Count >= wanted || exhausted || page >= MaxListPages
                ? Observable.Return(kept)
                : Pages(owner, generation, fresh[^1].OrderKey, seen.Union(fresh.Select(copy => copy.MessageId)),
                    kept, wanted, keeps, oldestFirst, pageSize, page + 1, who);
        });

    /// <summary>The words of a search, lowercased; none for no search. Pure.</summary>
    public static ImmutableList<string> SearchTerms(string? search)
        => string.IsNullOrWhiteSpace(search)
            ? []
            : search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(term => term.Trim('"').ToLowerInvariant())
                .Where(term => term.Length > 0)
                .ToImmutableList();

    /// <summary>Whether <paramref name="copy"/> contains every term in its subject, sender, preview or body. Pure.</summary>
    public static bool Matches(MailMessageCopy copy, IReadOnlyCollection<string> terms)
    {
        if (terms.Count == 0)
            return true;
        var text = string.Join('\n', copy.Subject, copy.From, copy.FromAddress, copy.Preview, copy.Body).ToLowerInvariant();
        return terms.All(term => text.Contains(term, StringComparison.Ordinal));
    }

    // The follow-ups in flight, per (owner, message): each waits for the previous one, so a Delete's follow-up and
    // the Undo after it apply in the order they were asked for.
    private ImmutableDictionary<string, IObservable<bool>> followTails =
        ImmutableDictionary<string, IObservable<bool>>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>
    /// The copy's half of an inbox Delete (<paramref name="removed"/>) or Undo, once every earlier follow-up of the
    /// same message in this process has finished:
    /// <list type="bullet">
    /// <item><description>Delete removes the message's copy — unless the copy reflects something that happened
    /// AFTER the Delete was asked for (<see cref="MailMessageCopy.CopiedAt"/>: an Undo asked later, or a sync
    /// since), which a late removal must never undo.</description></item>
    /// <item><description>Undo re-reads the message from the provider (a feed that can) and writes it back — only
    /// when it was received inside the copy's window, as the sync would — and removes it again unless the copy is
    /// STILL on in the same generation afterwards, so an Undo never leaves a copy behind a switch-off.</description></item>
    /// </list>
    /// Only while the owner keeps a copy. Emits whether a copy was written or removed. Hot: it starts at once, so
    /// its order against the next follow-up is the order of the calls. The writes are System on the owner's
    /// behalf, so the CODE that calls this is judged by the in-mesh impersonation guard.
    /// </summary>
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<bool> FollowMove(string owner, string messageId, bool removed, DateTimeOffset? askedAt = null)
        => FollowMoveFrom(Assembly.GetCallingAssembly(), owner, messageId, removed, askedAt);

    internal IObservable<bool> FollowMoveFrom(Assembly caller, string owner, string messageId, bool removed, DateTimeOffset? askedAt)
    {
        CheckCaller(caller, nameof(FollowMove), owner);
        var key = owner + "\n" + messageId;
        // The time the person ASKED — taken by the caller BEFORE it dispatched the provider move, so a response that
        // arrives late cannot make an earlier move look newer than a later one.
        var asked = askedAt ?? Now;
        var done = new AsyncSubject<bool>();
        // What the NEXT follow-up of this message waits for: this one, but never longer than FollowQueueBudget.
        IObservable<bool> tail = done.Timeout(FollowQueueBudget).Catch((Exception _) => Observable.Return(false));
        IObservable<bool> previous = Observable.Return(false);
        ImmutableInterlocked.AddOrUpdate(ref followTails, key, _ => tail, (_, last) =>
        {
            previous = last;
            return tail;
        });
        previous
            .Catch((Exception _) => Observable.Return(false))
            .IgnoreElements()
            .Concat(Observable.Defer(() => Follow(owner, messageId, removed, asked)))
            .Take(1)
            .DefaultIfEmpty(false)
            // Only THIS follow-up's tail is dropped — a later one queued behind it keeps its own.
            .Finally(() => ImmutableInterlocked.Update(ref followTails, static (tails, entry) =>
                tails.TryGetValue(entry.key, out var last) && ReferenceEquals(last, entry.tail) ? tails.Remove(entry.key) : tails,
                (key, tail)))
            .Subscribe(done.OnNext, done.OnError, done.OnCompleted);
        return done;
    }

    /// <summary>Now, on the engine's clock — what a move is stamped with when it is ASKED.</summary>
    public DateTimeOffset Now => Clock.GetUtcNow();

    private IObservable<bool> Follow(string owner, string messageId, bool removed, DateTimeOffset asked)
        => copies.Source(owner, Provider).Take(1).Timeout(SourceReadBudget).SelectMany(source => source is null
            ? Observable.Return(false)
            : removed
                // A tombstone dated when the Delete was ASKED, decided at the owner: it replaces only an older copy.
                ? Tombstoned(owner, source.Generation, messageId, asked)
                : Restored(owner, source, messageId, asked));

    private IObservable<bool> Restored(string owner, MailCopySource source, string messageId, DateTimeOffset asked)
        => !Feed.CanReread
            ? Observable.Return(false)
            // Read through the account whose copy this IS: after a reconnect to another account (before the next
            // sync rebuilds), an Undo copies nothing.
            : Feed.AccountId(owner).Take(1).Timeout(SourceReadBudget).SelectMany(id => id != source.MailboxId
                ? Observable.Return<MailMessageCopy?>(null)
                : Feed.Reread(owner, messageId).Take(1).DefaultIfEmpty(null))
                .SelectMany(message => message is null || !InWindow(message, source.WindowStart)
                    ? Observable.Return(false)
                    : Feed.Attachments(owner).Take(1).SelectMany(files =>
                        // Stamped with when the Undo was ASKED, not when it got written, and only over an OLDER
                        // copy: a sync's newer state stays.
                        Copied(owner, source, message with { Provider = Provider, Generation = source.Generation, CopiedAt = asked }, files))
                        .SelectMany(written => !written ? Observable.Return(true) : copies.Source(owner, Provider)
                            .Take(1)
                            .Timeout(SourceReadBudget)
                            .Select(now => now is not null && now.Generation == source.Generation)
                            .DefaultIfEmpty(false)
                            // A re-check that cannot be read proves nothing: keep what the Undo wrote (a generation
                            // dropped later removes it whole) rather than clean up on a guess.
                            .Catch((Exception _) => Observable.Return(true))
                            .SelectMany(stillOn => stillOn
                                ? Observable.Return(true)
                                // The copy was switched off (or rebuilt) while the Undo waited on the provider: its
                                // generation is obsolete, so it is removed whole — no tombstone is left standing
                                // in a generation nobody reads.
                                : Lost(owner, source).Select(_ => false))));
}
