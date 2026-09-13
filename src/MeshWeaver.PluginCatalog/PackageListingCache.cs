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

    private readonly PromiseCache<PackageListingKey, IReadOnlyList<PackageManifest>> promises = new();

    // When each stored entry's factory ran, on a MONOTONIC clock — wall time steps and would forge
    // either an immortal entry or a permanently-expired one.
    private readonly ConcurrentDictionary<PackageListingKey, long> builtAt = new();

    // 🚨 The multicast connections this cache creates, owned HERE so the cache's own disposal
    // releases them (RootedRxConnectionRatchetGuard). A bare AutoConnect(1) would leave the handle
    // where no teardown can reach it; a pool-queued connect would then run against a closed scope.
    // Each entry's chain is Take(1), so a completed read drops its own handle and this never grows
    // with the number of listings served.
    private readonly CompositeDisposable connections = new();

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
    public static TimeSpan WindowOf(string? configured) =>
        double.TryParse(configured, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds)
            : DefaultWindow;

    /// <summary>
    /// The listing for <paramref name="key"/> — the cached one when it is inside the window, a fresh
    /// read otherwise. Concurrent callers share the single read.
    /// </summary>
    /// <param name="key">What is being listed.</param>
    /// <param name="produce">Reads the source. Invoked at most once per stored entry.</param>
    /// <returns>The shared listing.</returns>
    public IObservable<IReadOnlyList<PackageManifest>> Get(
        PackageListingKey key, Func<IObservable<IReadOnlyList<PackageManifest>>> produce)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(produce);

        if (!Enabled)
            return produce();

        if (builtAt.TryGetValue(key, out var at) && Elapsed(at) > Window)
        {
            promises.Invalidate(key);
            builtAt.TryRemove(key, out _);
        }

        return promises.GetOrAdd(key, k =>
        {
            builtAt[k] = ticks();
            // 🚨 Replay(1) + an OWNED AutoConnect, not the bare source. A PromiseCache entry is
            // subscribed by every later caller, so a COLD observable would be re-run by each of them
            // and the cache would hold a recipe rather than a result — caching nothing while looking
            // like it worked. The connect fires on the first real subscriber; the cache's own
            // Do-decoration deliberately does not count as one.
            return produce().Take(1).Replay(1)
                .AutoConnectOwnedBy(connections, nameof(PackageListingCache));
        });
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
            promises.Invalidate(key);
            builtAt.TryRemove(key, out _);
            evicted++;
        }

        if (evicted > 0)
            logger?.LogInformation(
                "Plugin listing cache: {Count} listing(s) of {Repo} forgotten after a green build.",
                evicted, repoUrl);
        return evicted;
    }

    /// <summary>True when a listing for <paramref name="key"/> is currently held.</summary>
    /// <param name="key">The key to test.</param>
    public bool Holds(PackageListingKey key) => promises.Contains(key);

    /// <summary>Releases every multicast connection this cache opened.</summary>
    public void Dispose() => connections.Dispose();

    private TimeSpan Elapsed(long since) => Stopwatch.GetElapsedTime(since, ticks());

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
