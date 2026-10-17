using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using NuGet.Common;
using MsLogging = Microsoft.Extensions.Logging;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;

namespace MeshWeaver.NuGet;

/// <summary>
/// Resolves NuGet packages to assembly paths using pure NuGet client libraries.
/// No MSBuild, no dotnet CLI, no SDK — safe to run on a runtime-only container.
/// Resolution is OFFLINE-FIRST (#6863): a package identity already installed — complete —
/// in the global-packages folder is resolved from its own <c>.nuspec</c>, and a feed is asked
/// only for an identity the folder cannot answer. A floating or ranged root request still asks
/// the feed which version to take; an exact version never does.
///
/// <para>The resolution is REACTIVE (<see cref="Resolve"/>). The NuGet SDK is Task-based, so its
/// calls are one leaf run through the Http <see cref="IIoPool"/>; one resolution per inputs is
/// shared through an instance <see cref="PromiseCache{TKey,TValue}"/>, which keeps a success and
/// evicts a failure; and every resolution is bounded by <see cref="DefaultResolveBudget"/>, which
/// cancels the leaf itself, not only a caller's wait.</para>
/// </summary>
public sealed class NuGetAssemblyResolver : INuGetAssemblyResolver, IDisposable
{
    private static readonly NuGetFramework DefaultFramework = NuGetFramework.Parse("net10.0");

    /// <summary>
    /// The wall-clock budget of ONE resolution — every feed call, download and extraction it makes.
    /// NuGet's own HTTP client bounds a single request at 100 s and retries it, so a feed that accepts
    /// a connection and never answers held a resolution for many minutes; and the shared resolution
    /// ignored every caller's token, so a caller's bound only stopped its own WAIT while the work, and
    /// the cache entry every later caller joined, ran on. Past this budget the leaf is cancelled and
    /// the resolution fails with a <see cref="TimeoutException"/> naming the packages and the feeds. It
    /// sits below the compile pipeline's own bound on a compile (five minutes), so the resolver fails
    /// first and by name.
    /// </summary>
    public static readonly TimeSpan DefaultResolveBudget = TimeSpan.FromMinutes(3);

    private readonly PromiseCache<string, ResolvedPackageSet> _resolutions = new();
    // Owns every shared resolution's connection, so disposing the resolver (the DI singleton dies with
    // its mesh) releases them, and terminates any subscriber still attached, on the release lane.
    private readonly CompositeDisposable _connections = new();
    // Owns every in-flight persistent-cache save, so none outlives the resolver.
    private readonly CompositeDisposable _saves = new();
    private readonly IIoPool _cachePool;
    private readonly ReleaseLane _releaseLane;
    private readonly IIoPool _pool;
    private readonly TimeSpan _resolveBudget;
    private readonly SourceCacheContext _sourceCache = new();
    private readonly MsLogging.ILogger<NuGetAssemblyResolver> logger;
    private readonly ISettings _settings;
    private readonly string? _globalPackagesFolder;
    private readonly NuGetLogger _nugetLogger;
    private readonly INuGetPackageCache _packageCache;

    /// <summary>
    /// TEST-ONLY: a resolver reading the NuGet settings found from the application directory upward,
    /// running its leaves on the unbounded fallback pool. Internal on purpose: production builds the
    /// resolver through <see cref="NuGetServiceCollectionExtensions.AddNuGetResolver"/>, on the mesh's
    /// Http pool, and must never be able to fall back to this one silently.
    /// </summary>
    /// <param name="logger">Logger for resolution and install outcomes.</param>
    /// <param name="packageCache">Optional persistent cache hydrated before a feed download.</param>
    internal NuGetAssemblyResolver(
        MsLogging.ILogger<NuGetAssemblyResolver> logger,
        INuGetPackageCache? packageCache = null)
        : this(logger, DefaultSettings(), null, packageCache)
    {
    }

    /// <summary>
    /// Creates a resolver reading the NuGet settings found from the application directory upward,
    /// running its NuGet SDK leaf on the mesh's <see cref="IoPoolNames.Http"/> pool.
    /// </summary>
    /// <param name="logger">Logger for resolution and install outcomes.</param>
    /// <param name="ioPools">The mesh's pools; the resolution runs on the Http lane.</param>
    /// <param name="packageCache">Optional persistent cache hydrated before a feed download.</param>
    /// <param name="releaseLane">The mesh's release lane; <c>null</c> gives the resolver its own.</param>
    public NuGetAssemblyResolver(
        MsLogging.ILogger<NuGetAssemblyResolver> logger,
        IoPoolRegistry ioPools,
        INuGetPackageCache? packageCache = null,
        ReleaseLane? releaseLane = null)
        : this(logger, DefaultSettings(), null, packageCache,
            pool: ioPools.Get(IoPoolNames.Http), releaseLane: releaseLane,
            cachePool: ioPools.Get(IoPoolNames.PackageCache))
    {
    }

    /// <summary>
    /// Creates a resolver outside a mesh — a command-line bake, a build tool — on the pool the
    /// caller NAMES. There is no default: a process without a mesh chooses its pool explicitly
    /// (<see cref="IoPool.Unbounded"/> included), so nothing ever lands on one by omission.
    /// </summary>
    /// <param name="logger">Logger for resolution and install outcomes.</param>
    /// <param name="pool">The pool the NuGet SDK leaf runs on.</param>
    /// <param name="packageCache">Optional persistent cache hydrated before a feed download.</param>
    public NuGetAssemblyResolver(
        MsLogging.ILogger<NuGetAssemblyResolver> logger,
        IIoPool pool,
        INuGetPackageCache? packageCache = null)
        : this(logger, DefaultSettings(), null, packageCache, pool: pool)
    {
    }

    // Anchor settings discovery to the ASSEMBLY directory, NOT the ambient CWD. With
    // root: null, NuGet loads ONLY the user/machine-wide configs and does NOT walk a
    // directory tree — so the repo/app nuget.config (which declares the `mesh-local`
    // feed + the packageSourceMapping that pins MeshWeaver.*/Memex.* to it) is never
    // seen. A version-LESS `#r "nuget:MeshWeaver.BusinessRules.Generator"` then resolves
    // off nuget.org, where the stale 3.0.0-preview1 ships analyzers/ only (no lib/<tfm>) —
    // the generator never loads and scope nodes compile but generate nothing (PensionFund
    // renders empty; NuGetAssemblyResolverTest fails the lib/ assertion). Passing the
    // assembly dir as root makes NuGet walk UP the tree to find it: in tests bin/.../net10.0
    // → repo root nuget.config; in the published container the app dir, where the csproj
    // ships nuget.config NEXT TO the baked dist/packages feed. CWD is whatever launched the
    // process and can't be relied on; the assembly location can.
    private static ISettings DefaultSettings() => Settings.LoadDefaultSettings(root: AppContext.BaseDirectory);

    /// <summary>
    /// Creates a resolver over explicit NuGet <paramref name="settings"/> (the feeds and the source
    /// mapping) and, optionally, an explicit <paramref name="globalPackagesFolder"/>. Used where the
    /// caller owns the configuration: tests pin a private package folder and an unreachable feed
    /// this way. The folder is a parameter rather than a setting because <c>NUGET_PACKAGES</c> in
    /// the environment overrides any configured <c>globalPackagesFolder</c>.
    /// </summary>
    /// <param name="logger">Logger for resolution and install outcomes.</param>
    /// <param name="settings">The NuGet settings naming the feeds.</param>
    /// <param name="globalPackagesFolder">The package folder to read and install into; <c>null</c> takes it from the settings.</param>
    /// <param name="packageCache">Optional persistent cache hydrated before a feed download.</param>
    /// <param name="resolveBudget">The budget of one resolution; <c>null</c> takes <see cref="DefaultResolveBudget"/>.</param>
    /// <param name="pool">The pool the NuGet SDK leaf runs on; <c>null</c> takes <see cref="IoPool.Unbounded"/>.</param>
    /// <param name="releaseLane">The lane released connections terminate on; <c>null</c> gives the resolver its own.</param>
    /// <param name="cachePool">The lane persistent-cache saves run on — never the resolution's pool; <c>null</c> takes <see cref="IoPool.Unbounded"/>.</param>
    internal NuGetAssemblyResolver(
        MsLogging.ILogger<NuGetAssemblyResolver> logger,
        ISettings settings,
        string? globalPackagesFolder = null,
        INuGetPackageCache? packageCache = null,
        TimeSpan? resolveBudget = null,
        IIoPool? pool = null,
        ReleaseLane? releaseLane = null,
        IIoPool? cachePool = null)
    {
        _cachePool = cachePool ?? IoPool.Unbounded;
        _releaseLane = releaseLane ?? new ReleaseLane();
        this.logger = logger;
        _settings = settings;
        _globalPackagesFolder = globalPackagesFolder;
        _resolveBudget = resolveBudget ?? DefaultResolveBudget;
        _pool = pool ?? IoPool.Unbounded;
        _nugetLogger = new NuGetLogger(logger);
        _packageCache = packageCache ?? NullNuGetPackageCache.Instance;
    }

    /// <summary>
    /// Resolves the <paramref name="requested"/> packages (and their transitive dependencies) to
    /// assembly paths. One resolution per (packages, framework) is shared by every subscriber and
    /// kept once it succeeds; a failed one is evicted as it settles, so the next subscriber starts a
    /// new attempt. A subscriber that unsubscribes stops only its own wait — the shared resolution
    /// runs on for the others and ends at <see cref="DefaultResolveBudget"/> at the latest, which
    /// cancels its leaf. A failure is logged once, whether or not anyone is still subscribed.
    /// </summary>
    /// <param name="requested">The package references to resolve.</param>
    /// <param name="targetFramework">The target framework to resolve assets for; defaults to <c>net10.0</c> when <c>null</c>.</param>
    /// <returns>Emits one <c>ResolvedPackageSet</c> and completes, or fails naming the packages.</returns>
    public IObservable<ResolvedPackageSet> Resolve(
        IReadOnlyCollection<NuGetPackageReference> requested,
        NuGetFramework? targetFramework = null)
    {
        if (requested.Count == 0) return Observable.Return(ResolvedPackageSet.Empty);
        var framework = targetFramework ?? DefaultFramework;
        var key = BuildCacheKey(requested, framework);
        ImmutableArray<NuGetPackageReference> inputs = [.. requested];
        // Deferred so EVERY subscription reads the cache anew: one that arrives after a failure was
        // evicted builds a new attempt instead of replaying the old terminal.
        return Observable.Defer(() => _resolutions.GetOrAdd(key, k => SharedResolution(k, inputs, framework)));
    }

    /// <summary>
    /// The Task bridge for callers whose own signature is Task-based. It waits on
    /// <see cref="Resolve"/> through the sanctioned bridge; <paramref name="ct"/> cancels this
    /// caller's wait, never the shared resolution.
    /// </summary>
    /// <param name="requested">The package references to resolve.</param>
    /// <param name="targetFramework">The target framework to resolve assets for; defaults to <c>net10.0</c> when <c>null</c>.</param>
    /// <param name="ct">Token used to cancel this caller's wait for the resolution.</param>
    /// <returns>A <c>ResolvedPackageSet</c> describing the resolved assemblies, probing directories and versions.</returns>
    public Task<ResolvedPackageSet> ResolveAsync(
        IReadOnlyCollection<NuGetPackageReference> requested,
        NuGetFramework? targetFramework = null,
        CancellationToken ct = default)
        => Resolve(requested, targetFramework).Await(ct);

    /// <summary>
    /// The ONE attempt for <paramref name="key"/>: the NuGet SDK leaf on the pool, bounded by the
    /// budget, its failure named, reported and evicted on the SHARED side — so it happens once per
    /// attempt and also when no subscriber is left — and replayed to every subscriber.
    /// </summary>
    private IObservable<ResolvedPackageSet> SharedResolution(
        string key, ImmutableArray<NuGetPackageReference> requested, NuGetFramework framework)
        => _pool.Invoke(ct =>
            {
                OnResolutionStarting?.Invoke();
                return ResolveCoreAsync(requested, framework, ct);
            })
            // On expiry Timeout disposes the leaf's subscription, which cancels the leaf's token, so
            // NuGet stops; the fallback raises the named error in its place.
            .Timeout(_resolveBudget, Observable.Defer(() =>
                Observable.Throw<ResolvedPackageSet>(BudgetExceeded(requested))))
            .Do(_ => { }, error =>
            {
                // Evict on the shared side, upstream of the replay: this runs before any subscriber
                // sees the failure, so the entry under the key is still this attempt's — nothing
                // else removes it, and no replacement can exist before it is removed.
                _resolutions.Invalidate(key);
                MsLogging.LoggerExtensions.LogWarning(logger, error,
                    "NuGet resolution of {Packages} failed (feeds: {Feeds})",
                    DescribePackages(requested), DescribeFeeds());
            })
            .Replay(1)
            .AutoConnectOwnedBy(_connections, _releaseLane, nameof(NuGetAssemblyResolver));

    /// <summary>
    /// Cancels every persistent-cache save still running, and releases every shared resolution's
    /// connection: an attempt still in flight is cancelled, and a subscriber still attached, or
    /// arriving later, is terminated with an <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        _saves.Dispose();
        _connections.Dispose();
    }

    /// <summary>
    /// Test seam: called on the pool thread as each resolution attempt starts. Null in production.
    /// </summary>
    internal Action? OnResolutionStarting { get; set; }

    private TimeoutException BudgetExceeded(ImmutableArray<NuGetPackageReference> requested)
        => new(
            $"Resolving NuGet package(s) {DescribePackages(requested)} did not finish within its budget of "
            + $"{_resolveBudget.TotalSeconds:0} s. The package folder could not answer, and the configured "
            + $"feed(s) {DescribeFeeds()} did not answer in time — a feed that accepts the connection and "
            + "never responds looks exactly like this.");

    private static string DescribePackages(IEnumerable<NuGetPackageReference> requested)
        => string.Join(", ", requested.Select(r => $"{r.Id} {r.VersionRange ?? "*"}"));

    private string DescribeFeeds()
        => string.Join(", ", LoadSources().Select(s => $"'{s.PackageSource.Source}'"));

    private SourceRepository[] LoadSources()
    {
        var providers = Repository.Provider.GetCoreV3();
        var sources = new PackageSourceProvider(_settings).LoadPackageSources()
            .Where(s => s.IsEnabled)
            .Select(s => new SourceRepository(s, providers))
            .ToArray();
        return sources.Length > 0
            ? sources
            : [new SourceRepository(new PackageSource("https://api.nuget.org/v3/index.json"), providers)];
    }

    private async Task<ResolvedPackageSet> ResolveCoreAsync(
        IReadOnlyCollection<NuGetPackageReference> requested,
        NuGetFramework framework,
        CancellationToken ct)
    {
        var packagesRoot = _globalPackagesFolder ?? SettingsUtility.GetGlobalPackagesFolder(_settings);
        var sources = LoadSources();

        // Honor nuget.config's packageSourceMapping. The repo config pins MeshWeaver.*/Memex.*
        // to the `mesh-local` feed (dist/packages) precisely so a version-LESS
        // `#r "nuget:MeshWeaver.BusinessRules.Generator"` resolves the locally-built package and
        // NOT a same-named package republished on nuget.org. Without this, the version-less
        // resolve picks the HIGHEST version across ALL enabled sources and returns from the first
        // source that has it (nuget.org is listed first). nuget.org carries a stale 3.0.0-preview1
        // of the generator — published before the lib/<tfm> asset was added, so it ships ONLY
        // analyzers/dotnet/cs/ — which outranks/ties the local feed; the resolver then surfaces no
        // lib/ assembly, the generator never loads, and scope nodes compile but generate nothing.
        var sourceMapping = PackageSourceMapping.GetPackageSourceMapping(_settings);

        var available = new HashSet<SourcePackageDependencyInfo>(PackageIdentityComparer.Default);
        var targets = new List<PackageIdentity>();

        foreach (var req in requested)
        {
            var identity = await ResolveIdentityAsync(
                req, FilterSourcesForPackage(req.Id, sources, sourceMapping), framework, ct);
            targets.Add(identity);
            await WalkDependenciesAsync(identity, packagesRoot, sources, sourceMapping, framework, available, ct);
        }

        var resolverContext = new PackageResolverContext(
            // HighestMinor: pick the highest version that satisfies each
            // constraint within the same major.minor as the target. For `#r`
            // directives in user-authored code we want security fixes (patch
            // bumps) to flow in transparently, while not silently jumping
            // across minor or major boundaries that may carry breaking
            // changes. `Lowest` (the previous default) pulled minimum-
            // satisfying versions for transitives, which surfaced EOL/CVE
            // releases when constraints used weak floors like `>= 1.0.0`.
            dependencyBehavior: DependencyBehavior.HighestMinor,
            targetIds: targets.Select(t => t.Id),
            requiredPackageIds: Enumerable.Empty<string>(),
            packagesConfig: Enumerable.Empty<global::NuGet.Packaging.PackageReference>(),
            preferredVersions: targets,
            availablePackages: available,
            packageSources: sources.Select(s => s.PackageSource),
            log: _nugetLogger);

        var resolver = new PackageResolver();
        var resolved = resolver.Resolve(resolverContext, ct).ToList();

        var assemblyPaths = ImmutableArray.CreateBuilder<string>();
        var runtimePaths = ImmutableArray.CreateBuilder<string>();
        var nativePaths = ImmutableArray.CreateBuilder<string>();
        var managedDirs = ImmutableArray.CreateBuilder<string>();
        var nativeDirs = ImmutableArray.CreateBuilder<string>();
        var versions = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        // The runtime this process IS — the portal image is linux-x64, a developer box osx-arm64 or
        // win-x64. Assets are chosen for it, nearest RID first (RuntimeAssetSelector).
        var ridChain = RuntimeAssetSelector.RidFallbackChain();

        foreach (var pkg in resolved)
        {
            var info = available.First(a =>
                string.Equals(a.Id, pkg.Id, StringComparison.OrdinalIgnoreCase) && a.Version == pkg.Version);
            var installedPath = await EnsureInstalledAsync(info, packagesRoot, ct);
            versions[pkg.Id] = pkg.Version.ToNormalizedString();

            // 🚨 lib/<tfm>/ alone is NOT the runtime asset. A package may replace it per RID under
            // runtimes/<rid>/lib/<tfm>/ — and for every .NET SQL Server client the lib/ copy is the
            // "not supported on this platform" stub: a NodeType referencing Microsoft.Data.SqlClient
            // compiled, then failed at render on linux-x64 (client-a.meshweaver.cloud, 2026-10-11).
            // Compile against lib/, LOAD the RID group, and surface runtimes/<rid>/native/ too.
            using var reader = new PackageFolderReader(installedPath);
            var assets = RuntimeAssetSelector.Select(installedPath, reader.GetFiles(), framework, ridChain);
            if (assets.RuntimeRid is not null)
                MsLogging.LoggerExtensions.LogDebug(logger,
                    "NuGet package {Id} {Version}: runtime assets from runtimes/{Rid}/lib (compile against lib/)",
                    pkg.Id, versions[pkg.Id], assets.RuntimeRid);

            foreach (var path in assets.Compile.Where(File.Exists)) assemblyPaths.Add(path);
            foreach (var path in assets.Runtime.Where(File.Exists))
            {
                runtimePaths.Add(path);
                if (Path.GetDirectoryName(path) is { Length: > 0 } dir) managedDirs.Add(dir);
            }
            foreach (var path in assets.Native.Where(File.Exists))
            {
                nativePaths.Add(path);
                if (Path.GetDirectoryName(path) is { Length: > 0 } dir) nativeDirs.Add(dir);
            }
        }

        // Managed runtime directories first, then the native ones (`…/runtimes/<rid>/native`, which
        // the managed probe skips and LoadUnmanagedDll searches — PackageAssemblyProbe).
        var probing = managedDirs.Concat(nativeDirs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return new ResolvedPackageSet(assemblyPaths.ToImmutable(), probing, versions.ToImmutable())
        {
            RuntimeAssemblyPaths = runtimePaths.ToImmutable(),
            NativeLibraryPaths = nativePaths.ToImmutable(),
        };
    }

    private async Task<PackageIdentity> ResolveIdentityAsync(
        NuGetPackageReference req, SourceRepository[] sources, NuGetFramework framework, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(req.VersionRange) && NuGetVersion.TryParse(req.VersionRange, out var pinned))
            return new PackageIdentity(req.Id, pinned);

        var range = string.IsNullOrEmpty(req.VersionRange)
            ? VersionRange.All
            : VersionRange.Parse(req.VersionRange);

        foreach (var src in sources)
        {
            // A source that offers no FindPackageByIdResource cannot answer for this id at all
            // (an unreachable or misconfigured feed resolves the resource to null). Skipping is
            // right: the next source gets its turn, and exhausting them all still ends in the
            // named throw below rather than a NullReferenceException here.
            var finder = await src.GetResourceAsync<FindPackageByIdResource>(ct);
            if (finder is null) continue;
            var versions = await finder.GetAllVersionsAsync(req.Id, _sourceCache, _nugetLogger, ct);
            var match = versions?.Where(v => range.Satisfies(v)).OrderByDescending(v => v).FirstOrDefault();
            if (match is not null) return new PackageIdentity(req.Id, match);
        }
        throw new InvalidOperationException($"No NuGet package '{req.Id}' matching '{req.VersionRange ?? "*"}' found on configured sources.");
    }

    /// <summary>
    /// Restricts the candidate sources for a single package id to those allowed by
    /// nuget.config's <c>packageSourceMapping</c> (longest-prefix pattern wins, exactly as
    /// NuGet restore applies it). When no mapping is configured, or the id matches no pattern,
    /// all sources are returned — matching NuGet's own behavior. This is what keeps a
    /// version-less <c>#r "nuget:MeshWeaver.*"</c> pinned to the mesh-local feed instead of
    /// resolving a same-named package off nuget.org. Falls back to all sources only if the
    /// mapped source is not among the enabled ones (a config mismatch) rather than failing.
    /// </summary>
    private static SourceRepository[] FilterSourcesForPackage(
        string packageId, SourceRepository[] sources, PackageSourceMapping mapping)
    {
        if (!mapping.IsEnabled) return sources;
        var allowed = mapping.GetConfiguredPackageSources(packageId);
        if (allowed is null || allowed.Count == 0) return sources;
        var filtered = sources
            .Where(s => allowed.Contains(s.PackageSource.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return filtered.Length > 0 ? filtered : sources;
    }

    private async Task WalkDependenciesAsync(
        PackageIdentity root, string packagesRoot, SourceRepository[] sources, PackageSourceMapping mapping,
        NuGetFramework framework, HashSet<SourcePackageDependencyInfo> collected, CancellationToken ct)
    {
        var queue = new Queue<PackageIdentity>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (collected.Any(c => PackageIdentityComparer.Default.Equals(c, current))) continue;

            // OFFLINE FIRST (#6863): an identity the package folder already holds complete is
            // answered by its own .nuspec. The feed is never asked, so a warm folder resolves —
            // and loads a recorded build — while every feed is down.
            // The rule is EXACT identities only: every node of the walk is the exact version a
            // dependency's range asks for (its minimum), never a different installed version standing
            // in for it. A candidate the folder does not hold at that exact version needs the feed —
            // choosing among versions is the feed's job — and fails with the named error while the
            // feed is down. Substituting installed versions was tried and rejected: it lets unrelated
            // folder contents change which versions resolve (NuGetRuntimeAssets.md, "Known limit").
            var info = ReadInstalledDependencyInfo(current, packagesRoot, framework)
                ?? await ResolveFromFeedAsync(current, packagesRoot, sources, mapping, framework, ct);
            if (info is null)
            {
                // A REQUESTED package that neither the folder nor any feed holds is the answer
                // "it does not exist" — say so by name here, rather than letting the package
                // resolver below fail on a target it was never given.
                if (PackageIdentityComparer.Default.Equals(current, root))
                {
                    var asked = string.Join(", ",
                        FilterSourcesForPackage(current.Id, sources, mapping).Select(s => $"'{s.PackageSource.Source}'"));
                    throw new InvalidOperationException(
                        $"NuGet package {current.Id} {current.Version.ToNormalizedString()} was not found: it is not "
                        + $"installed in the package folder '{packagesRoot}', and none of the configured feed(s) "
                        + $"{asked} has it.");
                }
                continue;
            }

            collected.Add(info);
            foreach (var dep in info.Dependencies)
            {
                var depIdentity = new PackageIdentity(dep.Id, dep.VersionRange.MinVersion);
                if (!collected.Any(c => PackageIdentityComparer.Default.Equals(c, depIdentity)))
                    queue.Enqueue(depIdentity);
            }
        }
    }

    private async Task<SourcePackageDependencyInfo?> ResolveFromFeedAsync(
        PackageIdentity current, string packagesRoot, SourceRepository[] sources, PackageSourceMapping mapping,
        NuGetFramework framework, CancellationToken ct)
    {
        // Each package id is restricted to its mapped source(s) too, so a transitive
        // MeshWeaver.* dependency can't silently slip in from nuget.org either.
        var candidates = FilterSourcesForPackage(current.Id, sources, mapping);
        // Every configured feed gets its turn: one failing feed is not the answer while another
        // may hold the package. Each failure is kept with the feed it came from, so the error
        // below names which feed failed and why, never "all unreachable".
        var failures = ImmutableList<(string Feed, Exception Error)>.Empty;
        foreach (var src in candidates)
        {
            try
            {
                var resource = await src.GetResourceAsync<DependencyInfoResource>(ct);
                if (resource is null) continue;
                var info = await resource.ResolvePackage(current, framework, _sourceCache, _nugetLogger, ct);
                if (info is null) continue;
                foreach (var (feed, error) in failures)
                    MsLogging.LoggerExtensions.LogWarning(logger, error,
                        "NuGet feed {Feed} failed resolving {Id} {Version}; resolved from {Source} instead",
                        feed, current.Id, current.Version.ToNormalizedString(), src.PackageSource.Source);
                return info;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures = failures.Add((src.PackageSource.Source, ex));
            }
        }
        if (failures.IsEmpty) return null;

        // The folder could not answer, no feed supplied the package, and at least one feed
        // failed. Name what was missing locally and what each failing feed reported.
        var detail = string.Join("; ", failures.Select(f => $"'{f.Feed}': {f.Error.Message}"));
        throw new InvalidOperationException(
            $"NuGet package {current.Id} {current.Version.ToNormalizedString()} is not installed complete in "
            + $"the package folder '{packagesRoot}', and resolving it from the configured feed(s) failed — {detail}",
            failures.Count == 1 ? failures[0].Error : new AggregateException(failures.Select(f => f.Error)));
    }

    /// <summary>
    /// Reads the dependency info of <paramref name="identity"/> from the package folder when the
    /// package is installed there complete (<see cref="ReadCompleteInstall"/>). Dependencies come
    /// from the nuspec's group nearest to <paramref name="framework"/>, as a feed's dependency info
    /// would. Returns <c>null</c> when the folder cannot answer, which sends the caller to the feed.
    /// </summary>
    private SourcePackageDependencyInfo? ReadInstalledDependencyInfo(
        PackageIdentity identity, string packagesRoot, NuGetFramework framework)
    {
        var nuspec = ReadCompleteInstall(identity, packagesRoot);
        if (nuspec is null) return null;
        var group = NuGetFrameworkUtility.GetNearest(
            nuspec.GetDependencyGroups(), framework, g => g.TargetFramework);
        var dependencies = group?.Packages ?? [];
        MsLogging.LoggerExtensions.LogDebug(logger,
            "NuGet package {Id} {Version} resolved from the package folder (no feed call)",
            identity.Id, identity.Version.ToNormalizedString());
        return new SourcePackageDependencyInfo(
            new PackageIdentity(nuspec.GetId(), identity.Version), dependencies, listed: true,
            source: null, downloadUri: null, packageHash: null);
    }

    /// <summary>
    /// The ONE test of whether <paramref name="identity"/> is installed in the package folder and may
    /// be used as it stands — by the offline dependency walk AND by the install fast path, so a folder
    /// the walk refuses is never handed to asset selection either. Installed means: the v3 folder
    /// exists, carries a completion marker NuGet's own restore trusts (<c>.nupkg.metadata</c>, or the
    /// older <c>.nupkg.sha512</c>, both written after extraction), and its <c>.nuspec</c> declares
    /// exactly that id and version. Returns the nuspec, or <c>null</c> (with the reason logged when a
    /// folder exists but is refused).
    /// </summary>
    private NuspecReader? ReadCompleteInstall(PackageIdentity identity, string packagesRoot) =>
        ReadCompleteFolder(identity, packagesRoot,
            new VersionFolderPathResolver(packagesRoot).GetInstallPath(identity.Id, identity.Version));

    /// <summary>
    /// <see cref="ReadCompleteInstall"/> over an explicit <paramref name="installPath"/>, so a staging
    /// folder can be checked by the same rule before it is published to the install path.
    /// </summary>
    private NuspecReader? ReadCompleteFolder(PackageIdentity identity, string packagesRoot, string installPath)
    {
        if (!Directory.Exists(installPath)) return null;
        var pathResolver = new VersionFolderPathResolver(packagesRoot);
        var version = identity.Version.ToNormalizedString();
        var complete = File.Exists(Path.Combine(installPath, PackagingCoreConstants.NupkgMetadataFileExtension))
            || File.Exists(Path.Combine(installPath, pathResolver.GetHashFileName(identity.Id, identity.Version)));
        var nuspecPath = Path.Combine(installPath, pathResolver.GetManifestFileName(identity.Id, identity.Version));
        if (!complete || !File.Exists(nuspecPath))
        {
            MsLogging.LoggerExtensions.LogWarning(logger,
                "Package folder {Path} for {Id} {Version} is incomplete (no completion marker or no nuspec); not using it",
                installPath, identity.Id, version);
            return null;
        }

        try
        {
            OnBeforeManifestRead?.Invoke(installPath);
            var nuspec = new NuspecReader(nuspecPath);
            var declared = nuspec.GetIdentity();
            if (string.Equals(declared.Id, identity.Id, StringComparison.OrdinalIgnoreCase)
                && declared.Version == identity.Version)
                return nuspec;
            MsLogging.LoggerExtensions.LogWarning(logger,
                "Package folder {Path} declares {DeclaredId} {DeclaredVersion}, not {Id} {Version}; not using it",
                installPath, declared.Id, declared.Version.ToNormalizedString(), identity.Id, version);
            return null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The folder passed the existence checks and was gone by the time the manifest was read:
            // another resolution is repairing it under the install lock (a refused folder is deleted
            // there). That is a local MISS, exactly as if the folder had never been there — the
            // caller goes on to the feed and the install lock, which waits for that repair.
            MsLogging.LoggerExtensions.LogDebug(logger, ex,
                "Package folder {Path} for {Id} {Version} disappeared while it was read; treating it as not installed",
                installPath, identity.Id, version);
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or PackagingException)
        {
            MsLogging.LoggerExtensions.LogWarning(logger, ex,
                "Package folder {Path} for {Id} {Version} has an unreadable nuspec; not using it",
                installPath, identity.Id, version);
            return null;
        }
    }

    /// <summary>
    /// Test seam: called with a package folder after it passed the existence checks and before its
    /// manifest is read. A concurrent repair can delete the folder in exactly that window, so a test
    /// deletes it here to make the interleaving deterministic. Null in production.
    /// </summary>
    internal Action<string>? OnBeforeManifestRead { get; set; }

    /// <summary>
    /// Test seam: called with the package identity after the lock-free check refused its folder
    /// and before the install lock is taken. That is the window in which a second resolution can
    /// complete the same package, so a test parks one caller here to make that interleaving
    /// deterministic. Null in production.
    /// </summary>
    internal Action<PackageIdentity>? OnInstallFolderRefused { get; set; }

    private async Task<string> EnsureInstalledAsync(SourcePackageDependencyInfo info, string packagesRoot, CancellationToken ct)
    {
        var identity = new PackageIdentity(info.Id, info.Version);
        // The v3 layout lower-cases the id AND the version folder — the same path the install
        // below writes and the offline walk reads (VersionFolderPathResolver).
        var installedPath = new VersionFolderPathResolver(packagesRoot).GetInstallPath(info.Id, info.Version);
        // Lock-free fast path: a COMPLETE folder is never deleted by anyone (only a refused one is,
        // and only under the lock below), so using it without the lock is safe.
        if (ReadCompleteInstall(identity, packagesRoot) is not null)
            return installedPath;

        OnInstallFolderRefused?.Invoke(identity);

        // Everything that inspects, deletes or writes this package's folder runs under ONE lock per
        // package identity, shared by every resolution in this process AND every process on this
        // package folder: NuGet's own file lock (ConcurrencyUtilities), the primitive its restore
        // and PackageExtractor install under. The lock name is the install DIRECTORY; the extractor
        // takes its own lock on the package file inside it, so the two never nest on one name.
        return await ConcurrencyUtilities.ExecuteWithFileLockedAsync(
            installedPath,
            lockedCt => InstallUnderLockAsync(info, identity, packagesRoot, installedPath, lockedCt),
            ct);
    }

    private async Task<string> InstallUnderLockAsync(
        SourcePackageDependencyInfo info, PackageIdentity identity, string packagesRoot, string installedPath,
        CancellationToken ct)
    {
        var versionString = info.Version.ToNormalizedString();
        // Re-check INSIDE the lock: another resolution may have completed this package while we
        // waited. A folder it completed is used as it stands — deleting it would pull assemblies
        // out from under a caller that already returned their paths.
        if (ReadCompleteInstall(identity, packagesRoot) is not null)
            return installedPath;

        // Still refused under the lock — half-extracted, or holding another identity — so nobody is
        // using it: asset selection only ever reads complete folders. Remove it and install afresh
        // (persistent cache first, then the feed). Leaving it would hand partial or WRONG
        // assemblies to the compile and the load.
        if (Directory.Exists(installedPath))
        {
            MsLogging.LoggerExtensions.LogWarning(logger,
                "Reinstalling NuGet package {Id} {Version}: the folder {Path} is incomplete or holds another package",
                info.Id, versionString, installedPath);
            Directory.Delete(installedPath, recursive: true);
        }

        // Try to hydrate from the persistent cache (e.g., Azure Blob) before hitting the feed.
        if (await TryHydrateFromCacheAsync(identity, packagesRoot, installedPath, ct))
            return installedPath;

        if (info.Source is null)
            throw new InvalidOperationException(
                $"{info.Id} {info.Version} was resolved without a source repository, so there is "
                + "nowhere to download it from. This is dependency info assembled from a feed that "
                + "did not identify itself — check the configured sources.");
        var downloadResource = await info.Source.GetResourceAsync<DownloadResource>(ct);
        if (downloadResource is null)
            throw new InvalidOperationException(
                $"Source '{info.Source.PackageSource?.Source ?? "<unknown>"}' resolved {info.Id} "
                + $"{info.Version} but offers no download resource — the feed answered metadata and "
                + "cannot serve bytes. Check the source is reachable and is a v3 feed.");
        var downloadContext = new PackageDownloadContext(_sourceCache);
        using var result = await downloadResource.GetDownloadResourceResultAsync(
            new PackageIdentity(info.Id, info.Version), downloadContext, packagesRoot, _nugetLogger, ct);

        // 🚨 ONLY `Available` is usable here, and this used to accept `AvailableWithoutStream` too
        // — then immediately Seek() and CopyToAsync() the stream that status exists to say is
        // ABSENT. That was a latent NullReferenceException on a status the code deliberately let
        // through; the nullable annotations in the bumped NuGet client are what surfaced it.
        // This method installs FROM the stream, so "available without one" cannot satisfy it, and
        // saying so beats an NRE three frames away.
        if (result.Status != DownloadResourceResultStatus.Available || result.PackageStream is null)
            throw new InvalidOperationException(
                $"Failed to download {info.Id} {info.Version}: {result.Status}"
                + (result.Status == DownloadResourceResultStatus.AvailableWithoutStream
                    ? " — the source reports the package as available but served no stream, and this "
                      + "install path needs the bytes."
                    : string.Empty));

        result.PackageStream.Seek(0, SeekOrigin.Begin);
        // Install into the v3 global-packages layout ({id-lowercase}/{version}/) — the SAME path
        // `installedPath` above reads back from. ExtractPackageAsync + PackagePathResolver writes
        // the packages.config-style side-by-side `{Id}.{Version}` folder (original cased) instead,
        // so for any package that actually has to be downloaded (i.e. is NOT already in the global
        // cache — exactly the freshly-packed MeshWeaver.BusinessRules.Generator), the read path
        // stayed the EMPTY directory we pre-created: GetLibItemsAsync returned nothing and the
        // package's lib/ assembly was never surfaced (the scope generator silently failed to load,
        // scope nodes compiled but generated no IScope<,> implementations). VersionFolderPathResolver
        // is the canonical v3 install resolver and lands exactly at
        // Path.Combine(packagesRoot, id.ToLowerInvariant(), version.ToNormalizedString()).
        await PackageExtractor.InstallFromSourceAsync(
            source: info.Source.PackageSource?.Source ?? string.Empty,
            packageIdentity: new PackageIdentity(info.Id, info.Version),
            copyToAsync: destination => result.PackageStream.CopyToAsync(destination, ct),
            versionFolderPathResolver: new VersionFolderPathResolver(packagesRoot),
            packageExtractionContext: new PackageExtractionContext(
                PackageSaveMode.Defaultv3,
                XmlDocFileSaveMode.Skip,
                ClientPolicyContext.GetClientPolicy(_settings, _nugetLogger),
                _nugetLogger),
            token: ct);

        SavePersistently(info.Id, versionString, installedPath);

        return installedPath;
    }

    /// <summary>
    /// The persistent-cache save: optional, so not awaited, and run on its OWN lane
    /// (<see cref="IoPoolNames.PackageCache"/>) — a stalled save sits outside every resolution's
    /// budget, and on the resolution's pool it would hold the slot the next resolution needs, even
    /// one the package folder answers. The resolver owns the subscription, so disposing it cancels
    /// a save still running; a failure is logged, never lost.
    /// </summary>
    private void SavePersistently(string id, string version, string installedPath)
    {
        var save = new SingleAssignmentDisposable();
        _saves.Add(save);
        save.Disposable = _cachePool
            .Invoke(saveCt => _packageCache.SaveAsync(id, version, installedPath, saveCt))
            .Finally(() => _saves.Remove(save))
            .Subscribe(_ => { }, ex => MsLogging.LoggerExtensions.LogWarning(logger, ex,
                "Failed to save NuGet package {Id} {Version} to persistent cache", id, version));
    }

    /// <summary>
    /// Hydrates <paramref name="identity"/> from the persistent cache into a STAGING folder beside the
    /// install path, checks it there by the same rule as any installed folder, and only then
    /// publishes it to <paramref name="installedPath"/> with one directory rename. A cache writes
    /// entries in archive order, so extracting in place could expose the nuspec and the completion
    /// marker before the assemblies: a resolver taking the lock-free fast path would then accept a
    /// half-written folder. The install path therefore either does not exist or is complete.
    /// The staged folder must also pass the identity check: a poisoned cache (a different package
    /// under the wrong key, a cross-tenant blob, drift after a manual copy) would otherwise load
    /// wrong assemblies into a NodeType compile. Runs under the package's install lock.
    /// </summary>
    private async Task<bool> TryHydrateFromCacheAsync(
        PackageIdentity identity, string packagesRoot, string installedPath, CancellationToken ct)
    {
        var versionString = identity.Version.ToNormalizedString();
        // Same parent as the install path, so the publish is a rename on one volume. The name is
        // not a version, so NuGet's own folder enumeration never mistakes it for an install.
        var staging = Path.Combine(
            Path.GetDirectoryName(installedPath)!, $".hydrating-{versionString}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            if (!await _packageCache.TryHydrateAsync(identity.Id, versionString, staging, ct))
                return false;
            if (ReadCompleteFolder(identity, packagesRoot, staging) is null)
            {
                MsLogging.LoggerExtensions.LogWarning(logger,
                    "Hydrated package {Id} {Version} is not a complete install; discarding it and falling back to the feed",
                    identity.Id, versionString);
                return false;
            }
            Directory.Move(staging, installedPath);
            MsLogging.LoggerExtensions.LogInformation(logger,
                "Hydrated NuGet package {Id} {Version} from persistent cache", identity.Id, versionString);
            return true;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception delEx)
                {
                    MsLogging.LoggerExtensions.LogWarning(logger, delEx,
                        "Failed to delete the hydration staging folder {Path}", staging);
                }
            }
        }
    }

    private static string BuildCacheKey(IReadOnlyCollection<NuGetPackageReference> requested, NuGetFramework framework)
    {
        var parts = requested
            .OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(r => $"{r.Id}|{r.VersionRange ?? "*"}");
        return $"{framework.DotNetFrameworkName}::{string.Join(";", parts)}";
    }

    private sealed class NuGetLogger(MsLogging.ILogger logger) : LoggerBase
    {
        public override void Log(ILogMessage message)
        {
            var level = message.Level switch
            {
                LogLevel.Debug => MsLogging.LogLevel.Debug,
                LogLevel.Verbose => MsLogging.LogLevel.Trace,
                LogLevel.Information => MsLogging.LogLevel.Information,
                LogLevel.Minimal => MsLogging.LogLevel.Information,
                LogLevel.Warning => MsLogging.LogLevel.Warning,
                LogLevel.Error => MsLogging.LogLevel.Error,
                _ => MsLogging.LogLevel.Debug,
            };
            MsLogging.LoggerExtensions.Log(logger, level, "{Message}", message.Message);
        }

        public override Task LogAsync(ILogMessage message)
        {
            Log(message);
            return Task.CompletedTask;
        }
    }
}
