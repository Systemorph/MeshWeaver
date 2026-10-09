using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What a cached listing is OF: one repository, at one subdirectory, in one format, at one ref.
///
/// <para>🚨 <b>There is no caller in this key, and that is the security property.</b> What is cached
/// is the SOURCE snapshot — the repository's manifests as the registry read them — never a response.
/// The per-caller work (<c>IsGranted</c>, the tier refusals, the pushed-artifact stamp) runs over the
/// cached list on every request, so one instance's catalog can never be served to another. Caching
/// the RESPONSE instead would be #3768's shape: the registry answering with an identity that is not
/// the caller's.</para>
/// </summary>
/// <param name="RepoUrl">The repository URL or local path the source reads.</param>
/// <param name="Subdir">The configured subdirectory, or the empty string.</param>
/// <param name="Format">The source format — a node repo and a package.json repo parse the same tree differently.</param>
/// <param name="GitRef">The git ref the source is read at.</param>
public sealed record PackageListingKey(string RepoUrl, string Subdir, string Format, string GitRef);

/// <summary>
/// The registry's listing cache: one repository read per <see cref="PackageListingKey"/> per freshness
/// window, shared by every request, instead of one per request.
///
/// <para><b>The defect (#4222).</b> <c>GET /api/plugins</c> resolves its sources FRESH on every
/// request (<c>PluginRegistryEndpoints.Sources</c> → <c>PackageSources.FromConfiguration</c> →
/// <c>PackageSources.FromRepo</c> builds a brand-new <c>IPackageSource</c> each time), and each
/// listing then fetches the whole plugins repository from GitHub and parses every manifest in it.
/// Measured from inside two production portals on 2026-08-26: an 8.7&#160;KB document connects in
/// ~0.03&#160;s and takes <b>12–19&#160;s to first byte</b>. The consumer-side rate was measured a
/// month later — the control instance's <c>plugin-registry</c> pipeline logged <b>2,028</b> attempt
/// timeouts over 33.6 days, about 60 a day, every one of them this listing exceeding its 30&#160;s
/// attempt budget. Raising the budget is not the fix; the listing's inputs change at the cadence of
/// a merge, not of a request.</para>
///
/// <para><b>Why a cache is SAFE here even though a response cache would not be.</b> See
/// <see cref="PackageListingKey"/>: the key carries no caller, because the value carries no
/// per-caller decision.</para>
///
/// <para><b>Invalidation, in two layers.</b> The primary signal is the one the registry already
/// receives: a green build of the source repository (<c>PluginUpdateWatcher.OnGreenBuild</c>, driven
/// by the GitHub webhook → <c>Admin/_Build/{owner}.{repo}</c>) calls <see cref="EvictRepo"/>, so the
/// next listing re-reads. The freshness <see cref="Window"/> is the SAFETY NET for a broadcast that
/// never arrived — the same role, and the same justification, as
/// <c>PluginCatalogOptions.ReconcileSafetyNetInterval</c> one layer up. It bounds how stale a cache
/// may be; it is not a bound placed over anything that hangs.</para>
///
/// <para>🚨 <b>A fault is never cached.</b> <see cref="PromiseCache{TKey,TValue}"/> evicts on the
/// terminal <c>OnError</c>, so a transient GitHub failure is retried by the next caller rather than
/// replayed for the life of the pod (#1369). Concurrent first callers still share the ONE fetch,
/// which is the other half of the win: a burst of catalog opens used to be a burst of clones.</para>
///
/// <para><b>Mesh-scoped singleton, never static</b> (<c>Doc/Architecture/NoStaticState</c>) —
/// registered by <c>AddPluginCatalog</c>, so its lifetime is the mesh's and a test mesh cannot
/// inherit another's catalog.</para>
/// </summary>
public sealed class PackageListingCache : IDisposable
{
    /// <summary>Config-bound freshness window, in seconds; <c>0</c> or negative disables caching.</summary>
    public const string WindowSecondsConfigKey = "PluginCatalog:ListingCacheSeconds";

    /// <summary>
    /// Five minutes. Short against the consumer's 30-minute reconcile safety net, so a missed
    /// webhook costs at most one stale reconcile; long enough that a page full of catalog opens
    /// costs one repository read rather than one each.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    // The READS in flight, one per key: concurrent callers share the single read, a fault evicts it
    // (PromiseCache), and a settled read RELEASES its own entry (pair-exact) so the next
    // revalidation can start a fresh one. Nothing settled is kept here — that is `held`.
    private readonly PromiseCache<PackageListingKey, IReadOnlyList<PackageManifest>> reads = new();

    // 🚨 The listing each key last READ, and when that read STARTED (#4963). This is what a request
    // is answered from — including after the window has run out, while the revalidating read is in
    // flight. Before, an expired entry was dropped and the request that found it WAITED for the
    // re-read; measured on the fleet registry 2026-10-09, that wait outlasted the 25 s answer budget
    // on every replica (all five source listings "running 24.9 s" in 123 deadline refusals over 110
    // minutes), so catalog and index requests that landed in a refresh were refused for data the
    // registry already held.
    private readonly ConcurrentDictionary<PackageListingKey, HeldListing> held = new();

    // When each key's newest read STARTED, on a MONOTONIC clock — wall time steps and would forge
    // either an immortal entry or a permanently-expired one. The set of keys is exactly the set of
    // listings this cache has ever read, which is what EvictRepo walks.
    private readonly ConcurrentDictionary<PackageListingKey, long> builtAt = new();

    // Bumped by EvictRepo, so a read that started BEFORE a green build lands as already-stale: its
    // answer predates the merge and must be revalidated, never served as fresh for a whole window.
    private readonly ConcurrentDictionary<PackageListingKey, long> generations = new();

    // 🚨 The multicast connections this cache creates, owned HERE so the cache's own disposal
    // releases them (RootedRxConnectionRatchetGuard). A bare AutoConnect(1) would leave the handle
    // where no teardown can reach it; a pool-queued connect would then run against a closed scope.
    // Each entry's chain is Take(1), so a completed read drops its own handle and this never grows
    // with the number of listings served.
    private readonly CompositeDisposable connections = new();

    // One lane for every connection this registry owns, so its disposal delivers their release
    // terminals in order rather than concurrently (see ReleaseLane).
    private readonly ReleaseLane releaseLane = new();

    private readonly Func<long> ticks;
    private readonly ILogger? logger;

    /// <summary>Creates the cache with the production clock.</summary>
    /// <param name="window">Freshness window; <see cref="TimeSpan.Zero"/> or less disables caching.</param>
    /// <param name="logger">Optional logger.</param>
    public PackageListingCache(TimeSpan window, ILogger? logger = null)
        : this(window, Stopwatch.GetTimestamp, logger)
    {
    }

    /// <summary>Creates the cache with an injected monotonic clock — the seam the window's tests drive.</summary>
    /// <param name="window">Freshness window; <see cref="TimeSpan.Zero"/> or less disables caching.</param>
    /// <param name="ticks">Monotonic tick source, in <see cref="Stopwatch"/> ticks.</param>
    /// <param name="logger">Optional logger.</param>
    public PackageListingCache(TimeSpan window, Func<long> ticks, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        Window = window;
        this.ticks = ticks;
        this.logger = logger;
    }

    /// <summary>How long a listing may be reused before it is read again.</summary>
    public TimeSpan Window { get; }

    /// <summary>True when this cache actually caches — false when the window switched it off.</summary>
    public bool Enabled => Window > TimeSpan.Zero;

    /// <summary>
    /// Read the window from configuration. Absent or malformed ⇒ <see cref="DefaultWindow"/>; zero or
    /// negative ⇒ off. Malformed deliberately does NOT mean off: a typo must not silently restore
    /// the per-request repository read this exists to remove.
    /// </summary>
    /// <param name="configured">The raw configured value, or null.</param>
    /// <returns>The window, or <see cref="TimeSpan.Zero"/> when caching is switched off.</returns>
    public static TimeSpan WindowOf(string? configured)
    {
        // 🚨 `double.TryParse` SUCCEEDS on "NaN" and "Infinity", and TimeSpan.FromSeconds throws on
        // both — inside the DI factory that builds this singleton, which would take the host down
        // over a typo. A value that is not a finite number is a malformed value, and a malformed
        // value falls back to the default. The upper bound is the same rule: a window longer than
        // a day is not a configuration anybody means, and TimeSpan.FromSeconds overflows past
        // ~2.9e8 days.
        if (!double.TryParse(configured, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds)
            || seconds > MaximumWindow.TotalSeconds)
            return DefaultWindow;

        return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>One day — past this a configured value is a typo, not an intention.</summary>
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(1);

    /// <summary>
    /// The listing for <paramref name="key"/>. Inside the window: the held one. Past it (or after a
    /// green build of its repository): STILL the held one, answered at once, while ONE revalidating
    /// read runs behind it and replaces it when it lands. Only a key that has never been read makes
    /// its caller wait — and concurrent first callers share that single read.
    ///
    /// <para>🚨 <b>Why stale-while-revalidate, and why it is not a looser guarantee.</b> The
    /// listing's inputs change at the cadence of a merge, and the freshness window only says how
    /// long a read may be reused before the source is ASKED again — it never meant "a caller must
    /// wait for the source" (#4963). How stale an answer can be is unchanged in kind: the window
    /// plus the duration of one read, which is what a waiting caller got too, minus the wait.</para>
    ///
    /// <para>🚨 <b>A failed revalidation is named, never swallowed into a fresh-looking answer.</b>
    /// The held listing keeps being served (a transient GitHub fault must not turn a catalog that
    /// worked a minute ago into an empty one), the fault is logged with how old the served listing
    /// is, and the entry stays EXPIRED — so the very next request asks the source again.</para>
    /// </summary>
    /// <param name="key">What is being listed.</param>
    /// <param name="produce">Reads the source. Invoked at most once per read.</param>
    /// <returns>The listing — held, or the shared first read.</returns>
    public IObservable<IReadOnlyList<PackageManifest>> Get(
        PackageListingKey key, Func<IObservable<IReadOnlyList<PackageManifest>>> produce)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(produce);

        if (!Enabled)
            return produce();

        return Observable.Defer(() =>
        {
            if (!held.TryGetValue(key, out var current))
                return Read(key, produce, out _);

            // Fresh = read inside the window AND no green build of its repository since the read
            // STARTED. The generation is compared here, at serve time, so a read that lands after an
            // eviction can never be installed as fresh — landing and invalidation need no ordering.
            if (current.Generation == generations.GetValueOrDefault(key) && Elapsed(current.ReadAt) <= Window)
                return Observable.Return(current.Listing);

            Revalidate(key, produce, current);
            return Observable.Return(current.Listing);
        });
    }

    /// <summary>The shared read for <paramref name="key"/>: joined when one is in flight, started
    /// otherwise. It lands in <see cref="held"/> and releases its own in-flight entry when it
    /// settles; a fault is evicted by the promise cache and reaches every waiting subscriber.</summary>
    private IObservable<IReadOnlyList<PackageManifest>> Read(
        PackageListingKey key, Func<IObservable<IReadOnlyList<PackageManifest>>> produce, out bool started)
    {
        var createdHere = false;
        var shared = reads.GetOrAdd(key, k =>
        {
            createdHere = true;
            var startedAt = ticks();
            var generation = generations.GetValueOrDefault(k);
            builtAt[k] = startedAt;
            // 🚨 Replay(1) + an OWNED AutoConnect, not the bare source. The entry is subscribed by
            // every concurrent caller, so a COLD observable would be re-run by each of them and the
            // cache would hold a recipe rather than a result. The connect fires on the first real
            // subscriber; the cache's own Do-decoration deliberately does not count as one.
            return produce().Take(1)
                .Do(listing => Land(k, listing, startedAt, generation))
                .Replay(1)
                .AutoConnectOwnedBy(connections, releaseLane, nameof(PackageListingCache));
        });
        started = createdHere;
        // Released once SETTLED, never on a subscriber's cancellation, and pair-exact — a
        // replacement a later caller (or EvictRepo) installed is never dropped.
        return shared.Do(_ => { }, () => reads.Release(key, shared));
    }

    /// <summary>Starts (or joins) the read that replaces <paramref name="current"/>, owned by this
    /// cache rather than by the request that noticed the expiry.</summary>
    private void Revalidate(
        PackageListingKey key, Func<IObservable<IReadOnlyList<PackageManifest>>> produce, HeldListing current)
    {
        // A revalidation in flight for longer than a whole window has not answered within the time
        // the window allows a listing to be reused, so it is NAMED and a new read is started beside
        // it — the same re-ask the expired-entry path always made, now with no caller waiting on it.
        if (reads.Contains(key)
            && builtAt.TryGetValue(key, out var inFlightSince)
            && Elapsed(inFlightSince) > Window)
        {
            logger?.LogWarning(
                "Plugin listing cache: the read of {Repo} @ {Ref} started {Elapsed} ago has not "
                + "answered within the {Window} window — asking the source again; the listing read "
                + "{Age} ago is served meanwhile.",
                key.RepoUrl, key.GitRef, Elapsed(inFlightSince), Window, Elapsed(current.ReadAt));
            reads.Invalidate(key);
        }

        // 🚨 ONE subscription per revalidation, made by the request that STARTED it. Every other
        // request that finds the entry expired while that read is in flight only answers from the
        // held listing — subscribing each of them to the replay would retain one observer per
        // request for as long as a stalled read stalls, and log its fault once per request.
        var revalidation = Read(key, produce, out var started);
        if (!started)
            return;
        revalidation.Subscribe(
            _ => { },
            exception => logger?.LogWarning(exception,
                "Plugin listing cache: revalidating {Repo} @ {Ref} failed — still serving the listing "
                + "read {Age} ago, and the next request asks the source again.",
                key.RepoUrl, key.GitRef, Elapsed(current.ReadAt)));
    }

    /// <summary>Records a settled read — unless a NEWER read already landed — with the generation
    /// it STARTED under, so a green build that arrived while it was in flight leaves it stale.</summary>
    private void Land(PackageListingKey key, IReadOnlyList<PackageManifest> listing, long startedAt, long generation)
    {
        var landed = new HeldListing(listing, startedAt, generation);
        held.AddOrUpdate(key, landed, (_, existing) => existing.ReadAt > startedAt ? existing : landed);
    }

    /// <summary>
    /// Wraps <paramref name="inner"/> so its <c>ListPackages</c> goes through this cache. The file
    /// fetches are NOT wrapped: those are per-package reads on the install path, they are not
    /// repeated per catalog render, and a stale one would install stale bytes.
    /// </summary>
    /// <param name="inner">The source to wrap.</param>
    /// <param name="repoUrl">The repository URL or path it reads.</param>
    /// <param name="subdir">Its configured subdirectory, or the empty string.</param>
    /// <param name="format">Its format, so two readings of one repository cannot share an entry.</param>
    /// <returns>The wrapped source, or <paramref name="inner"/> unchanged when caching is off.</returns>
    public IPackageSource Wrap(IPackageSource inner, string repoUrl, string subdir, string format)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return Enabled ? new CachedPackageSource(inner, this, repoUrl, subdir ?? "", format ?? "") : inner;
    }

    /// <summary>
    /// Forgets every listing of <paramref name="repoUrl"/> — the green-build signal, so the next
    /// catalog read sees the merge that just landed instead of waiting out the window.
    /// </summary>
    /// <param name="repoUrl">The repository that changed; matched case-insensitively, with any
    /// trailing <c>.git</c> or slash ignored, because the webhook and the configured source spell
    /// the same repository differently.</param>
    /// <returns>How many entries were forgotten.</returns>
    public int EvictRepo(string? repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl))
            return 0;

        var wanted = Normalize(repoUrl);
        var evicted = 0;
        foreach (var key in builtAt.Keys)
        {
            if (!string.Equals(Normalize(key.RepoUrl), wanted, StringComparison.OrdinalIgnoreCase))
                continue;
            // A read in flight now started before the build: forget it, so the next request starts
            // one that can see the merge, and bump the generation so its answer lands as stale.
            generations.AddOrUpdate(key, 1, (_, g) => g + 1);
            reads.Invalidate(key);
            // The held listing stays SERVABLE — answering from it while the re-read runs is the
            // point (#4963) — but its generation is now behind, so the next request revalidates.
            evicted++;
        }

        if (evicted > 0)
            logger?.LogInformation(
                "Plugin listing cache: {Count} listing(s) of {Repo} forgotten after a green build.",
                evicted, repoUrl);
        return evicted;
    }

    /// <summary>True when a listing for <paramref name="key"/> is currently held or being read.</summary>
    /// <param name="key">The key to test.</param>
    public bool Holds(PackageListingKey key) => held.ContainsKey(key) || reads.Contains(key);

    /// <summary>Releases every multicast connection this cache opened.</summary>
    public void Dispose() => connections.Dispose();

    private TimeSpan Elapsed(long since) => Stopwatch.GetElapsedTime(since, ticks());

    /// <summary>A settled listing, when its read started, and the eviction generation it started
    /// under — stale as soon as a green build bumps the key's generation past it.</summary>
    private sealed record HeldListing(IReadOnlyList<PackageManifest> Listing, long ReadAt, long Generation);

    private static string Normalize(string repoUrl)
    {
        var trimmed = repoUrl.TrimEnd('/');
        return trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    /// <summary>
    /// The decorator <see cref="Wrap"/> installs. Listing goes through the cache; everything else is
    /// forwarded untouched, including the path-filtered fetch a remote source overrides.
    /// </summary>
    private sealed class CachedPackageSource(
        IPackageSource inner, PackageListingCache cache, string repoUrl, string subdir, string format)
        : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            cache.Get(
                new PackageListingKey(repoUrl, subdir, format, gitRef),
                () => inner.ListPackages(gitRef));

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            inner.FetchPackageFiles(package, gitRef);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef, IReadOnlyCollection<string>? paths) =>
            inner.FetchPackageFiles(package, gitRef, paths);
    }
}
