using System.Collections.Immutable;
using System.IO.Compression;
using System.Reactive.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using MeshWeaver.Compiler;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
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
/// &lt;root&gt;/_releases/&lt;platform-version&gt;          → a file holding that release's framework identity
/// &lt;root&gt;/&lt;identity&gt;/&lt;source&gt;/&lt;bundle&gt;.zip       → the bundles
/// &lt;root&gt;/&lt;identity&gt;/&lt;source&gt;/platform-surface.json → the target platform's type surface (#3651)
/// &lt;root&gt;/&lt;identity&gt;/&lt;source&gt;/_complete          → the seal, written strictly LAST
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
    /// 🚨 The TARGET platform's type surface, published beside <c>_complete</c> by
    /// <c>publish-bake-bundles.sh</c> (#3651) — forwarded from
    /// <see cref="ModulePlatformSurface.PublishedFileName"/> so the producer (the bake), the
    /// publisher (the script) and this reader name one file. The bake runs INSIDE the target image,
    /// which is the only process that can write what that platform carries; the gate reads it back
    /// to link a landed module against a platform not running anywhere it can reach.
    /// </summary>
    public const string PlatformSurfaceFileName = ModulePlatformSurface.PublishedFileName;

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
    /// <para>🚨 <b>This was the deployment gate's denominator, and it must never be that again
    /// (#3441).</b> Asked of the RUNNING identity to decide which installed packages are
    /// content-bearing, it made the expected set a function of the artifact store the gate was
    /// about to judge: a package whose bake broke left the set and every later roll was green
    /// about it. The gate now reads
    /// <see cref="EverSealedBundles(string?, ILogger?)"/> instead. This overload answers the
    /// narrower factual question — <i>what is sealed under THIS one identity</i> — which remains a
    /// legitimate reading (it is what the boot seeder effectively resolves, and what
    /// <c>ReleaseGateDenominatorTest</c> uses to reproduce the old verdict as its negative
    /// control). It has no production caller today; kept public deliberately rather than deleted,
    /// because a public surface here can have in-mesh and satellite callers the compiler cannot
    /// see.</para>
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
    /// 🚨 <b>The deployment gate's DENOMINATOR, and it must not come from the artifact under
    /// judgement (#3441).</b> Every bundle id this root has EVER sealed, across every framework
    /// identity it holds — plus how many identities actually carried a sealed publication, so a
    /// caller can tell "this root serves no bakes" from "this root serves bakes and this package
    /// is not among them".
    ///
    /// <para><b>Why not simply ask the LIVE identity.</b> That is what the gate used to do, and it
    /// is the vacuous-denominator shape this repo forbids elsewhere: a package counted as
    /// content-bearing exactly when it had a sealed bundle under the identity running NOW, read
    /// from the same store the gate was about to judge. So a package whose bake BROKE silently
    /// left the denominator and every later roll was green about it — the gate stopped asking
    /// about precisely the package that had stopped being baked. In the limit, an identity with no
    /// publication at all made every installed package non-content-bearing and the content half of
    /// the gate checked NOTHING while reporting a pass, which is "0 expected, 0 found, green".
    /// That is the state the maintainer named: <i>"we kept rolling without edu being properly
    /// baked"</i>.</para>
    ///
    /// <para><b>Why this is independent.</b> The judgement is about the TARGET release's identity
    /// directory; the denominator is read from the OTHER identity directories — publications made
    /// by earlier CD waves, which the run under judgement cannot have written. And it is MONOTONE:
    /// a package that has once shipped a bake can never silently leave the denominator, so a bake
    /// that regresses to nothing is a HOLD rather than an exemption.</para>
    ///
    /// <para><b>Why it still cannot freeze an environment.</b> A package that has never sealed a
    /// bundle under any identity — a module-only or NodeType-less package, which produces no
    /// bundle ever — is still not demanded, exactly as before. The exemption is preserved; only
    /// its EVIDENCE moved from "one identity's answer today" to "any identity's answer ever".</para>
    ///
    /// <para>🚨 <b>The cost of monotone, stated honestly.</b> A package that legitimately STOPS
    /// shipping content — its last NodeType removed while it stays installed — produces no bundle
    /// for the target identity and will hold, and keep holding; uninstalling it clears the hold
    /// (the outer set is the environment's install records), re-baking does not. That is a
    /// deliberate trade against the erosion bug, and the direction is chosen: the old failure was
    /// SILENT (rolling onto content that was not there), this one is LOUD — named on the policy
    /// node and re-evaluated every tick. A gate that is visibly wrong can be acted on; one that is
    /// invisibly wrong cannot.</para>
    ///
    /// <para>🚨 <b>It reads each source's sentinel DECLARATION, not a per-bundle presence check</b>
    /// (<see cref="DeclaredBundlesOf"/>). That is deliberate in both directions: inclusive, because
    /// a torn publication that once listed a package should keep that package in the set the gate
    /// asks about (which can only HOLD, never exempt); and cheap, because this reads EVERY identity
    /// on a network share against a 60 s verdict budget, and a denominator expensive enough to time
    /// out would answer <see cref="PackageAvailabilityKind.Indeterminate"/> and freeze every
    /// environment. The full presence check stays exactly where it decides the verdict —
    /// <see cref="ArtifactsForIdentity"/>, on the target identity.</para>
    /// </summary>
    /// <summary>
    /// 🚨 <b>Every platform release this root has a marker for — the roll SELECTOR's candidate
    /// universe (#3479).</b> Unordered; the caller applies its own SemVer ordering and policy
    /// filters (<c>VersionSelect</c>), because this assembly holds no opinion about which release
    /// is newer.
    ///
    /// <para><b>Why the markers are the right universe.</b> A release's framework identity is a
    /// property of its BINARIES and cannot be computed from a tag, so <c>_releases/&lt;version&gt;</c>
    /// is the only way anything outside the image learns it. A version with no marker therefore has
    /// no resolvable identity, every package answers
    /// <see cref="PackageAvailabilityKind.Indeterminate"/> for it, and it could never be SELECTED —
    /// so enumerating the markers loses no candidate the selector could have chosen, and it lets an
    /// environment answer "which release should I be on" without listing a container registry it
    /// may not be able to reach.</para>
    ///
    /// <para>Fail-safe and NAMED, like every other read here: an absent or unreadable root returns
    /// a <see cref="PublishedReleaseCatalogue.Refusal"/> rather than an empty list, because "no
    /// releases published" and "I could not look" decide opposite things.</para>
    /// </summary>
    public static PublishedReleaseCatalogue PublishedReleases(
        string? publishedRoot, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(publishedRoot))
            return PublishedReleaseCatalogue.Unreadable(
                $"no published bundle root is configured ({ShippedPrebuiltBundles.PublishedRootConfigKey})");

        var markers = Path.Combine(publishedRoot, ReleaseMarkerDirectoryName);
        if (!Directory.Exists(markers))
            return PublishedReleaseCatalogue.Unreadable(
                $"the published bundle root '{publishedRoot}' holds no '{ReleaseMarkerDirectoryName}' "
                + "directory, so no release's framework identity is knowable here — cannot determine "
                + "which release ships all plugins, which is not clearance to roll to the newest one");

        try
        {
            return new PublishedReleaseCatalogue(
                [.. Directory.EnumerateFiles(markers)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .OrderBy(name => name, StringComparer.Ordinal)],
                null);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "ReleaseAvailability: could not list the published releases under {Root}/{Markers}",
                publishedRoot, ReleaseMarkerDirectoryName);
            return PublishedReleaseCatalogue.Unreadable(
                $"the release markers under '{markers}' could not be listed ({ex.Message})");
        }
    }

    /// <summary>Reactive form of <see cref="PublishedReleases(string?, ILogger?)"/> — the
    /// file-system leaf runs on the caller's I/O pool, never on a hub action block.</summary>
    public static IObservable<PublishedReleaseCatalogue> ObserveReleases(
        IIoPool pool, string? publishedRoot, ILogger? logger = null) =>
        pool.InvokeBlocking(_ => PublishedReleases(publishedRoot, logger));

    public static SealedBundleFloor EverSealedBundles(string? publishedRoot, ILogger? logger = null)
    {
        // 🚨 "I could not look" is NOT "there is nothing here", and the difference decides the
        // verdict: an unreadable root must HOLD (Indeterminate), while a readable root that holds
        // no publication is the one stated applicability exemption. A configured root that does not
        // EXIST is the mis-mount / mistyped-path case — the deployment declares it consumes CI
        // bakes and its storage is not there — so it is a refusal, never an exemption. Collapsing
        // the two is the same vacuity this whole method exists to remove.
        if (string.IsNullOrWhiteSpace(publishedRoot))
            return SealedBundleFloor.Unreadable(
                $"no published bundle root is configured ({ShippedPrebuiltBundles.PublishedRootConfigKey})");
        if (!Directory.Exists(publishedRoot))
            return SealedBundleFloor.Unreadable(
                $"the configured published bundle root '{publishedRoot}' does not exist — this "
                + "deployment declares that it consumes CI bakes, so an absent root is an "
                + "unreadable one (a volume that did not mount, or a mistyped path), not evidence "
                + "that nothing is published. Cannot determine ≠ clear to proceed.");

        var bundles = new List<string>();
        var identities = 0;
        try
        {
        foreach (var identityDirectory in Directory.EnumerateDirectories(publishedRoot)
                     .OrderBy(d => d, StringComparer.Ordinal))
        {
            // The release-marker directory holds version→identity FILES, never a publication.
            // Skipping it by name keeps "how many identities published" honest — it would
            // contribute no bundles either way, but it would not be an identity.
            if (string.Equals(
                    Path.GetFileName(identityDirectory),
                    ReleaseMarkerDirectoryName,
                    StringComparison.Ordinal))
                continue;
            // The sentinel's DECLARATION, not a per-bundle presence check — see DeclaredBundlesOf
            // for why the denominator must be both inclusive and cheap.
            var declaredHere = Directory.EnumerateDirectories(identityDirectory)
                .OrderBy(d => d, StringComparer.Ordinal)
                .Select(DeclaredBundlesOf)
                .Where(listing => listing is not null)
                .SelectMany(listing => listing!)
                .ToList();
            if (declaredHere.Count == 0)
                continue;
            identities++;
            bundles.AddRange(declaredHere);
        }
        }
        catch (Exception ex)
        {
            // An IO fault against the share is an availability incident, and it must be
            // distinguishable from an empty root for exactly the reason above.
            logger?.LogWarning(ex,
                "ReleaseAvailability: could not read the published bundle root {Root} while "
                + "establishing which packages must carry a sealed bake", publishedRoot);
            return SealedBundleFloor.Unreadable(
                $"the published bundle root '{publishedRoot}' could not be read ({ex.Message})");
        }
        return new SealedBundleFloor(ReleaseArtifacts.Of(bundles).SealedBundles, identities);
    }

    /// <summary>
    /// 🚨 The FULL observation of one identity (#3175): the sealed bundle ids, the module set every
    /// complete source sealed (each module bundle read for the MVID of every <c>MeshWeaver.*</c>
    /// assembly it CARRIES, spelt as the dependency records spell it), and every sealed bundle's
    /// per-NodeType dependency record. The bytes were always on disk — <see cref="SealedModulesOf"/>
    /// already refused a torn set — and <see cref="ReleaseAvailability"/> can only assert CONSISTENCY
    /// when it is handed both halves. Reads every bundle's MANIFEST, and for each sealed module bundle
    /// inflates its module-folder assemblies into memory to read the MVID from their PE metadata —
    /// never a content bundle's NodeType assembly bytes.
    /// </summary>
    /// <remarks>
    /// <para>A module bundle that cannot be read, or a source sealed before module sealing existed,
    /// becomes the set's <see cref="SealedModuleSet.Refusal"/> — a named unreadability the gate turns
    /// into a HOLD — and never a silent omission that would read as "no module here".</para>
    ///
    /// <para>🚨 <b>Every COPY is accounted for, not only the declared one (#3221).</b> A module-owned
    /// <c>MeshWeaver.*</c> sibling RIDES the bundles that reference it
    /// (<see href="../ModuleClosureAccounting">Module Closure Accounting</see>), so one assembly name
    /// reaches a mesh from several bundles at once: measured on MeshWeaver.Plugins <c>main</c>
    /// 2026-09-03, 19 of 37 module bundles carry a copy of an assembly some OTHER package declares as
    /// its module — <c>MeshWeaver.Markdown.Collaboration</c> (Essentials') rides 14 of them and
    /// <c>MeshWeaver.AI</c> (AI's) rides 12. Those copies must be the SAME BUILD: the loader collapses
    /// them to whichever loads first, so a divergence makes the live MVID a coin toss and every
    /// NodeType binding the name is declined at adoption. Riding copies therefore take part in
    /// <see cref="SealedModuleSet.Conflicts"/>, while only the DECLARED entry defines
    /// <see cref="SealedModuleSet.MvidByModule"/> — the entry is what an instance registers as an
    /// <c>InstalledModuleAssembly</c> and therefore what <c>NodeTypeCompilationHelpers.ModuleMvidsOf</c>
    /// reports, so a ride-only name must not start standing in for a module nothing declares.</para>
    /// </remarks>
    internal static ReleaseArtifacts ArtifactsForIdentity(string identityDirectory, ILogger? logger)
    {
        var bundles = new List<string>();
        var records = ImmutableArray.CreateBuilder<BundleDependencyRecord>();
        // The DECLARED modules — what an instance registers, and therefore what a record must name.
        var sealedBy = new Dictionary<string, (string Mvid, string Source)>(StringComparer.Ordinal);
        // EVERY copy of every MeshWeaver.* assembly the sealed module bundles carry, declared or
        // riding: the first one seen per name, against which every later copy must compare equal.
        var producedBy = new Dictionary<string, SealedCopy>(StringComparer.Ordinal);
        var conflicts = ImmutableArray.CreateBuilder<string>();
        var refusals = new List<string>();
        // 🚨 #3651 — the identity's PLATFORM SURFACE, from the first sealed source that carries one.
        // Every source under one identity was baked inside the same image, so their documents
        // describe the same platform; the first readable one is the surface. The reasons a source
        // has none are collected so a gate that measured nothing can say why.
        ModulePlatformSurface? surface = null;
        var surfaceNotes = new List<string>();

        foreach (var sourceDirectory in Directory.EnumerateDirectories(identityDirectory)
                     .OrderBy(d => d, StringComparer.Ordinal))
        {
            // 🚨 #3461: `publication` is where the bytes ARE (the pointed-to generation, or the
            // source directory in the flat layout); `sourceDirectory` only names the SOURCE.
            var (publication, complete) = CompletePublicationOf(sourceDirectory, logger);
            if (complete is null)
                continue;
            var source = Path.GetFileName(sourceDirectory)!;
            bundles.AddRange(complete);
            foreach (var bundle in complete)
                records.AddRange(DependencyRecordsOf(Path.Combine(publication, bundle), bundle, logger));
            if (surface is null)
            {
                var (read, note) = PlatformSurfaceOf(publication, source, logger);
                surface = read;
                if (note is not null)
                    surfaceNotes.Add(note);
            }

            var modules = SealedModulesOf(sourceDirectory, logger);
            if (modules.Modules is null)
            {
                refusals.Add($"source '{source}': {modules.Refusal}");
                continue;
            }
            foreach (var moduleBundle in modules.Modules)
            {
                var path = Path.Combine(
                    modules.Directory ?? publication, ModulesDirectoryName, moduleBundle);
                ImmutableArray<SealedModuleAssembly> carried;
                try
                {
                    carried = SealedModuleAssembliesOf(path);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or BadImageFormatException or InvalidOperationException)
                {
                    refusals.Add($"source '{source}': module bundle '{moduleBundle}' is unreadable — {ex.Message}");
                    continue;
                }
                foreach (var (name, mvid, isEntry) in carried)
                {
                    if (producedBy.TryGetValue(name, out var first))
                    {
                        if (!string.Equals(first.Mvid, mvid, StringComparison.Ordinal))
                            conflicts.Add(
                                $"module {name}: source '{first.Source}' sealed {first.Mvid} "
                                + $"{RoleOf(first.IsEntry)} '{first.Bundle}', source '{source}' sealed "
                                + $"{mvid} {RoleOf(isEntry)} '{moduleBundle}'");
                    }
                    else
                    {
                        producedBy[name] = new SealedCopy(mvid, source, moduleBundle, isEntry);
                    }
                    if (isEntry)
                        sealedBy.TryAdd(name, (mvid, source));
                }
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
            PlatformSurface = surface,
            PlatformSurfaceDetail = surface is not null
                ? null
                : surfaceNotes.Count == 0
                    ? $"no sealed source under framework identity '{Path.GetFileName(identityDirectory)}' "
                      + $"publishes {PlatformSurfaceFileName} (the publication predates #3651), so "
                      + "the target's type surface is unknown here"
                    : string.Join("; ", surfaceNotes),
        };
    }

    /// <summary>
    /// One sealed source's <see cref="PlatformSurfaceFileName"/>, parsed — or the reason it yields
    /// none. Absent is the ordinary state of a publication sealed before #3651 and is NOT logged as
    /// a problem; a document that is there and does not parse IS, because a producer wrote
    /// something the reader cannot use.
    /// </summary>
    private static (ModulePlatformSurface? Surface, string? Note) PlatformSurfaceOf(
        string publication, string source, ILogger? logger)
    {
        var path = Path.Combine(publication, PlatformSurfaceFileName);
        if (!File.Exists(path))
            return (null, $"source '{source}' publishes no {PlatformSurfaceFileName} (sealed before #3651)");
        try
        {
            return (ModulePlatformSurface.FromJson(File.ReadAllText(path)), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogWarning(ex,
                "ReleaseAvailability: {Path} is present but unusable — the target's type surface "
                + "cannot be read from it, so no landed module can be linked against this release",
                path);
            return (null, $"source '{source}': {PlatformSurfaceFileName} could not be read ({ex.Message})");
        }
    }

    /// <summary>
    /// Every <c>MeshWeaver.*</c> assembly a sealed module bundle CARRIES — its declared entry first,
    /// then each riding sibling — with each MVID in the dependency-record spelling
    /// (<c>mvid:&lt;N&gt;</c> — <c>NodeTypeCompilationHelpers.ModuleMvidsOf</c>'s projection, so the
    /// gate compares the id the seeder will compute). The entry is the manifest's
    /// <c>module.assemblyName</c>, else the single assembly under the module folder — the same
    /// resolution the lanes apply when they compose the bundle.
    ///
    /// <para>🚨 <b>What is read, and why that set (#3221).</b> The module folder is FLAT and holds
    /// the module's private closure, so it carries the entry plus whatever rode with it. Only
    /// <c>MeshWeaver.*</c> names are folded in: those bind by a strictly synchronised
    /// <c>AssemblyVersion</c>, so two copies under one simple name are one assembly identity and the
    /// loader keeps whichever it saw first — that is the double-production hazard. A third-party
    /// diamond RIDES by design, versions independently, and is deliberately NOT judged here. The
    /// files are read from the ARCHIVE rather than from <c>module.assemblies</c>, because what lands
    /// is what the bundle contains, not what its manifest claims.</para>
    /// </summary>
    internal static ImmutableArray<SealedModuleAssembly> SealedModuleAssembliesOf(string moduleBundlePath)
    {
        var manifest = BundleReader.ReadManifest(moduleBundlePath);
        using var file = File.OpenRead(moduleBundlePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var flat = archive.Entries
            .Where(e => e.FullName.StartsWith(NuGetPackageWriter.ModuleFolder + "/", StringComparison.Ordinal)
                        && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        && !e.FullName[(NuGetPackageWriter.ModuleFolder.Length + 1)..].Contains('/'))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();
        var name = manifest?.Module?.AssemblyName;
        if (string.IsNullOrWhiteSpace(name))
        {
            if (flat.Count != 1)
                throw new InvalidOperationException(
                    $"cannot identify the module's entry assembly (no manifest module.assemblyName; "
                    + $"{NuGetPackageWriter.ModuleFolder}/ holds {flat.Count} dll(s))");
            name = Path.GetFileNameWithoutExtension(flat[0].FullName);
        }
        var entry = archive.GetEntry($"{NuGetPackageWriter.ModuleFolder}/{name}.dll")
            ?? throw new InvalidOperationException(
                $"{NuGetPackageWriter.ModuleFolder}/{name}.dll is missing from the bundle");

        var carried = ImmutableArray.CreateBuilder<SealedModuleAssembly>(flat.Count);
        carried.Add(new SealedModuleAssembly(name, MvidOf(entry), IsEntry: true));
        foreach (var sibling in flat)
        {
            var simple = Path.GetFileNameWithoutExtension(sibling.FullName);
            if (string.Equals(simple, name, StringComparison.Ordinal) || !IsPlatformAssemblyName(simple))
                continue;
            carried.Add(new SealedModuleAssembly(simple, MvidOf(sibling), IsEntry: false));
        }
        return carried.ToImmutable();
    }

    /// <summary>The MVID of one archived assembly, in the dependency-record spelling. Inflated into
    /// memory (a zip entry is not seekable, and <c>PEReader</c> needs to seek).</summary>
    private static string MvidOf(ZipArchiveEntry entry)
    {
        if (entry.Length is < 0 or > int.MaxValue)
            throw new InvalidOperationException(
                $"{entry.FullName} declares an implausible length ({entry.Length} bytes) — the module bundle is corrupt");
        using var stream = entry.Open();
        using var bytes = new MemoryStream((int)entry.Length);
        stream.CopyTo(bytes);
        bytes.Position = 0;
        using var pe = new PEReader(bytes);
        var metadata = pe.GetMetadataReader();
        return CompiledDependencies.MvidScheme
               + metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("N");
    }

    /// <summary>The names that bind by a strictly synchronised <c>AssemblyVersion</c> and therefore
    /// collapse to ONE identity in a loaded process — the same predicate
    /// <c>MeshWeaver.Plugin.Build.DepsClosure</c> uses to decide what the platform side owns.</summary>
    private static bool IsPlatformAssemblyName(string name) =>
        name.StartsWith("MeshWeaver.", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "MeshWeaver", StringComparison.OrdinalIgnoreCase);

    /// <summary>Which bundle, from which source, sealed the first copy of one assembly name, and
    /// whether it was that bundle's DECLARED module or a sibling riding beside it.</summary>
    private readonly record struct SealedCopy(string Mvid, string Source, string Bundle, bool IsEntry);

    /// <summary>How a copy reached the sealed set — the half of a conflict line that tells an
    /// operator which producer to change.</summary>
    private static string RoleOf(bool isEntry) =>
        isEntry ? "as the declared module of" : "as a sibling riding";

    /// <summary>
    /// The per-NodeType dependency records one sealed bundle carries. EMPTY when the archive carries
    /// no manifest entry or its manifest lists no records (a legacy bundle — nothing to judge, not an
    /// error); EMPTY and LOGGED when the bundle is not a readable archive or its manifest does not
    /// parse.
    ///
    /// <para>🚨 Per-bundle, never per-identity. The first cut let a bundle that was not a zip throw
    /// out of <see cref="Read"/>'s outer catch, so ONE unreadable file made the whole identity
    /// <see cref="ReleaseArtifacts.Unreadable"/> AND lost the resolved framework identity — which is
    /// how core #3187 reddened MeshWeaver.Plugins main within the hour: its catalogue tests publish
    /// bundles as literal bytes and pin the contract this observation had before records existed
    /// (presence counts, the identity resolves, names match case-insensitively). Those rules still
    /// hold. A bundle without readable records simply has nothing for the consistency check to
    /// judge, exactly as a legacy bundle recorded before #1707 slice 2 has none.</para>
    /// </summary>
    private static IReadOnlyList<BundleDependencyRecord> DependencyRecordsOf(
        string bundlePath, string bundleFileName, ILogger? logger)
    {
        var id = bundleFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? bundleFileName[..^4]
            : bundleFileName;
        BundleReader.Manifest? manifest;
        try
        {
            manifest = BundleReader.ReadManifest(bundlePath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or System.Text.Json.JsonException)
        {
            logger?.LogWarning(ex,
                "ReleaseAvailability: sealed bundle {Bundle} carries no readable manifest — it counts "
                + "for presence, and its dependency records cannot be checked for consistency",
                bundlePath);
            return [];
        }
        var records = new List<BundleDependencyRecord>();
        foreach (var assembly in manifest?.Assemblies ?? [])
        {
            if (assembly.Dependencies is null || string.IsNullOrEmpty(assembly.NodePath))
                continue;
            records.Add(new BundleDependencyRecord(
                id, assembly.NodePath,
                assembly.Dependencies.ToImmutableDictionary(StringComparer.Ordinal)));
        }
        return records;
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

    /// <summary>
    /// The same reading as <see cref="SealedBundlesOf"/>, but it also says WHY a publication is
    /// unreadable and WHICH publication a readable one is (#3401).
    ///
    /// <para>🚨 Torn and "no such bundle" are different facts and a consumer must be able to tell
    /// them apart. The publisher UNSEALS before it republishes — <c>publish-bake-bundles.sh</c>
    /// deletes the sentinel first, uploads, and re-seals LAST, deliberately, so nobody can read a
    /// mix of old and new bundles. A reader whose request lands inside that window is looking at a
    /// publication that is being replaced right now, which is transient and self-healing; a reader
    /// asking for a name the seal does not list is making a permanent mistake. Collapsing both to
    /// 404 is what made MeshWeaver.Manufacturing's 2026-09-06 red read as "the bundle is gone"
    /// when the bundle was served intact 20 minutes later.</para>
    ///
    /// <para><b>The generation</b> identifies one publication INSTANCE. A consumer that reads the
    /// index and then fetches N bundles is doing N+1 reads of a directory that can be resealed
    /// underneath it; carrying the generation lets the server refuse a fetch that would silently
    /// mix bytes from two publications. It folds the seal's listing together with the moment the
    /// seal was written, so any reseal changes it even when the bundle NAMES are identical.</para>
    /// </summary>
    public static SealedPublicationReading SealedPublicationOf(
        string sourceDirectory, ILogger? logger = null)
    {
        // 🚨 #3461: resolve the publication pointer ONCE, here, and hand the resolved directory
        // back on the reading. Every path this publication's bytes live at is composed under
        // `Directory` — a caller that resolves and then composes against `sourceDirectory` would
        // read the flat layout while reporting the generation's, which is a wrong answer rather
        // than a missing one, and both paths exist during the migration.
        var directory = ShippedPrebuiltBundles.PublicationDirectoryOf(sourceDirectory, logger);
        var sentinel = Path.Combine(
            directory, ShippedPrebuiltBundles.CompletionSentinelFileName);
        if (!File.Exists(sentinel))
            return new SealedPublicationReading(
                null, null,
                "the publication is being republished right now (no completion sentinel) — the "
                + "publisher removes it before uploading and restores it last, so this is a "
                + "transient window, not a missing publication") { Directory = directory };

        var listed = File.ReadAllLines(sentinel)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (listed.FirstOrDefault(name => !File.Exists(Path.Combine(directory, name)))
            is { } missing)
            return new SealedPublicationReading(
                null, null,
                $"the publication is torn — its seal lists '{missing}', which is not on disk")
            { Directory = directory };

        return new SealedPublicationReading(listed, GenerationOf(sentinel, listed), null)
        {
            Directory = directory,
        };
    }

    /// <summary>
    /// A short, stable token for one publication INSTANCE: the seal's listing plus the instant the
    /// seal was written. The timestamp is what makes a reseal to an identical bundle list a
    /// DIFFERENT generation — which it is, because the bytes behind those names may have changed.
    /// </summary>
    private static string GenerationOf(string sentinel, IReadOnlyList<string> listed)
    {
        var material =
            $"{File.GetLastWriteTimeUtc(sentinel).Ticks}\n{string.Join("\n", listed)}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    private static IReadOnlyList<string>? CompleteBundlesOf(string sourceDirectory, ILogger? logger)
        => CompletePublicationOf(sourceDirectory, logger).Bundles;

    /// <summary>
    /// The same reading as <see cref="CompleteBundlesOf"/>, plus the directory the bytes are
    /// actually in — the generation this source's pointer names, or the source directory itself
    /// (#3461). Every caller that composes a path under a publication takes it from here rather
    /// than resolving a second time; see
    /// <c>ShippedPrebuiltBundles.PublicationDirectoryOf</c>.
    /// </summary>
    private static (string Directory, IReadOnlyList<string>? Bundles) CompletePublicationOf(
        string sourceDirectory, ILogger? logger)
    {
        var directory = ShippedPrebuiltBundles.PublicationDirectoryOf(sourceDirectory, logger);
        var sentinel = Path.Combine(
            directory, ShippedPrebuiltBundles.CompletionSentinelFileName);
        if (!File.Exists(sentinel))
        {
            logger?.LogInformation(
                "ReleaseAvailability: {SourceDirectory} carries no {Sentinel} — the publication "
                + "is torn, so its bundles do not count as available",
                directory, ShippedPrebuiltBundles.CompletionSentinelFileName);
            return (directory, null);
        }

        var listed = File.ReadAllLines(sentinel)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (listed.Any(name => !File.Exists(Path.Combine(directory, name))))
        {
            logger?.LogInformation(
                "ReleaseAvailability: {SourceDirectory} is sealed but a listed bundle is "
                + "absent — the publication is torn, so its bundles do not count as available",
                directory);
            return (directory, null);
        }

        return (directory, listed);
    }

    private static bool IsComplete(string sourceDirectory, ILogger? logger) =>
        CompleteBundlesOf(sourceDirectory, logger) is not null;

    /// <summary>
    /// What a source directory's sentinel DECLARES was shipped, without verifying that each listed
    /// bundle is still on disk. Null when there is no sentinel at all.
    ///
    /// <para>🚨 <b>Deliberately weaker than <see cref="CompleteBundlesOf"/>, and only ever used for
    /// the DENOMINATOR (#3441).</b> Two reasons, and both point the same way:</para>
    ///
    /// <para><b>Semantics.</b> "Has this package ever shipped a bake?" is answered by the
    /// publisher's own declaration. Whether every byte is still present is a question about what
    /// can be ADOPTED NOW — the numerator — which <see cref="ArtifactsForIdentity"/> answers with
    /// the full check, for the target identity, where it decides the verdict. Being INCLUSIVE here
    /// is the safe direction: a publication that once listed a package keeps that package in the
    /// set of things the gate asks about, which can only HOLD a roll, never exempt one.</para>
    ///
    /// <para><b>Cost, which is a correctness concern here.</b> The denominator reads EVERY identity
    /// the root holds, and the published root is a network share (Azure Files over SMB on AKS).
    /// Verifying presence would cost one stat per bundle per source per identity — on the order of
    /// thousands of round trips per poll tick once identities accumulate — against a gate whose
    /// whole verdict is bounded at 60 s. Blowing that bound answers
    /// <see cref="PackageAvailabilityKind.Indeterminate"/>, which HOLDS: a denominator expensive
    /// enough to time out would freeze every environment, turning this gate into the outage it
    /// exists to prevent. Reading the sentinel alone is one file read per source.</para>
    /// </summary>
    private static IReadOnlyList<string>? DeclaredBundlesOf(string sourceDirectory)
    {
        var sentinel = Path.Combine(
            ShippedPrebuiltBundles.PublicationDirectoryOf(sourceDirectory),
            ShippedPrebuiltBundles.CompletionSentinelFileName);
        if (!File.Exists(sentinel))
            return null;
        return File.ReadAllLines(sentinel)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

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
        // 🚨 #3461: one resolution, handed back on the reading. `publication` is the directory the
        // bytes are in; the module set is `<publication>/modules`, never `<source>/modules`.
        var (publication, complete) = CompletePublicationOf(sourceDirectory, logger);
        if (complete is null)
            return new(null, "no sealed publication") { Directory = publication };
        var directory = Path.Combine(publication, ModulesDirectoryName);
        var index = Path.Combine(directory, ModulesIndexFileName);
        if (!File.Exists(index))
        {
            logger?.LogInformation(
                "ReleaseAvailability: {SourceDirectory} is sealed but carries no {Modules}/{Index} — "
                + "it predates module sealing, so a consumer cannot compose from it until it is republished",
                publication, ModulesDirectoryName, ModulesIndexFileName);
            return new(null,
                $"the publication is sealed but carries no module set ({ModulesDirectoryName}/{ModulesIndexFileName}) — "
                + "it predates module sealing; republish the source under a platform that seals module sets")
            { Directory = publication };
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
                publication, bad);
            return new(null, $"the module set is torn: '{bad}' is listed in {ModulesDirectoryName}/{ModulesIndexFileName} but absent (or not a bare name)")
            { Directory = publication };
        }
        return new(listed, null) { Directory = publication };
    }
}

/// <summary>One reading of a sealed publication's module set: the listed bundle names, or
/// <c>null</c> with the reason a consumer must not compose from it.</summary>
public sealed record ModuleSetReading(IReadOnlyList<string>? Modules, string? Refusal)
{
    /// <summary>
    /// 🚨 The directory the module bundles are actually IN (#3461) — the generation this source's
    /// <c>_current</c> pointer names, or the source directory itself in the flat layout. Compose
    /// <c>&lt;Directory&gt;/modules/&lt;bundle&gt;</c>, NEVER
    /// <c>&lt;sourceDirectory&gt;/modules/&lt;bundle&gt;</c>: during the migration both exist, so
    /// composing against the source directory reads the wrong publication's bytes and says
    /// nothing. Null only on a reading nobody produced through
    /// <c>PublishedBundleCatalogue.SealedModulesOf</c> (a hand-built one in a test).
    /// </summary>
    public string? Directory { get; init; }
}

/// <summary>
/// One reading of a source's sealed publication (#3401): the bundle names and the generation that
/// identifies this publication instance, or <c>null</c> bundles with the reason it cannot be read.
/// <paramref name="TornReason"/> is non-null exactly when <paramref name="Bundles"/> is null.
/// </summary>
public sealed record SealedPublicationReading(
    IReadOnlyList<string>? Bundles, string? Generation, string? TornReason)
{
    /// <summary>
    /// 🚨 The directory the sealed bytes are actually IN (#3461) — the generation this source's
    /// <c>_current</c> pointer names, or the source directory itself in the flat layout. Compose
    /// every bundle path under THIS, never under the source directory handed in: during the
    /// migration both exist, so composing against the source directory serves the wrong
    /// publication's bytes under this reading's generation — which is the mix the generation
    /// exists to prevent. Null only on a reading nobody produced through
    /// <c>PublishedBundleCatalogue.SealedPublicationOf</c> (a hand-built one in a test).
    /// </summary>
    public string? Directory { get; init; }
}

/// <summary>
/// One <c>MeshWeaver.*</c> assembly a sealed module bundle carries, and the build it is
/// (<c>mvid:&lt;N&gt;</c>). <paramref name="IsEntry"/> separates the bundle's DECLARED module — the
/// one an instance registers, and therefore the one a dependency record must name — from a sibling
/// RIDING beside it because it is module-owned and exists nowhere in <c>/app</c>
/// (<see href="../ModuleClosureAccounting">Module Closure Accounting</see>). Both must be the same
/// build for one framework identity; only the entry defines what the identity's module IS (#3221).
/// </summary>
/// <param name="Name">The assembly's simple name.</param>
/// <param name="Mvid">Its MVID in the dependency-record spelling (<c>mvid:&lt;32 hex&gt;</c>).</param>
/// <param name="IsEntry">True for the bundle's declared module, false for a riding sibling.</param>
public readonly record struct SealedModuleAssembly(string Name, string Mvid, bool IsEntry);

/// <summary>One reading of the catalogue: what the target release turned out to be, and what was
/// published for it.</summary>
/// <param name="Target">The release, with its framework identity resolved (or not).</param>
/// <param name="Artifacts">What is sealed for it.</param>
public sealed record ReleaseObservation(ReleaseTarget Target, ReleaseArtifacts Artifacts);

/// <summary>
/// Every platform release a published root carries a marker for — the roll selector's candidate
/// universe (#3479).
/// </summary>
/// <param name="Versions">The release versions, ordinal-sorted for determinism. 🚨 That is NOT
/// SemVer order: the caller orders them, because it owns the version rules and the update policy.
/// </param>
/// <param name="Refusal">Why the listing could not be made, or null when it was. Non-null is a
/// HOLD — an absent or unreadable marker directory on a deployment that declares it consumes CI
/// bakes is a mis-mount, not evidence that nothing is published.</param>
public sealed record PublishedReleaseCatalogue(
    ImmutableArray<string> Versions, string? Refusal)
{
    /// <summary>A listing that could not be made — the fail-safe constructor.</summary>
    public static PublishedReleaseCatalogue Unreadable(string reason) => new([], reason);
}

/// <summary>
/// 🚨 The deployment gate's DENOMINATOR (#3441) — what a published root has ever demonstrably been
/// able to serve, read across every framework identity it holds rather than from the one identity
/// the gate is about to judge. See
/// <see cref="PublishedBundleCatalogue.EverSealedBundles(string?, Microsoft.Extensions.Logging.ILogger?)"/>
/// for why the denominator may not be taken from the artifact under judgement.
/// </summary>
/// <param name="Bundles">Every bundle id sealed under ANY identity in the root, extension
/// stripped and case-insensitive — the same spelling <see cref="ReleaseArtifacts.SealedBundles"/>
/// uses, so the two sets compare directly.</param>
/// <param name="Identities">How many framework-identity directories carried at least one SEALED
/// source. 🚨 ZERO is the load-bearing value: it means this root serves no bakes at all, so
/// "package X has never been sealed" carries NO information about X and must not be read as an
/// exemption. A caller that cannot tell that case apart is back to a denominator that can be
/// vacuously empty.</param>
/// <param name="Refusal">Why the root could not be READ, or null when it was. 🚨 Non-null is a HOLD
/// and never an exemption: an absent or unreadable root on a deployment that declares it consumes
/// CI bakes is a mis-mount or an IO incident, not evidence that nothing is published. Collapsing
/// "could not look" into "nothing here" is the vacuity this type exists to remove.</param>
public sealed record SealedBundleFloor(
    ImmutableHashSet<string> Bundles, int Identities, string? Refusal = null)
{
    /// <summary>A root that was READ and holds no sealed publication under any identity.</summary>
    public static SealedBundleFloor Empty { get; } =
        new(ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase), 0);

    /// <summary>An observation that could not be made — the fail-safe constructor.</summary>
    public static SealedBundleFloor Unreadable(string reason) =>
        new(ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase), 0, reason);

    /// <summary>Whether this root serves CI bakes at all — the precondition for reading an absent
    /// bundle as "this package ships no content" rather than as "we have observed nothing".
    /// False for an unreadable observation too, which is why callers must test
    /// <see cref="Refusal"/> FIRST: the two share this answer and mean opposite things.</summary>
    public bool ServesBakes => Refusal is null && Identities > 0;
}
