using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Linq;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// The sanctioned promise-cache: work that must run <b>at most once</b> and then be observed by
/// many — schema provisioning, a connect handshake, a container-exists probe — held as the
/// <see cref="IObservable{T}"/> it produced, keyed, and <b>evicted when it faults</b>.
///
/// <para><b>Why this type exists instead of a bare
/// <c>ConcurrentDictionary&lt;TKey, IObservable&lt;TValue&gt;&gt;</c>.</b>
/// <see cref="IoPoolExtensions.Run{T}"/> pumps its leaf into a
/// <see cref="System.Reactive.Subjects.ReplaySubject{T}"/>, and a <c>ReplaySubject</c> latches
/// <i>terminals</i> — <c>OnError</c> included. A bare dictionary therefore turns ONE transient
/// fault into a PERMANENT one: the cached observable replays that same exception to every later
/// subscriber, for the life of the process, and nothing ever re-attempts.
/// <c>Replay(1).AutoConnect(1)</c> and <c>Replay(1).RefCount()</c> behave identically — one
/// already-terminated subject sits behind the connectable.</para>
///
/// <para>That is not hypothetical. It made a partition permanently un-provisionable after a single
/// connect blip — every later write to it <c>42P01</c>-ing until the pod was restarted — and it
/// made <c>nodetype-sources:Edu/Module</c> permanently unreadable after one slow Postgres
/// handshake (#1316, #1369). It kept re-appearing because the <i>recipe</i> was the defect, so the
/// recipe now owns the cure.</para>
///
/// <para><b>The contract.</b></para>
/// <list type="bullet">
///   <item><b>Success is cached, failure is evicted.</b> A retry is a genuinely NEW attempt —
///     never a replay of the old terminal.</item>
///   <item><b>This is not a retry loop, a timer, or a poller.</b> Eviction means only "the next
///     caller who asks will try again". The cache never re-attempts on its own — no watchdog, no
///     backoff, no resubscribe. (A retry watchdog amplifying a mishandled error into a resubscribe
///     storm is precisely the 2026-06-08 production outage.)</item>
///   <item><b>The caller still sees the error.</b> Eviction does not swallow the fault: the
///     subscriber that hit it gets it, unchanged. The <i>cache</i> merely stops serving it to
///     everyone who comes after.</item>
///   <item><b>Eviction is pair-exact.</b> Several subscribers can be attached when the fault
///     arrives, and by the time the last of them reacts a fresh entry may already be in flight.
///     Removing by key alone would drop that healthy replacement; removing the exact
///     (key, entry) pair cannot.</item>
///   <item><b>An in-flight entry is never evicted.</b> Eviction is driven by the terminal
///     <c>OnError</c>, so concurrent callers keep sharing the single attempt until it resolves —
///     which is the whole point of the promise. A caller that subscribes in the instant between
///     the fault and the removal observes that same fault; it was concurrent with the failing
///     attempt, so that is the correct answer for it.</item>
///   <item><b>The factory runs at most once per stored entry.</b> <c>ConcurrentDictionary</c>'s
///     own <c>GetOrAdd</c> may invoke a value-factory several times under contention and discard
///     all but one winner — and with an EAGER factory (<see cref="IoPoolExtensions.Run{T}"/>
///     schedules its work at construction, not at subscribe) every discarded call would still have
///     fired a real round-trip. The <see cref="Lazy{T}"/> inside each entry closes that: only the
///     entry that WON the insert is ever forced.</item>
/// </list>
///
/// <para><b>Never static.</b> Hold this as an instance field on a mesh-scoped singleton so its
/// lifetime is the mesh's — see <c>Doc/Architecture/NoStaticState.md</c>.</para>
///
/// <para>Full recipe: <c>Doc/Architecture/ControlledIoPooling.md</c> →
/// "Promise-cache for idempotent one-shots".</para>
/// </summary>
/// <typeparam name="TKey">Cache key — a schema name, a URL, a collection name.</typeparam>
/// <typeparam name="TValue">What the one-shot produces.</typeparam>
public sealed class PromiseCache<TKey, TValue>
    where TKey : notnull
{
    // The Lazy is wrapped in a holder so the fault callback can capture the DICTIONARY VALUE
    // itself (the holder) before it is stored, which is what makes the removal pair-exact.
    private sealed class Entry
    {
        public Lazy<IObservable<TValue>> Promise = null!;
    }

    private readonly ConcurrentDictionary<TKey, Entry> entries;

    /// <summary>Creates a promise-cache with the default key comparer.</summary>
    public PromiseCache()
        : this(null)
    {
    }

    /// <summary>
    /// Creates a promise-cache with an explicit key comparer — e.g.
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> for a Postgres schema name.
    /// </summary>
    /// <param name="comparer">Key comparer, or null for the default.</param>
    public PromiseCache(IEqualityComparer<TKey>? comparer)
        => entries = new ConcurrentDictionary<TKey, Entry>(comparer ?? EqualityComparer<TKey>.Default);

    /// <summary>
    /// The cached promise for <paramref name="key"/>, built with <paramref name="factory"/> on
    /// first use. Concurrent first callers share one build and one run; a fault evicts the entry
    /// so the NEXT caller builds a fresh one.
    /// </summary>
    /// <param name="key">Cache key.</param>
    /// <param name="factory">
    /// Builds the promise — typically <c>pool.Run(ct => …Async(ct))</c>. Invoked at most once per
    /// stored entry, under that entry's <see cref="Lazy{T}"/>, so it must not re-enter this cache
    /// for the same key.
    /// </param>
    /// <returns>The shared promise. Subscribe as usual — the fault handling is already attached.</returns>
    public IObservable<TValue> GetOrAdd(TKey key, Func<TKey, IObservable<TValue>> factory)
        => entries.GetOrAdd(key, static (k, s) => s.Self.CreateEntry(k, s.Factory), (Self: this, Factory: factory))
            .Promise.Value;

    /// <summary>
    /// Forgets <paramref name="key"/> so the next <see cref="GetOrAdd"/> builds a fresh promise.
    /// For a real domain invalidation — the partition was dropped, the collection unregistered —
    /// <b>not</b> for test isolation: this cache dies with its mesh-scoped owner, so a test needs
    /// no reset (see <c>Doc/Architecture/NoStaticState.md</c>).
    /// </summary>
    /// <param name="key">Key to forget.</param>
    /// <returns>True when an entry was present and removed.</returns>
    public bool Invalidate(TKey key) => entries.TryRemove(key, out _);

    /// <summary>
    /// Atomically removes <paramref name="key"/> and hands back the promise it held. Exactly one
    /// concurrent caller can win, which is what a teardown wants: take the connection/handle if it
    /// was ever opened, and be the only one that closes it.
    /// </summary>
    /// <param name="key">Key to take.</param>
    /// <param name="promise">The promise that was cached, when this caller won.</param>
    /// <returns>True when this caller took the promise.</returns>
    public bool TryTake(TKey key, [NotNullWhen(true)] out IObservable<TValue>? promise)
    {
        if (entries.TryRemove(key, out var entry))
        {
            promise = entry.Promise.Value;
            return true;
        }

        promise = null;
        return false;
    }

    /// <summary>True when <paramref name="key"/> currently holds a promise (settled or in flight).</summary>
    /// <param name="key">Key to test.</param>
    public bool Contains(TKey key) => entries.ContainsKey(key);

    /// <summary>
    /// The promise for <paramref name="key"/> <b>without</b> creating one — for teardown paths
    /// that must act on a connection/handle only if it was ever opened.
    /// </summary>
    /// <param name="key">Key to look up.</param>
    /// <param name="promise">The cached promise, when present.</param>
    /// <returns>True when a promise was present.</returns>
    public bool TryGet(TKey key, [NotNullWhen(true)] out IObservable<TValue>? promise)
    {
        if (entries.TryGetValue(key, out var entry))
        {
            promise = entry.Promise.Value;
            return true;
        }

        promise = null;
        return false;
    }

    private Entry CreateEntry(TKey key, Func<TKey, IObservable<TValue>> factory)
    {
        var entry = new Entry();
        entry.Promise = new Lazy<IObservable<TValue>>(
            // 🚨 Do, never a bookkeeping Subscribe. Subscribing here would be the FIRST subscriber
            // of an AutoConnect(1) / RefCount chain and would connect it — running work nobody
            // asked for and, for a live feed, opening an upstream nothing tears down. Do only
            // decorates: it observes the terminal of whoever actually subscribes.
            () => factory(key).Do(_ => { }, _ => Evict(key, entry)),
            LazyThreadSafetyMode.ExecutionAndPublication);
        return entry;
    }

    // Pair-exact: remove THIS entry, never "whatever is under the key now". By the time a fault
    // propagates, a later caller may already have installed a healthy replacement — dropping that
    // would trade a permanent fault for permanent duplicate work.
    private void Evict(TKey key, Entry faulted)
        => entries.TryRemove(new KeyValuePair<TKey, Entry>(key, faulted));
}

/// <summary>
/// The single-slot <see cref="PromiseCache{TKey,TValue}"/> — one process-wide-per-owner one-shot
/// with no key: a connect handshake, a container-ready probe, a collection's initial load.
///
/// <para>Same contract as the keyed cache, and it exists for the same reason: the hand-rolled
/// spellings it replaces (<c>_field ??= pool.Run(…)</c> under a <c>lock</c>, or an
/// <c>Interlocked.CompareExchange</c>, or a <see cref="Lazy{T}"/>) all cached the FAULT as
/// eagerly as the success, so one transient failure disabled the feature until the pod was
/// restarted. Hold it as an instance field, never static.</para>
/// </summary>
/// <typeparam name="TValue">What the one-shot produces.</typeparam>
public sealed class PromiseSlot<TValue>
{
    private readonly PromiseCache<byte, TValue> cache = new();

    /// <summary>
    /// The cached promise, built with <paramref name="factory"/> on first use. Concurrent first
    /// callers share one run; a fault evicts it so the next caller starts a fresh attempt.
    /// </summary>
    /// <param name="factory">Builds the promise — typically <c>pool.Run(ct => …Async(ct))</c>.</param>
    /// <returns>The shared promise.</returns>
    public IObservable<TValue> GetOrCreate(Func<IObservable<TValue>> factory)
        => cache.GetOrAdd(0, _ => factory());

    /// <summary>
    /// The promise <b>without</b> creating one — for teardown paths that must act on a
    /// connection/handle only if it was ever opened.
    /// </summary>
    /// <param name="promise">The cached promise, when present.</param>
    /// <returns>True when a promise was present.</returns>
    public bool TryGet([NotNullWhen(true)] out IObservable<TValue>? promise)
        => cache.TryGet(0, out promise);

    /// <summary>Forgets the promise so the next <see cref="GetOrCreate"/> builds a fresh one.</summary>
    /// <returns>True when a promise was present and removed.</returns>
    public bool Invalidate() => cache.Invalidate(0);

    /// <summary>
    /// Atomically takes the promise and clears the slot — exactly one concurrent caller wins, so a
    /// teardown closes what it opened exactly once.
    /// </summary>
    /// <param name="promise">The promise that was cached, when this caller won.</param>
    /// <returns>True when this caller took the promise.</returns>
    public bool TryTake([NotNullWhen(true)] out IObservable<TValue>? promise)
        => cache.TryTake(0, out promise);
}
