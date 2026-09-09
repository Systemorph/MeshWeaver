using System.Collections.Immutable;
using System.Globalization;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// How much of the CI-published prebuilt-bundle store
/// (<see cref="ShippedPrebuiltBundles.PublishedRootConfigKey"/>) to keep, and how the sweep runs.
///
/// <para><b>Every knob is a KEEP rule, and the rules are ORed</b> — an identity directory survives
/// if ANY rule protects it. Deleting a publication costs a compile (or, on a
/// <c>Modules:RequirePrebuilt</c> mesh, a loud refusal); keeping one costs ~30 MB on a 16 GiB share
/// that ALSO holds the modules, the assembly cache and the DataProtection key ring — and a FULL
/// share truncates writes silently (memex.systemorph.com, 2026-09-08: 3 MiB free, every runtime
/// recompile landing as <c>Bad IL format</c>, CD's bake read-back <c>ResourceNotFound</c> for
/// 39 of 45 files). So the rules keep what something REFERENCES and let nothing else accumulate.</para>
/// </summary>
public sealed record PrebuiltBundleRetention
{
    /// <summary>The shipped default: retain unused continuous artifacts for 30 days.</summary>
    public static readonly PrebuiltBundleRetention Default = new();

    /// <summary>
    /// Compatibility member for callers compiled against the former count-based policy.
    /// This value no longer affects retention; cleanup uses <see cref="MinimumAge"/>.
    /// </summary>
    public int KeepNewestPerSource { get; init; } = 10;

    /// <summary>The minimum age of unreferenced artifacts before cleanup. Values below 30 days are clamped.</summary>
    public TimeSpan MinimumAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Additional protection for an unsealed publication in flight, beyond the minimum age.</summary>
    public TimeSpan UnsealedGrace { get; init; } = TimeSpan.FromHours(2);

    /// <summary>How often the recurring pass runs after the boot pass.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Whether the sweep may delete. <b>Default true</b>: the store is a cache of rebuildable
    /// artifacts, and the share filling up is the outage. Set false to measure and report only.
    /// </summary>
    public bool Delete { get; init; } = true;
}

/// <summary>A platform version string placed on its line: <c>3.1.0-ci.8076</c> → line <c>3.1.0</c>, pre-release.</summary>
public static class PlatformVersionLine
{
    /// <summary>The <c>X.Y.Z</c> line of a version (build metadata and pre-release stripped), or null when it does not start with a numeric core.</summary>
    public static string? LineOf(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;
        var core = version.Trim();
        var plus = core.IndexOf('+');
        if (plus >= 0) core = core[..plus];
        var dash = core.IndexOf('-');
        if (dash >= 0) core = core[..dash];
        if (core.Length == 0 || !char.IsAsciiDigit(core[0]))
            return null;
        foreach (var c in core)
            if (!char.IsAsciiDigit(c) && c != '.')
                return null;
        return core;
    }

    /// <summary>True when the version carries a pre-release label (<c>-ci.&lt;n&gt;</c>, historically <c>-rc…</c>).</summary>
    public static bool IsPrerelease(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;
        var v = version.Trim();
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        return v.Contains('-');
    }

    /// <summary>
    /// The bare platform version a pin or a report names, in the form the <c>_releases</c> marker
    /// files are named: an image reference (<c>…/memex-portal-ai:3.0.0-ci.8080@sha256:…</c>) is
    /// reduced to its tag, and <c>+build</c> metadata is dropped. Null for a blank value.
    /// </summary>
    public static string? Normalize(string? versionOrImageTag)
    {
        if (string.IsNullOrWhiteSpace(versionOrImageTag))
            return null;
        var v = versionOrImageTag.Trim();
        var at = v.IndexOf('@');
        if (at >= 0) v = v[..at];
        var colon = v.LastIndexOf(':');
        if (colon >= 0) v = v[(colon + 1)..];
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        return v.Length == 0 ? null : v;
    }
}

/// <summary>
/// One thing outside this process that PINS a platform build and pulls its seal from this store:
/// a <c>Hosting/Deployment</c> record's <c>pinnedImageTag</c>, or a registered instance's
/// reported platform version / framework identity. On the registry (memex-cloud) remote
/// instances fetch their OWN identity's seal over the HTTP prebuilt surface, so such an identity
/// is referenced however old it is — pearl pinned to <c>3.0.0-ci.8080</c> would otherwise fall
/// outside the age window and lose its bundle at the next boot.
/// </summary>
/// <param name="Origin">Who pins it — the record or instance, for the ledger.</param>
/// <param name="Version">The pinned platform version or image tag (normalised by <see cref="PlatformVersionLine.Normalize"/>), or null.</param>
/// <param name="Identity">The framework identity when the origin reports it directly, or null.</param>
public sealed record PinnedPlatformReference(string Origin, string? Version, string? Identity);

/// <summary>
/// A provider of <see cref="PinnedPlatformReference"/>s, registered as an enumerable singleton;
/// the retention pass unions every registered source on every pass. An erroring source aborts
/// the pass — a reference set that could not be read licenses no deletion.
/// </summary>
public delegate IObservable<ImmutableList<PinnedPlatformReference>> PinnedPlatformReferenceSource();

/// <summary>One source directory under an identity, as the sweep read it.</summary>
/// <param name="Name">The source segment (<c>plugins</c>, <c>education</c>, …).</param>
/// <param name="IsSealed">The completion sentinel is present in the publication directory and every bundle it lists is on disk.</param>
/// <param name="SealUtc">When the sentinel was written, or null when unsealed.</param>
/// <param name="NewestWriteUtc">The newest write under the source directory (any file).</param>
/// <param name="ReadFault">Why the seal could not be READ, or null. A source with a read fault PROTECTS its identity.</param>
public sealed record PrebuiltSourceEntry(
    string Name, bool IsSealed, DateTimeOffset? SealUtc, DateTimeOffset NewestWriteUtc, string? ReadFault);

/// <summary>One framework-identity directory of the store and everything the rules need to judge it.</summary>
/// <param name="Identity">The directory name — the framework identity (<c>s…</c> / <c>g…</c>).</param>
/// <param name="Directory">Full path.</param>
/// <param name="Sources">Its source directories.</param>
/// <param name="Bytes">Total bytes under the identity directory.</param>
/// <param name="NewestWriteUtc">The newest write anywhere under it.</param>
public sealed record PrebuiltIdentityEntry(
    string Identity,
    string Directory,
    ImmutableList<PrebuiltSourceEntry> Sources,
    long Bytes,
    DateTimeOffset NewestWriteUtc)
{
    /// <summary>True when any source's seal could not be read.</summary>
    public bool HasReadFault => Sources.Any(s => s.ReadFault is not null);

    /// <summary>True when any source (or the whole identity) is not sealed.</summary>
    public bool HasUnsealedSource => Sources.Count == 0 || Sources.Any(s => !s.IsSealed);

    /// <summary>The newest seal across sources, or null when nothing is sealed.</summary>
    public DateTimeOffset? NewestSealUtc =>
        Sources.Where(s => s.SealUtc is not null).Select(s => s.SealUtc).Max();

    /// <summary>The instant the collectable ordering sorts on: the newest seal, else the newest write.</summary>
    public DateTimeOffset OrderingInstant => NewestSealUtc ?? NewestWriteUtc;
}

/// <summary>One <c>_releases/&lt;version&gt;</c> marker: the file NAME is the platform version, its CONTENT the identity.</summary>
/// <param name="Version">The platform version (the file name).</param>
/// <param name="Identity">The framework identity the marker names, or null when it could not be read.</param>
/// <param name="Path">Full path of the marker file.</param>
/// <param name="WrittenUtc">The marker's last write.</param>
/// <param name="ReadFault">Why the marker could not be read, or null. An unreadable marker ABORTS the plan.</param>
public sealed record ReleaseMarkerEntry(
    string Version, string? Identity, string Path, DateTimeOffset WrittenUtc, string? ReadFault)
{
    /// <summary>The marker's line (<see cref="PlatformVersionLine.LineOf"/>).</summary>
    public string? Line => PlatformVersionLine.LineOf(Version);

    /// <summary>True for a <c>-ci.&lt;n&gt;</c> (or any other pre-release) marker.</summary>
    public bool IsPrerelease => PlatformVersionLine.IsPrerelease(Version);
}

/// <summary>What one scan of the store found.</summary>
/// <param name="Identities">Every identity directory.</param>
/// <param name="Markers">Every release marker.</param>
public sealed record PrebuiltStoreScan(
    ImmutableList<PrebuiltIdentityEntry> Identities,
    ImmutableList<ReleaseMarkerEntry> Markers);

/// <summary>What a sweep decided, before any deletion.</summary>
/// <param name="LiveIdentity">The framework identity this process runs.</param>
/// <param name="LivePlatformVersion">This process's platform version, or null when unknown.</param>
/// <param name="Identities">Every identity found.</param>
/// <param name="Collectable">The identities no rule protects, oldest first.</param>
/// <param name="Protected">Why each surviving identity survived, keyed by identity.</param>
/// <param name="CollectableMarkers">The pre-release markers whose identity goes with this plan, or is already gone.</param>
/// <param name="AbortReason">Why NOTHING may be collected, or null when the plan is sound.</param>
/// <param name="UnresolvedPins">Pinned references whose version maps to no <c>_releases</c> marker and that name no identity — they abort cleanup because the consumer inventory is incomplete.</param>
public sealed record PrebuiltBundleSweepPlan(
    string LiveIdentity,
    string? LivePlatformVersion,
    ImmutableList<PrebuiltIdentityEntry> Identities,
    ImmutableList<PrebuiltIdentityEntry> Collectable,
    ImmutableDictionary<string, string> Protected,
    ImmutableList<ReleaseMarkerEntry> CollectableMarkers,
    string? AbortReason,
    ImmutableList<string> UnresolvedPins)
{
    /// <summary>Total bytes across every identity.</summary>
    public long TotalBytes => Identities.Sum(i => i.Bytes);

    /// <summary>Bytes the plan would reclaim.</summary>
    public long CollectableBytes => Collectable.Sum(i => i.Bytes);

    /// <summary>One line an operator can read.</summary>
    public string Summary =>
        $"{Identities.Count} identity directory(ies) / {Mb(TotalBytes)} — live={LiveIdentity}"
        + $" ({LivePlatformVersion ?? "version unknown"}), collectable={Collectable.Count} / {Mb(CollectableBytes)}"
        + $", markers to retire={CollectableMarkers.Count}"
        + (UnresolvedPins.IsEmpty ? "" : $", unresolved consumer references (cleanup blocked): {string.Join(", ", UnresolvedPins)}")
        + (AbortReason is null ? "" : $", ABORTED: {AbortReason}");

    internal static string Mb(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("N1", CultureInfo.InvariantCulture) + " MB";
}

/// <summary>The outcome of a sweep: what it planned and what it actually removed.</summary>
/// <param name="AtUtc">When the sweep ran.</param>
/// <param name="Plan">The plan, or null when the store could not be scanned.</param>
/// <param name="Deleted">Whether deletion was armed AND the plan was sound.</param>
/// <param name="DeletedIdentities">Identity directories actually removed.</param>
/// <param name="DeletedBytes">Bytes actually reclaimed.</param>
/// <param name="DeletedMarkers">Release markers actually removed.</param>
/// <param name="FailedDeletes">Removals that failed (they stay; the next pass re-plans them).</param>
/// <param name="AbortReason">Why the sweep collected nothing, or null.</param>
public sealed record PrebuiltBundleSweepResult(
    DateTimeOffset AtUtc,
    PrebuiltBundleSweepPlan? Plan,
    bool Deleted,
    int DeletedIdentities,
    long DeletedBytes,
    int DeletedMarkers,
    int FailedDeletes,
    string? AbortReason)
{
    /// <summary>One ledger line.</summary>
    public string LedgerLine =>
        $"{AtUtc:O} " + (Plan is null
            ? $"ABORTED before planning: {AbortReason}"
            : AbortReason is not null
                ? $"ABORTED: {AbortReason} ({Plan.Summary})"
                : Deleted
                    ? $"removed {DeletedIdentities} identity(ies) / {PrebuiltBundleSweepPlan.Mb(DeletedBytes)}, "
                      + $"{DeletedMarkers} marker(s){(FailedDeletes == 0 ? "" : $", {FailedDeletes} failed")} — {Plan.Summary}"
                    : $"report only — {Plan.Summary}");
}

/// <summary>
/// 🚨 THE PREBUILT-BUNDLE STORE GROWS BY ONE IDENTITY DIRECTORY PER CI BUILD, AND NOTHING USED TO
/// REMOVE ONE. Measured 2026-09-08 on memex.systemorph.com: 482 identity directories, 13398 MiB, on
/// a 16384 MiB share with 3 MiB free.
///
/// <para><b>The rule: prune what nothing references, never by age alone.</b> Stated once here and
/// applied to two stores — this one, and the container registry's pinned digests
/// (<c>Doc/Architecture/PinnedImageRetention</c>, #3438): <i>an artifact that anything pins, names,
/// runs or may adopt is kept regardless of age and regardless of how many newer ones exist; only
/// an artifact NOTHING references is collected, and an artifact whose references cannot be READ
/// counts as referenced.</i></para>
///
/// <para><b>What references an identity</b> (each is a KEEP; they are ORed):</para>
/// <list type="number">
/// <item>it is the framework identity <b>this process runs</b>;</item>
/// <item>a <b>clean release marker</b> (<c>_releases/X.Y.Z</c>, no pre-release label) names it —
/// support has not been established as ended, so the release stays available;</item>
/// <item>a <b>NodeType record's adoption stamp</b> (<c>CompiledFrameworkVersion</c>, written by
/// <c>PrebuiltAssemblySeeder</c> and by every local compile) names it;</item>
/// <item>it holds, for some source, the <b>newest sealed publication on each represented major line</b> —
/// exactly what <c>Modules:VersionStrictness=Family</c> adopts at boot;</item>
/// <item>its newest content write or release marker is younger than
/// <see cref="PrebuiltBundleRetention.MinimumAge"/> (at least 30 days), regardless of build count;</item>
/// <item>a source under it is <b>unsealed and younger than <see cref="PrebuiltBundleRetention.UnsealedGrace"/></b>
/// — a seal in flight;</item>
/// <item>its seal <b>could not be read</b> — unreadable is never unreferenced.</item>
/// </list>
///
/// <para>Everything else is collected, oldest first, one identity at a time, each removal logged
/// with the bytes reclaimed. The pre-release markers of a removed identity (and of an identity
/// already gone for longer than the age window) are removed with it, so <c>_releases/</c> does not grow
/// forever; a clean release marker is never removed. A release marker that cannot be read, or a
/// store that cannot be listed, ABORTS the sweep with nothing collected: a partial picture licenses
/// no deletion. This sweep is IDENTITY-level; the per-source generation retention that follows a
/// pointer swap is the publisher's (<c>Doc/Architecture/SealedPublicationGenerations</c>).</para>
/// </summary>
public static class PrebuiltBundleStore
{
    /// <summary>Directory under the root holding the retention ledger. Leading underscore: never an identity.</summary>
    public const string RetentionDirectoryName = "_retention";

    /// <summary>The append-only ledger of what each pass removed, one line per pass and one per identity.</summary>
    public const string LedgerFileName = "ledger.txt";

    /// <summary>
    /// Read the whole store: every identity directory (skipping the <c>_…</c> bookkeeping
    /// directories) with its sources' seals, and every release marker. Throws when the root or the
    /// marker directory cannot be listed — the caller aborts on that.
    /// </summary>
    public static PrebuiltStoreScan Scan(string root, ILogger? logger = null)
    {
        var identities = ImmutableList.CreateBuilder<PrebuiltIdentityEntry>();
        foreach (var identityDirectory in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(identityDirectory);
            if (string.IsNullOrEmpty(name) || name.StartsWith('_') || name.StartsWith('.'))
                continue;
            identities.Add(ReadIdentity(identityDirectory, name, logger));
        }

        var markers = ImmutableList.CreateBuilder<ReleaseMarkerEntry>();
        var markerDirectory = Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName);
        if (Directory.Exists(markerDirectory))
            foreach (var file in Directory.EnumerateFiles(markerDirectory).OrderBy(f => f, StringComparer.Ordinal))
            {
                var version = Path.GetFileName(file);
                var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                try
                {
                    var identity = File.ReadAllText(file).Trim();
                    markers.Add(new ReleaseMarkerEntry(version, identity.Length == 0 ? null : identity, file, written,
                        identity.Length == 0 ? "the marker is empty" : null));
                }
                catch (Exception ex)
                {
                    markers.Add(new ReleaseMarkerEntry(version, null, file, written, $"{ex.GetType().Name}: {ex.Message}"));
                }
            }

        return new PrebuiltStoreScan(identities.ToImmutable(), markers.ToImmutable());
    }

    private static PrebuiltIdentityEntry ReadIdentity(string identityDirectory, string identity, ILogger? logger)
    {
        var bytes = 0L;
        var newestWrite = new DateTimeOffset(Directory.GetLastWriteTimeUtc(identityDirectory), TimeSpan.Zero);
        foreach (var file in Directory.EnumerateFiles(identityDirectory, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            bytes += info.Length;
            var write = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (write > newestWrite) newestWrite = write;
        }

        var sources = ImmutableList.CreateBuilder<PrebuiltSourceEntry>();
        foreach (var sourceDirectory in Directory.EnumerateDirectories(identityDirectory).OrderBy(d => d, StringComparer.Ordinal))
        {
            var sourceName = Path.GetFileName(sourceDirectory);
            var sourceNewest = new DateTimeOffset(Directory.GetLastWriteTimeUtc(sourceDirectory), TimeSpan.Zero);
            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var write = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                if (write > sourceNewest) sourceNewest = write;
            }
            // 🚨 Composed under the RESOLVED publication directory (#3461): a pointer may name a
            // generation subdirectory, and the seal lives there, not beside the pointer.
            var publication = ShippedPrebuiltBundles.PublicationDirectoryOf(sourceDirectory, logger);
            var sentinel = Path.Combine(publication, ShippedPrebuiltBundles.CompletionSentinelFileName);
            if (!File.Exists(sentinel))
            {
                sources.Add(new PrebuiltSourceEntry(sourceName, false, null, sourceNewest, null));
                continue;
            }
            try
            {
                var sealUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(sentinel), TimeSpan.Zero);
                var torn = File.ReadAllLines(sentinel)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .Any(name => !File.Exists(Path.Combine(publication, name)));
                sources.Add(new PrebuiltSourceEntry(sourceName, !torn, torn ? null : sealUtc, sourceNewest, null));
            }
            catch (Exception ex)
            {
                // 🚨 Every read failure, not just IOException — a denied ACL is every bit as much
                // "I cannot evaluate this seal" as a locked file. It PROTECTS the identity.
                sources.Add(new PrebuiltSourceEntry(sourceName, false, null, sourceNewest, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }
        return new PrebuiltIdentityEntry(identity, identityDirectory, sources.ToImmutable(), bytes, newestWrite);
    }

    /// <summary>
    /// Decide what may be collected. Pure — no filesystem, no clock — so the rules are testable
    /// exactly as they are enforced.
    /// </summary>
    /// <param name="liveIdentity">The framework identity this process runs.</param>
    /// <param name="livePlatformVersion">This process's platform version (build metadata stripped), or null.</param>
    /// <param name="scan">What the store holds.</param>
    /// <param name="stampedIdentities">Every identity a NodeType record's <c>CompiledFrameworkVersion</c> names.</param>
    /// <param name="pinned">Every platform build a Deployment record pins or a registered instance reports (<see cref="PinnedPlatformReference"/>).</param>
    /// <param name="retention">The knobs.</param>
    /// <param name="nowUtc">The instant the grace is measured against.</param>
    public static PrebuiltBundleSweepPlan Plan(
        string liveIdentity,
        string? livePlatformVersion,
        PrebuiltStoreScan scan,
        ImmutableHashSet<string> stampedIdentities,
        ImmutableList<PinnedPlatformReference> pinned,
        PrebuiltBundleRetention retention,
        DateTimeOffset nowUtc)
    {
        var unreadable = scan.Markers.FirstOrDefault(m => m.ReadFault is not null);
        if (unreadable is not null)
            return new PrebuiltBundleSweepPlan(liveIdentity, livePlatformVersion, scan.Identities,
                ImmutableList<PrebuiltIdentityEntry>.Empty, ImmutableDictionary<string, string>.Empty,
                ImmutableList<ReleaseMarkerEntry>.Empty,
                $"release marker '{unreadable.Version}' could not be read ({unreadable.ReadFault}) — which "
                + "identity it references is unknown, so no identity can be called unreferenced",
                ImmutableList<string>.Empty);

        // 🚨 The SEALED-PUBLICATION LINEAGE, never the version LABEL (#3542). Retention decides what
        // to DELETE, so a label that sorts above a later run does not merely mis-rank a listing here:
        // it protects the stale publication and collects the newest one. `3.0.0-rc9.ci.7824` and the
        // withdrawn `3.1.0-ci.7841` both outrank `3.0.0-ci.8130` under SemVer.
        var comparer = PlatformReleaseOrder.Newest;
        var readable = scan.Markers.Where(m => m.Identity is not null).ToImmutableList();

        // identity → the newest version naming it (the SAME reading ShippedPrebuiltBundles orders
        // fallback candidates by: SealedPublicationIndex.ReleasesOf).
        var newestVersionOf = readable
            .GroupBy(m => m.Identity!, StringComparer.Ordinal)
            .ToImmutableDictionary(g => g.Key, g => g.Select(m => m.Version).OrderByDescending(v => v, comparer).First(),
                StringComparer.Ordinal);
        var cleanMarkersOf = readable
            .Where(m => !m.IsPrerelease)
            .GroupBy(m => m.Identity!, StringComparer.Ordinal)
            .ToImmutableDictionary(g => g.Key, g => string.Join(", ", g.Select(m => m.Version)), StringComparer.Ordinal);
        var reasons = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        void Keep(string identity, string reason)
        {
            if (!reasons.ContainsKey(identity))
                reasons[identity] = reason;
        }

        var sources = scan.Identities.SelectMany(i => i.Sources.Where(s => s.IsSealed).Select(s => s.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        var minimumAge = retention.MinimumAge < TimeSpan.FromDays(30)
            ? TimeSpan.FromDays(30) : retention.MinimumAge;

        // version (marker file name, normalised) → every identity a marker of that version names.
        var identitiesOfVersion = readable
            .GroupBy(m => PlatformVersionLine.Normalize(m.Version) ?? m.Version, StringComparer.Ordinal)
            .ToImmutableDictionary(g => g.Key, g => g.Select(m => m.Identity!).ToImmutableHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        var pinnedReasons = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var unresolvedPins = ImmutableList.CreateBuilder<string>();
        foreach (var pin in pinned)
        {
            // 🚨 A pinned build is referenced HOWEVER OLD: on the registry, the instance that pins it
            // pulls exactly that identity's seal over the HTTP prebuilt surface, and the newest-N
            // rule cannot see it. A reported identity is the exact reference; a version reaches its
            // identity only through the _releases marker — a version no marker names keeps NOTHING,
            // and the ledger says so, because nothing can say which directory it would have been.
            var version = PlatformVersionLine.Normalize(pin.Version);
            if (!string.IsNullOrWhiteSpace(pin.Identity))
                pinnedReasons.TryAdd(pin.Identity, $"pinned by {pin.Origin}{(version is null ? "" : $" ({version})")}");
            else if (version is not null && identitiesOfVersion.TryGetValue(version, out var ids))
                foreach (var id in ids)
                    pinnedReasons.TryAdd(id, $"pinned by {pin.Origin} at {version} (via its _releases marker)");
            else
                unresolvedPins.Add($"{pin.Origin}={version ?? "(no version)"}");
        }

        if (unresolvedPins.Count > 0)
            return new PrebuiltBundleSweepPlan(liveIdentity, livePlatformVersion, scan.Identities,
                [], ImmutableDictionary<string, string>.Empty, [],
                "consumer references could not be resolved; the protected inventory is incomplete",
                unresolvedPins.ToImmutable());

        foreach (var identity in scan.Identities)
        {
            if (string.Equals(identity.Identity, liveIdentity, StringComparison.Ordinal))
                Keep(identity.Identity, "the framework identity this process runs");
            else if (identity.HasReadFault)
                Keep(identity.Identity,
                    $"its seal could not be read ({identity.Sources.First(s => s.ReadFault is not null).ReadFault}) — unreadable is never unreferenced");
            else if (cleanMarkersOf.TryGetValue(identity.Identity, out var releases))
                Keep(identity.Identity, $"named by release marker {releases}");
            else if (pinnedReasons.TryGetValue(identity.Identity, out var pinnedBy))
                Keep(identity.Identity, pinnedBy);
            else if (stampedIdentities.Contains(identity.Identity))
                Keep(identity.Identity, "a NodeType record's adoption stamp (CompiledFrameworkVersion) names it");
        }

        foreach (var source in sources)
        {
            var sealedHere = scan.Identities
                .Where(i => i.Sources.Any(s => s.IsSealed && string.Equals(s.Name, source, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // Preserve the newest sealed publication for every represented major, including
            // consumers running a different major than this registry process (#3842).
            foreach (var family in sealedHere
                         .Where(i => newestVersionOf.ContainsKey(i.Identity))
                         .GroupBy(i => PrebuiltAdoptionPolicy.MajorOf(newestVersionOf[i.Identity]))
                         .Where(g => g.Key is not null))
            {
                var latest = family.OrderByDescending(i => newestVersionOf[i.Identity], comparer).First();
                Keep(latest.Identity,
                    $"the newest sealed '{source}' publication on platform major {family.Key} ({newestVersionOf[latest.Identity]}) — what Family strictness adopts");
            }
        }

        // An age window applies to sealed, unnamed and abandoned publications alike.
        // A recent marker is also a publication reference: old bytes can just have been released.
        foreach (var identity in scan.Identities)
            if (nowUtc - identity.NewestWriteUtc < minimumAge
                || readable.Any(m => m.Identity == identity.Identity && nowUtc - m.WrittenUtc < minimumAge))
                Keep(identity.Identity, $"within the {minimumAge.TotalDays:N0}-day retention window");

        // 7. a seal in flight.
        foreach (var identity in scan.Identities)
            if (identity.HasUnsealedSource && nowUtc - identity.NewestWriteUtc < retention.UnsealedGrace)
                Keep(identity.Identity,
                    $"a source is unsealed and its newest write is {(nowUtc - identity.NewestWriteUtc).TotalMinutes:N0} min old — a seal may be in flight (grace {retention.UnsealedGrace})");

        var collectable = scan.Identities
            .Where(i => !reasons.ContainsKey(i.Identity))
            .OrderBy(i => i.OrderingInstant)
            .ToImmutableList();
        var collectableIdentities = collectable.Select(i => i.Identity).ToImmutableHashSet(StringComparer.Ordinal);
        var present = scan.Identities.Select(i => i.Identity).ToImmutableHashSet(StringComparer.Ordinal);

        var pinnedVersions = pinned
            .Select(p => PlatformVersionLine.Normalize(p.Version))
            .Where(v => v is not null)
            .Select(v => v!)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var markers = readable
            .Where(m => m.IsPrerelease)
            .Where(m => !string.Equals(m.Identity, liveIdentity, StringComparison.Ordinal))
            .Where(m => livePlatformVersion is null || !string.Equals(m.Version, livePlatformVersion, StringComparison.Ordinal))
            // A pinned version's marker is the ONLY way its pin reaches an identity — never retired,
            // even when the directory is absent right now (a republish would need the marker).
            .Where(m => !pinnedVersions.Contains(PlatformVersionLine.Normalize(m.Version) ?? m.Version)
                        && !pinnedReasons.ContainsKey(m.Identity!))
            .Where(m => collectableIdentities.Contains(m.Identity!)
                        || (!present.Contains(m.Identity!) && nowUtc - m.WrittenUtc >= minimumAge))
            .ToImmutableList();

        return new PrebuiltBundleSweepPlan(
            liveIdentity, livePlatformVersion, scan.Identities, collectable, reasons.ToImmutable(), markers, null,
            unresolvedPins.ToImmutable());
    }

    /// <summary>
    /// Scan, plan, and — when <see cref="PrebuiltBundleRetention.Delete"/> is armed — collect, on
    /// the I/O pool. The stamps are the caller's (they come from the mesh, which is not a
    /// filesystem read).
    /// </summary>
    public static IObservable<PrebuiltBundleSweepResult> Sweep(
        string root,
        string liveIdentity,
        string? livePlatformVersion,
        ImmutableHashSet<string> stampedIdentities,
        ImmutableList<PinnedPlatformReference> pinned,
        PrebuiltBundleRetention retention,
        IIoPool pool,
        ILogger? logger = null)
        => pool.InvokeBlocking(_ =>
            SweepCore(root, liveIdentity, livePlatformVersion, stampedIdentities, pinned, retention, DateTimeOffset.UtcNow, logger));

    /// <summary>The sweep itself, with the clock passed in, so the deleting behaviour is exercised at an exact instant.</summary>
    public static PrebuiltBundleSweepResult SweepCore(
        string root,
        string liveIdentity,
        string? livePlatformVersion,
        ImmutableHashSet<string> stampedIdentities,
        ImmutableList<PinnedPlatformReference> pinned,
        PrebuiltBundleRetention retention,
        DateTimeOffset nowUtc,
        ILogger? logger = null,
        Action<string>? deleteDirectory = null)
    {
        PrebuiltStoreScan scan;
        try
        {
            if (!Directory.Exists(root))
            {
                var absent = new PrebuiltBundleSweepResult(nowUtc, null, false, 0, 0, 0, 0,
                    $"the published bundle root '{root}' does not exist");
                logger?.LogWarning("PrebuiltBundleRetention: {Reason} — nothing collected", absent.AbortReason);
                return absent;
            }
            scan = Scan(root, logger);
        }
        catch (Exception ex)
        {
            // 🚨 FAIL CLOSED: a partial listing is not a picture of the store, so it licenses nothing.
            logger?.LogWarning(ex,
                "PrebuiltBundleRetention: could not read {Root} — NOTHING collected. A store that cannot be "
                + "enumerated completely can never be pruned safely", root);
            var failed = new PrebuiltBundleSweepResult(nowUtc, null, false, 0, 0, 0, 0, $"store could not be read: {ex.Message}");
            AppendLedger(root, failed, logger);
            return failed;
        }

        var plan = Plan(liveIdentity, livePlatformVersion, scan, stampedIdentities, pinned, retention, nowUtc);
        if (plan.AbortReason is not null)
        {
            logger?.LogWarning("PrebuiltBundleRetention: {Summary}. NOTHING collected", plan.Summary);
            var aborted = new PrebuiltBundleSweepResult(nowUtc, plan, false, 0, 0, 0, 0, plan.AbortReason);
            AppendLedger(root, aborted, logger);
            return aborted;
        }

        if (!retention.Delete)
        {
            logger?.LogInformation(
                "PrebuiltBundleRetention: {Summary}. DELETING NOTHING — collection is not armed. Would collect: {Collectable}. Kept: {Kept}",
                plan.Summary,
                plan.Collectable.Count == 0 ? "(nothing)" : string.Join(", ", plan.Collectable.Select(i => $"{i.Identity} ({PrebuiltBundleSweepPlan.Mb(i.Bytes)})")),
                Describe(plan.Protected));
            var report = new PrebuiltBundleSweepResult(nowUtc, plan, false, 0, 0, 0, 0, null);
            AppendLedger(root, report, logger);
            return report;
        }

        var delete = deleteDirectory ?? (dir => Directory.Delete(dir, recursive: true));
        var deletedIdentities = 0;
        var deletedBytes = 0L;
        var failedDeletes = 0;
        var lines = new List<string>();
        foreach (var identity in plan.Collectable)
        {
            try
            {
                // Unseal first: a reader that lists the directory mid-removal then sees "no
                // sentinel" and backs off, instead of a sealed listing whose bundles are vanishing.
                foreach (var source in identity.Sources)
                {
                    var publication = ShippedPrebuiltBundles.PublicationDirectoryOf(Path.Combine(identity.Directory, source.Name), logger);
                    var sentinel = Path.Combine(publication, ShippedPrebuiltBundles.CompletionSentinelFileName);
                    if (File.Exists(sentinel))
                        File.Delete(sentinel);
                }
                delete(identity.Directory);
                deletedIdentities++;
                deletedBytes += identity.Bytes;
                logger?.LogInformation(
                    "PrebuiltBundleRetention: removed identity {Identity} ({Sources}) — {Bytes} reclaimed; sealed {SealUtc}",
                    identity.Identity, string.Join(",", identity.Sources.Select(s => s.Name)),
                    PrebuiltBundleSweepPlan.Mb(identity.Bytes), identity.NewestSealUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "never");
                lines.Add($"{nowUtc:O}   removed {identity.Identity} {PrebuiltBundleSweepPlan.Mb(identity.Bytes)} sources={string.Join(",", identity.Sources.Select(s => s.Name))}");
            }
            catch (Exception ex)
            {
                // Surfaced, not swallowed: the directory (or what is left of it) stays, and the next
                // pass re-plans it — a half-removed identity reads as unsealed to every reader.
                failedDeletes++;
                logger?.LogWarning(ex,
                    "PrebuiltBundleRetention: could not remove {Directory} — it stays, and the next pass considers it again",
                    identity.Directory);
            }
        }

        var deletedMarkers = 0;
        foreach (var marker in plan.CollectableMarkers)
        {
            try
            {
                File.Delete(marker.Path);
                deletedMarkers++;
                lines.Add($"{nowUtc:O}   retired marker {marker.Version} → {marker.Identity}");
            }
            catch (Exception ex)
            {
                failedDeletes++;
                logger?.LogWarning(ex, "PrebuiltBundleRetention: could not remove release marker {Path}", marker.Path);
            }
        }

        logger?.LogInformation(
            "PrebuiltBundleRetention: {Summary}. COLLECTED {Identities} identity(ies) reclaiming {Reclaimed}, retired {Markers} marker(s){Failed}. Kept: {Kept}",
            plan.Summary, deletedIdentities, PrebuiltBundleSweepPlan.Mb(deletedBytes), deletedMarkers,
            failedDeletes == 0 ? "" : $" ({failedDeletes} removal(s) failed)",
            Describe(plan.Protected));

        var result = new PrebuiltBundleSweepResult(nowUtc, plan, true, deletedIdentities, deletedBytes, deletedMarkers, failedDeletes, null);
        AppendLedger(root, result, logger, lines);
        return result;
    }

    private static string Describe(ImmutableDictionary<string, string> reasons) =>
        reasons.IsEmpty
            ? "(nothing)"
            : string.Join(" | ", reasons.OrderBy(kvp => kvp.Key, StringComparer.Ordinal).Select(kvp => $"{kvp.Key} — {kvp.Value}"));

    /// <summary>The ledger path under a root.</summary>
    public static string LedgerPathOf(string root) => Path.Combine(root, RetentionDirectoryName, LedgerFileName);

    private static void AppendLedger(string root, PrebuiltBundleSweepResult result, ILogger? logger, IReadOnlyList<string>? detail = null)
    {
        try
        {
            var path = LedgerPathOf(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllLines(path, (detail ?? []).Prepend(result.LedgerLine));
        }
        catch (Exception ex)
        {
            // The ledger is a record of the pass, not a precondition of it; a share that cannot take
            // one more line is exactly the condition the pass exists to relieve.
            logger?.LogWarning(ex, "PrebuiltBundleRetention: could not append to the ledger under {Root}", root);
        }
    }
}
