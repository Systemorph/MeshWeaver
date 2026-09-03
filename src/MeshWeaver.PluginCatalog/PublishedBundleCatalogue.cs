using System.Collections.Immutable;
using System.IO.Compression;
using System.Reactive.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MeshWeaver.Compiler;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The OBSERVATION half of <see cref="ReleaseAvailability"/>: reads the published bundle root — the
/// shared storage every bake lane writes to and every portal mounts — and answers, for one target
/// release, "which framework identity is it, and which bundles are sealed under that identity".
///
/// <para>The layout is the publisher's contract, mirrored here exactly once:</para>
/// <code>
/// &lt;root&gt;/_releases/&lt;platform-version&gt;      → a file holding that release's framework identity
/// &lt;root&gt;/&lt;identity&gt;/&lt;source&gt;/&lt;bundle&gt;.zip   → the bundles
/// &lt;root&gt;/&lt;identity&gt;/&lt;source&gt;/_complete      → the seal, written strictly LAST
/// </code>
///
/// <para>🚨 <b>The release marker is what makes "the target release's identity" knowable at all.</b>
/// A framework identity is a property of the BINARIES (#1725) — it is resolved by the image, so
/// nothing outside that image can compute it. Rather than guess, the publisher RECORDS it:
/// <c>publish-bake-bundles.sh</c> writes <c>_releases/&lt;version&gt;</c> on every run, keyed by the
/// platform version the self-updater actually compares. No marker therefore means one precise
/// thing — that release published no platform content bake — and the honest answer is
/// <see cref="PackageAvailabilityKind.Indeterminate"/>, i.e. HOLD.</para>
///
/// <para>Sealing is read through <see cref="ShippedPrebuiltBundles.CompletionSentinelFileName"/>
/// with the same rule the boot seeder applies: a source directory counts only when its sentinel is
/// present AND every bundle it lists exists. Anything less is a torn publication that the seeder
/// would skip, so the gate must not count it either — otherwise the gate would clear a release the
/// portal then recompiles.</para>
/// </summary>
public static class PublishedBundleCatalogue
{
    /// <summary>
    /// The directory under the published root holding one marker file per platform release,
    /// named by version, containing that release's framework identity. Leading underscore so it
    /// can never collide with a framework-identity directory (those are <c>s…</c>/<c>g…</c>).
    /// Mirrored in <c>.github/scripts/publish-bake-bundles.sh</c>; the pairing is pinned by
    /// <c>PlatformBakeLaneGuard</c>.
    /// </summary>
    public const string ReleaseMarkerDirectoryName = "_releases";

    /// <summary>The configuration key naming the published bundle root — forwarded from
    /// <see cref="ShippedPrebuiltBundles.PublishedRootConfigKey"/> so a consumer in the portal
    /// layer can address the same directory without referencing the hosting assembly.</summary>
    public const string PublishedRootConfigKey = ShippedPrebuiltBundles.PublishedRootConfigKey;

    /// <summary>
    /// Reads the catalogue for one target release. Synchronous and total — every failure becomes a
    /// <see cref="ReleaseArtifacts.Unreadable"/> observation rather than an exception, because the
    /// caller's fail-safe answer to "could not read" is already HOLD, and a throw would turn a
    /// hold into a crashed poller.
    /// </summary>
    /// <param name="publishedRoot">The published bundle root
    /// (<see cref="ShippedPrebuiltBundles.PublishedRootConfigKey"/>), or null when this deployment
    /// does not consume CI bakes.</param>
    /// <param name="targetVersion">The platform version being rolled to.</param>
    /// <param name="logger">Diagnostics.</param>
    public static ReleaseObservation Read(string? publishedRoot, string? targetVersion, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
            return new ReleaseObservation(
                new ReleaseTarget(targetVersion, null),
                ReleaseArtifacts.Unreadable("no target release version was given"));

        if (string.IsNullOrWhiteSpace(publishedRoot))
            return new ReleaseObservation(
                new ReleaseTarget(targetVersion, null),
                ReleaseArtifacts.Unreadable(
                    $"no published bundle root is configured ({ShippedPrebuiltBundles.PublishedRootConfigKey})"));

        try
        {
            var markerPath = Path.Combine(publishedRoot, ReleaseMarkerDirectoryName, targetVersion);
            if (!File.Exists(markerPath))
            {
                logger?.LogInformation(
                    "ReleaseAvailability: release {Version} has no marker under {Root}/{Markers} — "
                    + "its framework identity is unknown, so no package can be shown adoptable",
                    targetVersion, publishedRoot, ReleaseMarkerDirectoryName);
                return new ReleaseObservation(
                    new ReleaseTarget(targetVersion, null), ReleaseArtifacts.Of([]));
            }

            var identity = File.ReadAllText(markerPath).Trim();
            if (identity.Length == 0)
                return new ReleaseObservation(
                    new ReleaseTarget(targetVersion, null),
                    ReleaseArtifacts.Unreadable(
                        $"the release marker for {targetVersion} is empty"));

            if (!Directory.Exists(Path.Combine(publishedRoot, identity)))
            {
                logger?.LogInformation(
                    "ReleaseAvailability: release {Version} resolves framework identity {Identity}, "
                    + "but nothing is published under it — every package would be recompiled",
                    targetVersion, identity);
                return new ReleaseObservation(
                    new ReleaseTarget(targetVersion, identity), ReleaseArtifacts.Of([]));
            }

            return new ReleaseObservation(
                new ReleaseTarget(targetVersion, identity),
                ArtifactsForIdentity(Path.Combine(publishedRoot, identity), logger));
        }
        catch (Exception ex)
        {
            // Fail SAFE and NAMED: an IO fault against the share is an availability incident, and
            // the caller must be able to tell it apart from an incompatible release.
            logger?.LogWarning(ex,
                "ReleaseAvailability: could not read the published bundle root {Root} for release "
                + "{Version}", publishedRoot, targetVersion);
            return new ReleaseObservation(
                new ReleaseTarget(targetVersion, null),
                ReleaseArtifacts.Unreadable(ex.Message));
        }
    }

    /// <summary>Reactive form — the file-system leaf runs on the caller's I/O pool, never on a hub
    /// action block.</summary>
    public static IObservable<ReleaseObservation> Observe(
        IIoPool pool, string? publishedRoot, string? targetVersion, ILogger? logger = null) =>
        pool.InvokeBlocking(_ => Read(publishedRoot, targetVersion, logger));

    /// <summary>
    /// Every bundle sealed under one framework-identity directory, across all producing sources —
    /// bundle IDS, extension stripped, case-insensitive. Same completeness contract as the boot
    /// seeder: sentinel present, every listed bundle on disk, else the whole source contributes
    /// nothing (the seeder would skip it, so a gate that counted it would clear a release the
    /// portal then recompiles).
    ///
    /// <para>Public because it is also the deployment gate's "what am I adopting TODAY" reading —
    /// asked of the RUNNING identity to decide which installed packages are content-bearing at
    /// all, which is what keeps the gate a regression check instead of a permanent freeze.</para>
    /// </summary>
    public static ImmutableHashSet<string> SealedBundlesForIdentity(
        string? publishedRoot, string? identity, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(publishedRoot) || string.IsNullOrWhiteSpace(identity))
            return ReleaseArtifacts.Of([]).SealedBundles;
        var identityDirectory = Path.Combine(publishedRoot, identity);
        if (!Directory.Exists(identityDirectory))
            return ReleaseArtifacts.Of([]).SealedBundles;
        return ReleaseArtifacts.Of(SealedBundleNames(identityDirectory, logger)).SealedBundles;
    }

    /// <summary>
    /// 🚨 The FULL observation of one identity (#3175): the sealed bundle ids, the module set every
    /// complete source sealed (each module bundle's entry assembly read for its MVID, spelt as the
    /// dependency records spell it), and every sealed bundle's per-NodeType dependency record. The
    /// bytes were always on disk — <see cref="SealedModulesOf"/> already refused a torn set — and
    /// <see cref="ReleaseAvailability"/> can only assert CONSISTENCY when it is handed both halves.
    /// Reads manifests and one PE header per module; never a bundle's assembly bytes.
    /// </summary>
    /// <remarks>A module bundle that cannot be read, or a source sealed before module sealing
    /// existed, becomes the set's <see cref="SealedModuleSet.Refusal"/> — a named unreadability the
    /// gate turns into a HOLD — and never a silent omission that would read as "no module here".</remarks>
    internal static ReleaseArtifacts ArtifactsForIdentity(string identityDirectory, ILogger? logger)
    {
        var bundles = new List<string>();
        var records = ImmutableArray.CreateBuilder<BundleDependencyRecord>();
        var sealedBy = new Dictionary<string, (string Mvid, string Source)>(StringComparer.Ordinal);
        var conflicts = ImmutableArray.CreateBuilder<string>();
        var refusals = new List<string>();

        foreach (var sourceDirectory in Directory.EnumerateDirectories(identityDirectory)
                     .OrderBy(d => d, StringComparer.Ordinal))
        {
            var complete = CompleteBundlesOf(sourceDirectory, logger);
            if (complete is null)
                continue;
            var source = Path.GetFileName(sourceDirectory)!;
            bundles.AddRange(complete);
            foreach (var bundle in complete)
                records.AddRange(DependencyRecordsOf(Path.Combine(sourceDirectory, bundle), bundle));

            var modules = SealedModulesOf(sourceDirectory, logger);
            if (modules.Modules is null)
            {
                refusals.Add($"source '{source}': {modules.Refusal}");
                continue;
            }
            foreach (var moduleBundle in modules.Modules)
            {
                var path = Path.Combine(sourceDirectory, ModulesDirectoryName, moduleBundle);
                string name, mvid;
                try
                {
                    (name, mvid) = SealedModuleMvidOf(path);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or BadImageFormatException or InvalidOperationException)
                {
                    refusals.Add($"source '{source}': module bundle '{moduleBundle}' is unreadable — {ex.Message}");
                    continue;
                }
                if (sealedBy.TryGetValue(name, out var existing))
                {
                    if (!string.Equals(existing.Mvid, mvid, StringComparison.Ordinal))
                        conflicts.Add(
                            $"module {name}: source '{existing.Source}' sealed {existing.Mvid}, "
                            + $"source '{source}' sealed {mvid}");
                    continue;
                }
                sealedBy[name] = (mvid, source);
            }
        }

        var conflicted = conflicts.Select(c => c[..c.IndexOf(':')]["module ".Length..]).ToHashSet(StringComparer.Ordinal);
        return new ReleaseArtifacts(ReleaseArtifacts.Of(bundles).SealedBundles)
        {
            Modules = new SealedModuleSet(
                sealedBy.Where(kv => !conflicted.Contains(kv.Key))
                    .ToImmutableDictionary(kv => kv.Key, kv => kv.Value.Mvid, StringComparer.Ordinal),
                conflicts.ToImmutable(),
                refusals.Count == 0 ? null : string.Join("; ", refusals)),
            DependencyRecords = records.ToImmutable(),
        };
    }

    /// <summary>
    /// A sealed module bundle's entry assembly and its MVID in the dependency-record spelling
    /// (<c>mvid:&lt;N&gt;</c> — <c>NodeTypeCompilationHelpers.ModuleMvidsOf</c>'s projection, so the
    /// gate compares the id the seeder will compute). The entry is the manifest's
    /// <c>module.assemblyName</c>, else the single assembly under the module folder — the same
    /// resolution the lanes apply when they compose the bundle.
    /// </summary>
    internal static (string Name, string Mvid) SealedModuleMvidOf(string moduleBundlePath)
    {
        var manifest = BundleReader.ReadManifest(moduleBundlePath);
        using var file = File.OpenRead(moduleBundlePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var name = manifest?.Module?.AssemblyName;
        if (string.IsNullOrWhiteSpace(name))
        {
            var candidates = archive.Entries
                .Where(e => e.FullName.StartsWith(NuGetPackageWriter.ModuleFolder + "/", StringComparison.Ordinal)
                            && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            && !e.FullName[(NuGetPackageWriter.ModuleFolder.Length + 1)..].Contains('/'))
                .ToList();
            if (candidates.Count != 1)
                throw new InvalidOperationException(
                    $"cannot identify the module's entry assembly (no manifest module.assemblyName; "
                    + $"{NuGetPackageWriter.ModuleFolder}/ holds {candidates.Count} dll(s))");
            name = Path.GetFileNameWithoutExtension(candidates[0].FullName);
        }
        var entry = archive.GetEntry($"{NuGetPackageWriter.ModuleFolder}/{name}.dll")
            ?? throw new InvalidOperationException(
                $"{NuGetPackageWriter.ModuleFolder}/{name}.dll is missing from the bundle");
        using var stream = entry.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        bytes.Position = 0;
        using var pe = new PEReader(bytes);
        var metadata = pe.GetMetadataReader();
        var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        return (name, CompiledDependencies.MvidScheme + mvid.ToString("N"));
    }

    private static IEnumerable<BundleDependencyRecord> DependencyRecordsOf(string bundlePath, string bundleFileName)
    {
        var id = bundleFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? bundleFileName[..^4]
            : bundleFileName;
        var manifest = BundleReader.ReadManifest(bundlePath);
        foreach (var assembly in manifest?.Assemblies ?? [])
        {
            if (assembly.Dependencies is null || string.IsNullOrEmpty(assembly.NodePath))
                continue;
            yield return new BundleDependencyRecord(
                id, assembly.NodePath,
                assembly.Dependencies.ToImmutableDictionary(StringComparer.Ordinal));
        }
    }

    private static IEnumerable<string> SealedBundleNames(string identityDirectory, ILogger? logger)
    {
        foreach (var sourceDirectory in Directory.EnumerateDirectories(identityDirectory))
            foreach (var name in CompleteBundlesOf(sourceDirectory, logger) ?? [])
                yield return name;
    }

    /// <summary>
    /// 🚨 THE completeness rule, in ONE place: the bundle names a source directory lists in its
    /// sentinel when — and only when — the sentinel is present AND every bundle it lists is on
    /// disk. <c>null</c> means the publication is torn and the source counts for nothing.
    ///
    /// <para>Both readings go through here on purpose. A per-source answer that keyed on the
    /// sentinel alone (and a per-bundle answer that did not) would disagree about the same
    /// directory, and the one that said "ready" would be the optimistic one — which is precisely
    /// the direction a gate must never be wrong in.</para>
    /// </summary>
    /// <summary>
    /// The bundle names one source's SEALED publication lists, or null when the publication is
    /// torn (no sentinel, or a listed bundle absent). This is THE rule for "what may be served
    /// from this directory" — the registry's prebuilt route reads through it so a consumer can
    /// never be handed a torn set the boot seeder itself would refuse.
    /// </summary>
    public static IReadOnlyList<string>? SealedBundlesOf(string sourceDirectory, ILogger? logger = null)
        => CompleteBundlesOf(sourceDirectory, logger);

    private static IReadOnlyList<string>? CompleteBundlesOf(string sourceDirectory, ILogger? logger)
    {
        var sentinel = Path.Combine(
            sourceDirectory, ShippedPrebuiltBundles.CompletionSentinelFileName);
        if (!File.Exists(sentinel))
        {
            logger?.LogInformation(
                "ReleaseAvailability: {SourceDirectory} carries no {Sentinel} — the publication "
                + "is torn, so its bundles do not count as available",
                sourceDirectory, ShippedPrebuiltBundles.CompletionSentinelFileName);
            return null;
        }

        var listed = File.ReadAllLines(sentinel)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (listed.Any(name => !File.Exists(Path.Combine(sourceDirectory, name))))
        {
            logger?.LogInformation(
                "ReleaseAvailability: {SourceDirectory} is sealed but a listed bundle is "
                + "absent — the publication is torn, so its bundles do not count as available",
                sourceDirectory);
            return null;
        }

        return listed;
    }

    private static bool IsComplete(string sourceDirectory, ILogger? logger) =>
        CompleteBundlesOf(sourceDirectory, logger) is not null;

    /// <summary>
    /// Which sources are COMPLETE under one identity — the BUILD gate's question (#1755), asked per
    /// producing repo rather than per package.
    ///
    /// <para>🚨 "Sealed" is the sentinel AND every bundle it lists, exactly as
    /// <see cref="SealedBundlesForIdentity"/> and the boot seeder judge it. Keying on the sentinel
    /// alone would call a torn-beyond-the-seal publication ready and let a downstream repo build
    /// against an upstream whose bytes are not all there — while the portal that later reads the
    /// same directory would skip it whole. The two readings must never disagree.</para>
    /// </summary>
    public static IReadOnlySet<string> SealedSources(
        string? publishedRoot, string? identity, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(publishedRoot) || string.IsNullOrWhiteSpace(identity))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identityDirectory = Path.Combine(publishedRoot, identity);
        if (!Directory.Exists(identityDirectory))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateDirectories(identityDirectory)
            .Where(d => IsComplete(d, logger))
            .Select(d => Path.GetFileName(d)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The sub-directory of a sealed publication holding the module bundles its bake
    /// composed, and the index that lists them. Mirrored in
    /// <c>.github/scripts/publish-bake-bundles.sh</c> and <c>compose-sealed-modules.sh</c>.</summary>
    public const string ModulesDirectoryName = "modules";

    /// <summary>The module set's own listing — written strictly before the publication's
    /// <c>_complete</c>, so a sealed publication either carries its module set or predates
    /// module sealing altogether.</summary>
    public const string ModulesIndexFileName = "_index";

    /// <summary>
    /// 🚨 The module bundles one source's SEALED publication composed — the exact bytes its
    /// NodeType assemblies were built against (MeshWeaver#2698). A consumer pinned to this
    /// identity must compose THESE; the registry's package endpoint serves the module's own
    /// lane's last build under an unmoved version, and a gate that composed it judged assemblies
    /// built against one <c>MeshWeaver.AI</c> while holding another — the boot seeder declined
    /// every one ("dependency record mismatch"), fleet-wide, with no diff in any repo.
    ///
    /// <para><see cref="ModuleSetReading.Modules"/> is <c>null</c> with a
    /// <see cref="ModuleSetReading.Refusal"/> when the publication is torn (no seal, or a listed
    /// bundle absent — the same rule as <see cref="SealedBundlesOf"/>), when it PREDATES module
    /// sealing (no <c>modules/_index</c>: republish the source, which
    /// <c>publish-bake-bundles.sh</c> does on its next run), or when the index names a bundle
    /// that is absent or not a bare name. It is EMPTY, not null, when the bake composed nothing —
    /// a reader can tell "composed nothing" from "predates module sealing".</para>
    /// </summary>
    public static ModuleSetReading SealedModulesOf(string sourceDirectory, ILogger? logger = null)
    {
        if (CompleteBundlesOf(sourceDirectory, logger) is null)
            return new(null, "no sealed publication");
        var directory = Path.Combine(sourceDirectory, ModulesDirectoryName);
        var index = Path.Combine(directory, ModulesIndexFileName);
        if (!File.Exists(index))
        {
            logger?.LogInformation(
                "ReleaseAvailability: {SourceDirectory} is sealed but carries no {Modules}/{Index} — "
                + "it predates module sealing, so a consumer cannot compose from it until it is republished",
                sourceDirectory, ModulesDirectoryName, ModulesIndexFileName);
            return new(null,
                $"the publication is sealed but carries no module set ({ModulesDirectoryName}/{ModulesIndexFileName}) — "
                + "it predates module sealing; republish the source under a platform that seals module sets");
        }
        var listed = File.ReadAllLines(index)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        var bad = listed.FirstOrDefault(name =>
            name.IndexOfAny(['/', '\\', ':']) >= 0 || name == "." || name == ".."
            || !File.Exists(Path.Combine(directory, name)));
        if (bad is not null)
        {
            logger?.LogInformation(
                "ReleaseAvailability: {SourceDirectory} lists module bundle '{Bundle}' that is absent "
                + "or not a bare name — the module set is torn, so a consumer cannot compose from it",
                sourceDirectory, bad);
            return new(null, $"the module set is torn: '{bad}' is listed in {ModulesDirectoryName}/{ModulesIndexFileName} but absent (or not a bare name)");
        }
        return new(listed, null);
    }
}

/// <summary>One reading of a sealed publication's module set: the listed bundle names, or
/// <c>null</c> with the reason a consumer must not compose from it.</summary>
public sealed record ModuleSetReading(IReadOnlyList<string>? Modules, string? Refusal);

/// <summary>One reading of the catalogue: what the target release turned out to be, and what was
/// published for it.</summary>
/// <param name="Target">The release, with its framework identity resolved (or not).</param>
/// <param name="Artifacts">What is sealed for it.</param>
public sealed record ReleaseObservation(ReleaseTarget Target, ReleaseArtifacts Artifacts);
