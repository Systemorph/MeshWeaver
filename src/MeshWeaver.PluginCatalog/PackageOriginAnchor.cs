using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// One package as the REGISTRY carries it — the authoritative half of the
/// <c>(source, package)</c> pair a <see cref="MeshWeaver.Mesh.Security.PluginGrant"/> is matched
/// against, plus the few fields a bundle index needs to advertise it.
/// </summary>
/// <param name="PackageId">The package's catalog id.</param>
/// <param name="Source">🚨 The configured source name it is carried in — the anchor.</param>
/// <param name="ReleasedVersion">Its released SemVer, or null when it has none (not servable).</param>
/// <param name="Module">The compiled module it declares, or null.</param>
/// <param name="MinMeshVersion">That module's declared platform floor, or null.</param>
/// <param name="TargetPartition">The partition the package installs into, when it declares one —
/// what a serving instance checks to know whether it holds this package's content at all.</param>
public sealed record PackageOrigin(
    string PackageId,
    string Source,
    string? ReleasedVersion,
    string? Module,
    string? MinMeshVersion,
    string? TargetPartition = null)
{
    /// <summary>The partition this package's content lives in — its declared
    /// <see cref="TargetPartition"/>, else its id (what a node-native repo's folder name is).</summary>
    public string Partition =>
        TargetPartition is { Length: > 0 } declared ? declared : PackageId;
}

/// <summary>
/// How completely the anchor could be read. 🚨 Only <see cref="Authoritative"/> makes an ABSENCE
/// meaningful — under every other state "the registry does not carry it" is a thing we could not
/// establish, not a thing we observed.
/// </summary>
public enum AnchorState
{
    /// <summary>Every configured source listed successfully. An absence from this snapshot is a
    /// real negative.</summary>
    Authoritative,

    /// <summary>At least one source failed, but earlier observations are being carried forward. The
    /// bindings present are real; an absence proves nothing.</summary>
    Stale,

    /// <summary>No source could be listed and nothing was ever observed. The anchor is silent.</summary>
    Unreachable,

    /// <summary>This instance configures no package sources at all, so it is an authority on
    /// nothing. 🚨 Deliberately NOT folded into <see cref="Authoritative"/>: "I have no sources"
    /// answering "the registry carries no such package" is precisely the absence-denies bug.</summary>
    Unconfigured,
}

/// <summary>
/// What the registry carried, when it was asked, and how well it answered.
/// </summary>
/// <param name="State">How completely it was read.</param>
/// <param name="Origins">Package id → its origin, keyed case-insensitively (a lookup convenience;
/// the authorization comparison itself stays <see cref="MeshWeaver.Mesh.Security.PluginGrantEntry.Matches"/>'s).</param>
/// <param name="ObservedAt">When the newest contributing read happened.</param>
/// <param name="Failure">Why the read was not complete, when it was not.</param>
public sealed record PackageOriginSnapshot(
    AnchorState State,
    ImmutableDictionary<string, PackageOrigin> Origins,
    DateTimeOffset ObservedAt,
    string? Failure)
{
    /// <summary>🚨 Whether an ABSENCE from <see cref="Origins"/> may be read as a negative.</summary>
    public bool IsComplete => State == AnchorState.Authoritative;

    /// <summary>The source the registry binds <paramref name="packageId"/> to, or null.</summary>
    public string? SourceOf(string packageId) =>
        Origins.TryGetValue(packageId, out var origin) ? origin.Source : null;

    /// <summary>One line, for a log or a health payload.</summary>
    public string Describe() => State switch
    {
        AnchorState.Authoritative =>
            $"registry anchor: {Origins.Count} package(s), read in full at {ObservedAt:O}",
        AnchorState.Stale =>
            $"registry anchor DEGRADED: carrying {Origins.Count} previously observed package(s) — "
            + $"the last full read failed ({Failure})",
        AnchorState.Unreachable =>
            $"registry anchor UNREACHABLE and nothing was ever observed ({Failure})",
        _ =>
            "registry anchor NOT CONFIGURED — this instance declares no package sources, so it is "
            + "an authority on no package",
    };

    /// <summary>An empty snapshot in <paramref name="state"/>.</summary>
    public static PackageOriginSnapshot Empty(AnchorState state, DateTimeOffset at, string? failure = null) =>
        new(state,
            ImmutableDictionary.Create<string, PackageOrigin>(StringComparer.OrdinalIgnoreCase),
            at, failure);
}

/// <summary>
/// 🚨 <b>Reads the ENTITLEMENT ANCHOR</b> (#1782 gap 2): the packages this instance's configured
/// sources carry, and which source carries each — the binding
/// <see cref="PackageEntitlementAnchor.Resolve"/> decides against.
///
/// <para>It is deliberately the SAME reading <c>/api/plugins</c> serves its catalog from
/// (<see cref="PackageSources.FromConfiguration"/> → <see cref="IPackageSource.ListPackages"/>), so
/// "which source is this package from" has one answer on this instance rather than one per surface.
/// On a registry that holds the git credential those sources are the plugin repos; on a downstream
/// instance they are the registry it pulls from. Either way the anchor is upstream of the local
/// install records, which is the whole point — a record is a cache of this answer, and its absence
/// must send us HERE rather than to a refusal.</para>
///
/// <para>🚨 <b>The failure path never produces a denial.</b> A source that will not list makes the
/// snapshot <see cref="AnchorState.Stale"/> (or <see cref="AnchorState.Unreachable"/>), and every
/// consumer of the snapshot reads <see cref="PackageOriginSnapshot.IsComplete"/> before treating an
/// absence as an answer. The last successful observation is retained precisely so a previously
/// observed entitlement keeps working while the anchor is down.</para>
///
/// <para>🚨 <b>The freshness window is a SNAPSHOT window, not an entitlement expiry.</b> Entitlements
/// are eternal and nothing here introduces a term: the window only says how long an authoritative
/// listing may be reused before the sources are asked again, and its expiry triggers a READ, never
/// a refusal. Conflating the two would smuggle in exactly the expiry the Store model forbids.</para>
///
/// <para>No <c>async</c>: the sources are already <see cref="IObservable{T}"/>-shaped and their
/// genuinely-async leaves (git, HTTP) sit behind <see cref="MeshWeaver.Mesh.Threading.IIoPool"/>
/// inside them.</para>
/// </summary>
public sealed class PackageOriginAnchor
{
    /// <summary>How long an authoritative listing is reused before the sources are asked again.
    /// Config key <c>PluginCatalog:AnchorFreshnessSeconds</c>; 0 or negative disables reuse.</summary>
    public const string FreshnessConfigKey = "PluginCatalog:AnchorFreshnessSeconds";

    /// <summary>The default snapshot window — long enough that a boot-time stampede of consumers
    /// costs one listing, short enough that a newly published package appears within a minute.</summary>
    public static readonly TimeSpan DefaultFreshness = TimeSpan.FromSeconds(60);

    private readonly Func<IReadOnlyList<ConfiguredPackageSource>> sources;
    private readonly TimeSpan freshness;
    private readonly Func<DateTimeOffset> clock;
    private readonly ILogger? logger;
    private PackageOriginSnapshot? last;

    /// <summary>The DI constructor — reads the instance's configured package sources.</summary>
    /// <param name="hub">The root hub the sources are built against.</param>
    /// <param name="configuration">Where <c>PluginCatalog:Sources</c> lives.</param>
    /// <param name="loggerFactory">Diagnostics.</param>
    public PackageOriginAnchor(
        IMessageHub hub, IConfiguration configuration, ILoggerFactory? loggerFactory = null)
        : this(
            () => PackageSources.FromConfiguration(
                hub, configuration, loggerFactory?.CreateLogger<PackageOriginAnchor>()),
            Freshness(configuration),
            () => DateTimeOffset.UtcNow,
            loggerFactory?.CreateLogger<PackageOriginAnchor>())
    {
    }

    /// <summary>The seam constructor — the sources, the window and the clock are arguments so the
    /// anchor's degraded behaviour can be exercised without a registry, a repo or a wait.</summary>
    /// <param name="sources">The configured sources to list.</param>
    /// <param name="freshness">The snapshot window (not an entitlement term).</param>
    /// <param name="clock">Reads "now".</param>
    /// <param name="logger">Diagnostics.</param>
    public PackageOriginAnchor(
        Func<IReadOnlyList<ConfiguredPackageSource>> sources,
        TimeSpan freshness,
        Func<DateTimeOffset> clock,
        ILogger? logger = null)
    {
        this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        this.freshness = freshness;
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.logger = logger;
    }

    /// <summary>The most recent snapshot, or null when the anchor has never been read.</summary>
    public PackageOriginSnapshot? LastObserved => Volatile.Read(ref last);

    /// <summary>
    /// Reads the anchor. Emits exactly once and completes; never faults — every failure becomes a
    /// non-authoritative snapshot, because a throw here would turn "I could not ask" into an error
    /// the caller would have to decide something from, and the only safe decision from an error is
    /// the denial this whole change exists to remove.
    /// </summary>
    public IObservable<PackageOriginSnapshot> Read() => Observable.Defer(() =>
    {
        var now = clock();
        if (Volatile.Read(ref last) is { State: AnchorState.Authoritative } fresh
            && freshness > TimeSpan.Zero && now - fresh.ObservedAt < freshness)
            return Observable.Return(fresh);

        IReadOnlyList<ConfiguredPackageSource> configured;
        try
        {
            configured = sources();
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Entitlement anchor: the configured sources could not be built");
            return Observable.Return(Degrade(now, exception.Message));
        }

        if (configured.Count == 0)
            // Not an authority on anything. Recorded rather than cached, so a source list appearing
            // later is picked up on the next read.
            return Observable.Return(PackageOriginSnapshot.Empty(AnchorState.Unconfigured, now));

        var listings = configured.Select(source => source.Source
            .ListPackages(source.GitRef)
            .Take(1)
            .Select(packages => (Source: source, Packages: packages, Failure: (string?)null))
            .Catch((Exception exception) =>
            {
                logger?.LogWarning(exception,
                    "Entitlement anchor: listing source {Name} @ {Ref} failed — an ABSENCE from this "
                    + "read is therefore not evidence that a package is uncarried",
                    source.Name, source.GitRef);
                return Observable.Return((
                    Source: source,
                    Packages: (IReadOnlyList<PackageManifest>)[],
                    Failure: (string?)exception.Message));
            }));

        return listings.CombineLatest()
            .Take(1)
            .Select(perSource => Observe(perSource, clock()));
    });

    /// <summary>Folds one read into a snapshot and remembers it.</summary>
    private PackageOriginSnapshot Observe(
        IReadOnlyList<(ConfiguredPackageSource Source, IReadOnlyList<PackageManifest> Packages, string? Failure)> perSource,
        DateTimeOffset now)
    {
        var failures = perSource.Where(x => x.Failure is not null)
            .Select(x => $"{x.Source.Name}: {x.Failure}").ToArray();

        var observed = ImmutableDictionary.CreateBuilder<string, PackageOrigin>(
            StringComparer.OrdinalIgnoreCase);
        // The FIRST configured source wins on an id collision — the same precedence the merged
        // catalog applies, so the anchor and the listing can never disagree about which source a
        // package belongs to.
        foreach (var (source, packages, _) in perSource)
        foreach (var package in packages.Where(p => !string.IsNullOrWhiteSpace(p.Id)))
            observed.TryAdd(
                package.Id,
                new PackageOrigin(
                    package.Id, source.Name, package.ReleasedVersion, package.Module,
                    package.MinMeshVersion, package.TargetPartition));

        if (failures.Length == 0)
        {
            var snapshot = new PackageOriginSnapshot(
                AnchorState.Authoritative, observed.ToImmutable(), now, null);
            Volatile.Write(ref last, snapshot);
            return snapshot;
        }

        // A partial read: what WAS listed is real, so keep it — merged over whatever was observed
        // before, so a package that has been seen once does not vanish because a different source
        // is down. The state stays non-authoritative, which is what stops an absence from denying.
        var carried = Volatile.Read(ref last)?.Origins
                      ?? ImmutableDictionary.Create<string, PackageOrigin>(StringComparer.OrdinalIgnoreCase);
        var merged = carried.SetItems(observed);
        var degraded = new PackageOriginSnapshot(
            merged.IsEmpty ? AnchorState.Unreachable : AnchorState.Stale,
            merged, now, string.Join("; ", failures));
        Volatile.Write(ref last, degraded);
        return degraded;
    }

    /// <summary>The snapshot for "the sources themselves could not be built" — carries forward
    /// whatever was previously observed rather than presenting an empty authority.</summary>
    private PackageOriginSnapshot Degrade(DateTimeOffset now, string failure)
    {
        var carried = Volatile.Read(ref last)?.Origins
                      ?? ImmutableDictionary.Create<string, PackageOrigin>(StringComparer.OrdinalIgnoreCase);
        var snapshot = new PackageOriginSnapshot(
            carried.IsEmpty ? AnchorState.Unreachable : AnchorState.Stale, carried, now, failure);
        Volatile.Write(ref last, snapshot);
        return snapshot;
    }

    private static TimeSpan Freshness(IConfiguration configuration) =>
        int.TryParse(configuration?[FreshnessConfigKey], out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : DefaultFreshness;
}
