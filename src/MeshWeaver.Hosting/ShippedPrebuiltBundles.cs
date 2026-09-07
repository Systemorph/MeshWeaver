using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Adopts the prebuilt-assembly bundles the IMAGE ITSELF ships (issue #1660 WS1): at boot, before
/// the dynamic-NodeType sweep decides what to build, every <c>*.zip</c> under the image's
/// <c>prebuilt/</c> directory is read with <c>BundleReader</c> and seeded through
/// <see cref="PrebuiltAssemblySeeder"/> — so a NodeType whose bytes were compiled in CI against
/// THIS image's framework is <c>AlreadyBaked</c> to the sweep's store probe instead of a Roslyn
/// compile on the pod's critical path.
///
/// <para><b>The MVID gate holds here exactly as everywhere else.</b> Each bundle's manifest names
/// the framework MVID it was compiled against; <c>PrebuiltAssemblySeeder.DeclineReason</c> declines
/// anything that is not an exact match with the running process, and a declined bundle costs what
/// today costs — a compile. That makes shipping a bundle strictly safe: it can only ever REMOVE
/// compiles, never load ABI-incompatible bytes. (It also means the bundle must be produced from the
/// same compilation that produced the image's own Graph.dll — a bundle baked by a DIFFERENT build,
/// e.g. another CI run of the same tree with a different version stamp, declines wholesale until
/// framework-identity determinism lands: #1660 WS3.)</para>
///
/// <para><b>Best-effort by design, loudly.</b> A missing directory is the normal state of every
/// deployment that ships no bundles. A corrupt bundle, a bundle for content this mesh does not
/// hold, or a seed that cannot complete is LOGGED and skipped — the sweep then compiles that type
/// as it would have anyway. This is a fallback to today's behaviour, not a swallowed verdict:
/// nothing downstream certifies anything based on what happened here (the bake gate probes the
/// STORE, which only ever holds what was actually adopted).</para>
/// </summary>
public static class ShippedPrebuiltBundles
{
    /// <summary>Config key overriding where the shipped bundles live.
    /// Default: <see cref="DefaultDirectory"/>.</summary>
    public const string DirectoryConfigKey = "PreWarm:PrebuiltDirectory";

    /// <summary>
    /// Config key naming the CI-PUBLISHED bundle root (issue #1660 WS3) — a mounted storage
    /// directory (on AKS, a path under the shared <c>/data</c> Azure Files share) that CI fills
    /// with framework-identity-keyed bundle directories:
    /// <c>&lt;root&gt;/&lt;frameworkIdentity&gt;/&lt;source&gt;/&lt;bundle&gt;.zip</c>
    /// (written by <c>.github/scripts/publish-bake-bundles.sh</c> from the
    /// <c>mw-plugin-test --bake-output</c> artifact). At boot the pod seeds ONLY its own
    /// identity's subdirectory — CI builds are commit-deterministic, so the bake published for
    /// commit X is found by exactly the images built from commit X. Unset (the default) the lane
    /// is inert. Distinct from <see cref="DirectoryConfigKey"/>, which names bundles the IMAGE
    /// itself ships.
    /// </summary>
    public const string PublishedRootConfigKey = "PreWarm:PrebuiltBundleRoot";

    /// <summary>
    /// 🚨 The completeness sentinel of a published source directory (must match the
    /// <c>SENTINEL</c> in <c>.github/scripts/publish-bake-bundles.sh</c>): the publisher writes it
    /// strictly LAST, after every bundle uploaded, so its presence is the atomic "this publication
    /// is whole" fact. The reader honours the same contract — a source directory without it (a
    /// publish that died mid-way) is never seeded: adopting a PARTIAL bake would stamp types as
    /// built from a set that is missing members, and the pre-warmer would trust it. Its content
    /// lists the bundle file names, so a listed-but-missing bundle also reads as torn.
    /// </summary>
    public const string CompletionSentinelFileName = "_complete";

    /// <summary>
    /// 🚨 The publication POINTER of a source directory (must match the <c>POINTER</c> in
    /// <c>.github/scripts/publish-bake-bundles.sh</c>): one line naming the SUBDIRECTORY that
    /// holds the publication which currently applies.
    ///
    /// <para><b>Why a pointer at all (#3461).</b> In the flat layout a publisher replaces a
    /// publication IN PLACE — it deletes <see cref="CompletionSentinelFileName"/>, uploads over
    /// the live files, and re-seals. Two publishers interleaved on one prefix therefore both
    /// unseal, both upload, and the last to seal writes a sentinel over a directory holding SOME
    /// OF EACH one's bytes: one seal, one generation, self-consistent to every consumer and wrong.
    /// The byte-level postcondition in the publisher REFUSES to seal that, which turns a silent
    /// mix into a loud refusal — but it is a postcondition, not mutual exclusion, and it leaves
    /// the interval between its last verification read and the seal.</para>
    ///
    /// <para><b>What the pointer changes.</b> Each publication is written into its OWN directory,
    /// named by the publisher's run-unique publication token, so two publishers never write the
    /// same bytes and a mix becomes UNREPRESENTABLE rather than merely detectable. Publishing then
    /// ends with one small write that moves this pointer, and a reader sees either the previous
    /// generation — intact, sealed, still on the shelf — or the new one.</para>
    ///
    /// <para>🚨 <b>Absent means the FLAT layout, and that is the fail-safe direction.</b> A source
    /// directory with no pointer IS its own publication directory, which is exactly today's
    /// behaviour and stays legal for as long as any pinned reader needs it. The migration order,
    /// the retention rule, and what each phase does and does not close are in
    /// <c>Doc/Architecture/SealedPublicationGenerations</c>.</para>
    /// </summary>
    public const string PublicationPointerFileName = "_current";

    /// <summary>
    /// The directory that actually holds <paramref name="sourceDirectory"/>'s publication: the
    /// generation its <see cref="PublicationPointerFileName"/> names, or the source directory
    /// itself when there is no usable pointer.
    ///
    /// <para>🚨 <b>Every read of a published source directory goes through this, and every path
    /// composed under it is composed under the RESULT.</b> A caller that resolves the pointer and
    /// then composes against the source directory reads the flat layout's bytes while believing it
    /// read the pointed-to generation — silently, because during the migration both paths exist.
    /// That is why <c>PublishedBundleCatalogue.SealedPublicationOf</c> and
    /// <c>PublishedBundleCatalogue.SealedModulesOf</c> HAND BACK the directory they read rather
    /// than leaving each caller to resolve it a second time.</para>
    ///
    /// <para>🚨 <b>It never fails; it falls back.</b> A pointer that is absent, empty, unreadable,
    /// not a single path segment, or that names a directory which is not there resolves to the
    /// source directory. While the flat copy still exists that is the previous behaviour exactly;
    /// once it does not, the source directory carries no sentinel and the existing readers already
    /// answer "being republished right now" and back off — the same self-healing shape, never a
    /// mix. A pointer is a NAME, so anything carrying a directory separator, a root, or a
    /// <c>.</c>/<c>..</c> segment is refused outright: a publication pointer must never be able to
    /// address bytes outside its own source directory.</para>
    /// </summary>
    /// <param name="sourceDirectory">A <c>&lt;root&gt;/&lt;identity&gt;/&lt;source&gt;</c> directory.</param>
    /// <param name="logger">Diagnostics. A REFUSED pointer is a warning — it means a publisher
    /// wrote something this reader will not follow, which is worth seeing.</param>
    public static string PublicationDirectoryOf(string sourceDirectory, ILogger? logger = null)
    {
        var pointer = Path.Combine(sourceDirectory, PublicationPointerFileName);
        string? named;
        try
        {
            if (!File.Exists(pointer))
                return sourceDirectory;
            named = File.ReadAllLines(pointer)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A pointer being replaced right now reads short, or not at all. That is the ONE
            // window this layout has, it is a single small write rather than a whole publication,
            // and it degrades to the generation that applied a moment ago — never to a mix.
            logger?.LogInformation(ex,
                "ShippedPrebuiltBundles: {Pointer} could not be read — reading {SourceDirectory} "
                + "as its own publication directory; a pointer being replaced reads this way, and "
                + "the next read resolves it", pointer, sourceDirectory);
            return sourceDirectory;
        }

        if (string.IsNullOrEmpty(named))
            return sourceDirectory;

        if (named is "." or ".."
            || named != Path.GetFileName(named)
            || Path.IsPathRooted(named)
            || named.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            logger?.LogWarning(
                "ShippedPrebuiltBundles: {Pointer} names '{Named}', which is not a single "
                + "directory name — a publication pointer may only address a subdirectory of its "
                + "own source directory. Reading {SourceDirectory} as its own publication "
                + "directory instead", pointer, named, sourceDirectory);
            return sourceDirectory;
        }

        var generation = Path.Combine(sourceDirectory, named);
        if (!Directory.Exists(generation))
        {
            logger?.LogWarning(
                "ShippedPrebuiltBundles: {Pointer} names generation '{Named}', which is not on "
                + "disk — reading {SourceDirectory} as its own publication directory instead. A "
                + "generation the pointer names must outlive the pointer",
                pointer, named, sourceDirectory);
            return sourceDirectory;
        }
        return generation;
    }

    /// <summary>The conventional location — <c>prebuilt/</c> beside the app binaries, which is
    /// where the publish lays <c>$(PrebuiltBakeDir)</c> bundles into the image.</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "prebuilt");

    /// <summary>Budget for the one NodeType enumeration query (mirrors the sweep's own).</summary>
    private static readonly TimeSpan EnumerationBudget = TimeSpan.FromSeconds(30);

    /// <summary>Per-assembly seed budget. The node demonstrably exists (it came out of the
    /// enumeration), so the seed's stream read is a replay plus one owned write — a seed that
    /// cannot settle inside this is a wedged owner hub, and holding the whole boot on it would
    /// turn an optimisation into an outage. On elapse the type simply compiles in the sweep.</summary>
    private static readonly TimeSpan SeedBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Seeds every shipped bundle and emits the number of assemblies ADOPTED (0 when the directory
    /// is absent or empty — the normal state of a deployment without bundles). Cold; never faults:
    /// every per-bundle and per-seed failure folds into "not adopted" with a warning, because the
    /// sweep behind this compiles whatever was not adopted.
    /// </summary>
    /// <param name="mesh">The mesh hub (supplies the workspace, the I/O pool and the services).</param>
    /// <param name="directory">Bundle directory override; null uses <see cref="DefaultDirectory"/>.</param>
    /// <param name="logger">Diagnostics.</param>
    public static IObservable<int> SeedAll(IMessageHub mesh, string? directory, ILogger? logger)
        => Observable.Defer(() =>
        {
            var dir = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory;
            return SeedBundles(mesh, dir,
                () => Directory
                    .EnumerateFiles(dir, "*.zip", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToList(),
                logger)
                .Select(tally => tally.Covered);
        });

    /// <summary>
    /// Seeds the CI-published bundles for THIS process's framework identity from the configured
    /// published root (<see cref="PublishedRootConfigKey"/>): the pod resolves its own
    /// <c>FrameworkVersion</c> and seeds
    /// <c>&lt;root&gt;/&lt;identity&gt;/&lt;source&gt;/*.zip</c> — the layout CI's publish step
    /// writes, one <c>&lt;source&gt;</c> subdirectory per producing repo — honouring the
    /// completeness contract: only source directories sealed by
    /// <see cref="CompletionSentinelFileName"/> are read, and only the bundles the sentinel
    /// LISTS. Emits the number of assemblies adopted; inert (0, debug-logged) when the root is
    /// not configured, and loud-but-degrading like <see cref="SeedAll"/> everywhere else — a
    /// missing, torn, or partial publication only ever costs what today costs, a compile.
    /// </summary>
    /// <param name="mesh">The mesh hub (supplies the workspace, the I/O pool and the services).</param>
    /// <param name="publishedRoot">The published bundle root, or null/blank when the deployment
    /// does not consume CI bakes.</param>
    /// <param name="logger">Diagnostics.</param>
    public static IObservable<int> SeedPublishedRoot(IMessageHub mesh, string? publishedRoot, ILogger? logger)
        => Observable.Defer(() =>
        {
            if (string.IsNullOrWhiteSpace(publishedRoot))
            {
                logger?.LogDebug(
                    "ShippedPrebuiltBundles: no published bundle root configured ({Key}) — "
                    + "CI-published bakes are not consumed here", PublishedRootConfigKey);
                return Observable.Return(0);
            }
            var identity = PrebuiltAssemblySeeder.LiveFrameworkMvid;
            var dir = Path.Combine(publishedRoot, identity);
            if (!Directory.Exists(dir))
                // Info, not debug: on a deployment that HAS opted in, "CI published nothing for
                // this identity" is the one line that explains why the sweep is compiling.
                logger?.LogInformation(
                    "ShippedPrebuiltBundles: no CI-published bundles for framework identity "
                    + "{Identity} under {Root} — the sweep compiles instead",
                    identity, publishedRoot);
            return SeedBundles(mesh, dir, () => CompletePublishedBundlesOf(dir, logger), logger)
                .Select(tally => tally.Covered);
        });

    /// <summary>
    /// The sentinel-gated bundle set of a published identity directory: for each
    /// <c>&lt;source&gt;</c> subdirectory, exactly the bundles its
    /// <see cref="CompletionSentinelFileName"/> lists — an unsealed directory (publish died
    /// before the sentinel) or a listed-but-missing bundle (torn beyond the seal) skips that
    /// WHOLE source, loudly, and the sweep compiles instead. Runs inside the seeding pool's
    /// blocking leg.
    /// </summary>
    private static List<string> CompletePublishedBundlesOf(string identityDirectory, ILogger? logger)
    {
        var bundles = new List<string>();
        foreach (var source in Directory
                     .EnumerateDirectories(identityDirectory)
                     .OrderBy(d => d, StringComparer.Ordinal))
        {
            // 🚨 #3461: the publication may live in a GENERATION subdirectory this source's
            // pointer names, and every path below is composed under the resolved directory — not
            // under `source`. Resolving and then composing against the source directory would
            // read the flat layout's bytes while reporting the generation's, which during the
            // migration is a silent wrong answer rather than a missing one.
            var sourceDir = PublicationDirectoryOf(source, logger);
            var sentinel = Path.Combine(sourceDir, CompletionSentinelFileName);
            if (!File.Exists(sentinel))
            {
                logger?.LogWarning(
                    "ShippedPrebuiltBundles: {SourceDirectory} carries no {Sentinel} — the "
                    + "publication is incomplete (it died before the seal); NOT seeding it, the "
                    + "sweep compiles instead and the next CI publish re-publishes the source",
                    sourceDir, CompletionSentinelFileName);
                continue;
            }
            var listed = File.ReadAllLines(sentinel)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .OrderBy(l => l, StringComparer.Ordinal)
                .Select(name => Path.Combine(sourceDir, name))
                .ToList();
            var missing = listed.Where(p => !File.Exists(p)).ToList();
            if (missing.Count > 0)
            {
                logger?.LogWarning(
                    "ShippedPrebuiltBundles: {SourceDirectory} is sealed but {Missing} listed "
                    + "bundle(s) are absent (torn publication) — NOT seeding it, the sweep "
                    + "compiles instead", sourceDir, missing.Count);
                continue;
            }
            bundles.AddRange(listed);
        }
        return bundles;
    }

    /// <summary>
    /// Install/push-time consumption (#1707 slice 3): seeds from BOTH bundle sources — the
    /// image's shipped <c>prebuilt/</c> directory and the CI-published identity root — restricted
    /// to the caller's type paths. The only boot coupling the seeding core ever had was its
    /// mesh-wide NodeType enumeration; here the caller (a package install's written set, a git
    /// push's affected set) supplies the paths, so the cache is consumable the moment content
    /// lands instead of only at the next boot. Cold, never faults (degrades to "compile as
    /// today"), and validated per assembly by the seeder's framework + dependency-record gates.
    /// </summary>
    public static IObservable<int> SeedForTypes(
        IMessageHub mesh, IReadOnlyCollection<string> typePaths, ILogger? logger,
        string? imageDirectory = null, string? publishedRoot = null)
        => SeedForTypes(mesh, typePaths, logger, imageDirectory, publishedRoot, onCovered: null);

    /// <summary>
    /// <see cref="SeedForTypes(IMessageHub, IReadOnlyCollection{string}, ILogger, string, string)"/>
    /// with a per-path witness: <paramref name="onCovered"/> is invoked once for every NodeType
    /// path this pass BACKED — adopted now, or already current — from the seeder's own pool
    /// threads, so an implementation must be thread-safe.
    ///
    /// <para>🚨 A separate overload rather than one more optional parameter, and every argument
    /// here is REQUIRED. C# bakes a call's full argument list into the CALL SITE, so widening the
    /// shipped signature with an optional parameter is a BINARY break: an assembly compiled
    /// against the old surface still calls the 5-argument form and would fail with
    /// <c>MissingMethodException</c> at runtime — which in this framework means a prebuilt bundle
    /// adopted from an earlier build, exactly the artifact this method exists to serve. Leaving
    /// the overlapping parameters required is what keeps <c>SeedForTypes(mesh, paths, logger)</c>
    /// from becoming ambiguous between the two.</para>
    ///
    /// <para>The count it emits is unchanged and still authoritative; the witness only says WHICH
    /// paths are behind that count, which is the question a bake shortfall raises
    /// (<c>BakeSeedConsumer.Shortfall</c>).</para>
    /// </summary>
    public static IObservable<int> SeedForTypes(
        IMessageHub mesh, IReadOnlyCollection<string> typePaths, ILogger? logger,
        string? imageDirectory, string? publishedRoot,
        Action<string>? onCovered)
        => Observable.Defer(() =>
        {
            if (typePaths.Count == 0)
                return Observable.Return(0);
            var configuration = mesh.ServiceProvider.GetService<IConfiguration>();
            var imageDir = imageDirectory
                ?? (configuration?[DirectoryConfigKey] is { Length: > 0 } configured
                    ? configured
                    : DefaultDirectory);
            publishedRoot ??= configuration?[PublishedRootConfigKey];
            var paths = typePaths
                .Where(p => !string.IsNullOrEmpty(p))
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

            // 🚨 #3429 — THE PASS WITNESSES ITSELF PER PATH, so a zero can name the types it left
            // behind AND say which of them a bundle had actually offered. Two sets, because they
            // answer different questions: MATCHED is "a bundle entry named this type", COVERED is
            // "the store now backs it". Sets rather than counters on purpose — a per-entry sum
            // double-counts a type named by two bundles (the image's and the CI-published one), and
            // a diagnostic that reports a wrong denominator is the very failure this whole change
            // is about. Per-subscription (they live inside the Defer), never static, and written
            // from the seeder's pool threads — ConcurrentDictionary for the same reason `onCovered`
            // documents thread-safety. The caller's own witness is chained, never replaced.
            var covered = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var matched = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            void WitnessCovered(string path)
            {
                covered.TryAdd(path, 0);
                onCovered?.Invoke(path);
            }
            void WitnessMatched(string path) => matched.TryAdd(path, 0);

            var seeds = new List<IObservable<SeedTally>>
            {
                SeedBundles(mesh, imageDir,
                    () => Directory
                        .EnumerateFiles(imageDir, "*.zip", SearchOption.TopDirectoryOnly)
                        .OrderBy(f => f, StringComparer.Ordinal)
                        .ToList(),
                    logger, paths, WitnessCovered, WitnessMatched),
            };
            var sources = new List<string> { imageDir };
            if (!string.IsNullOrWhiteSpace(publishedRoot))
            {
                var identityDir = Path.Combine(publishedRoot, PrebuiltAssemblySeeder.LiveFrameworkMvid);
                sources.Add(identityDir);
                seeds.Add(SeedBundles(mesh, identityDir,
                    () => CompletePublishedBundlesOf(identityDir, logger), logger, paths,
                    WitnessCovered, WitnessMatched));
            }
            return seeds.Concat()
                .Aggregate(default(SeedTally), (total, one) => total + one)
                // 🚨 #3429 — A SHORTFALL SPEAKS. Every other zero-coverage exit in this file already
                // says why AT the moment it happens; this pass's own zero said nothing at all, so
                // "ten matching assemblies on disk, none adopted" read in a log exactly like "no
                // bundles mounted" and a package installed after boot compiled types whose bytes it
                // was standing on. The line is emitted only when the pass covered LESS than it was
                // asked for, and its level is the honest one: a bundle that NAMED a requested type
                // and still did not back it is a failure (Warning); a deployment with no bundle for
                // these types is a fact about the deployment (Information).
                .Do(tally =>
                {
                    var uncovered = paths.Where(p => !covered.ContainsKey(p))
                        .OrderBy(p => p, StringComparer.Ordinal)
                        .ToArray();
                    var offered = uncovered.Where(matched.ContainsKey).ToArray();
                    if (DescribeShortfall(tally, uncovered, offered, sources, paths.Count)
                        is not { } shortfall)
                        return;
                    if (IsShortfallAFailure(tally, offered.Length))
                        logger?.LogWarning("ShippedPrebuiltBundles: {Shortfall}", shortfall);
                    else
                        logger?.LogInformation("ShippedPrebuiltBundles: {Shortfall}", shortfall);
                })
                .Select(tally => tally.Covered);
        });

    /// <summary>How many uncovered type paths a shortfall line names before it truncates.</summary>
    private const int NamedUncoveredPaths = 8;

    /// <summary>
    /// Whether a shortfall is a FAILURE (bytes were here and did not land) rather than an absence
    /// (no bundle on this deployment covers these types).
    ///
    /// <para>The discriminator is <paramref name="offeredButUncovered"/> — how many UNCOVERED type
    /// paths a bundle entry had actually named. Such a type was declined per entry, faulted, or
    /// timed out, each a real defect on a mesh that shipped the bytes. Counting PATHS, never bundle
    /// entries: a type named by both the image bundle and the CI-published one contributes two
    /// entries and one path, and a comparison of per-entry sums would misread that as a shortfall
    /// (or hide a real one). A whole bundle declined on framework identity, an unreadable bundle
    /// and a hollow one are failures for the same reason (MeshWeaver#3472: a portal adopting bytes
    /// stamped for another identity is worse than adopting none, and both must be distinguishable
    /// from success).</para>
    /// </summary>
    internal static bool IsShortfallAFailure(SeedTally tally, int offeredButUncovered) =>
        offeredButUncovered > 0
        || tally.BundlesDeclined > 0
        || tally.BundlesHollow > 0
        || tally.Faulted > 0;

    /// <summary>
    /// The operator-facing reason a seeding pass covered less than it was asked for, or null when
    /// it covered everything. PURE — no I/O, no logging — so the reasoning is pinned by a test with
    /// no mesh, which is what keeps "a zero must say why" from regressing into a zero that says
    /// something unhelpful.
    /// </summary>
    /// <param name="tally">What the pass observed.</param>
    /// <param name="uncovered">The requested NodeType paths no bundle backed.</param>
    /// <param name="offeredButUncovered">Of those, the ones a bundle entry DID name — the paths
    /// whose bytes were present and did not land, which is the actionable subset.</param>
    /// <param name="sources">The bundle source directories the pass was pointed at.</param>
    /// <param name="requested">How many distinct NodeType paths the pass was asked for. Passed in
    /// rather than derived: <see cref="SeedTally.Covered"/> counts bundle ENTRIES, and a type named
    /// by two sources would inflate a derived denominator (Copilot review, #3483).</param>
    internal static string? DescribeShortfall(
        SeedTally tally, IReadOnlyCollection<string> uncovered,
        IReadOnlyCollection<string> offeredButUncovered, IReadOnlyCollection<string> sources,
        int requested)
    {
        if (uncovered.Count == 0)
            return null;

        var named = string.Join(", ", uncovered.Take(NamedUncoveredPaths));
        if (uncovered.Count > NamedUncoveredPaths)
            named += $", … (+{uncovered.Count - NamedUncoveredPaths} more)";
        var where = sources.Count == 0 ? "(none)" : string.Join(", ", sources);
        var head =
            $"adopted no prebuilt assembly for {uncovered.Count} of {requested} "
            + $"requested NodeType(s) — {named}. ";

        if (tally.Leaving > 0)
            return head
                + "This hub is LEAVING (#3129), so no adoption pass ran on it; the next generation "
                + "seeds its own bundles and these types compile here in the meantime.";

        if (tally.SourcesPresent == 0)
            return head
                + $"No bundle source directory exists on this deployment ({where}) — there was "
                + "nowhere to look, so these types compile in-mesh. Configure "
                + $"{DirectoryConfigKey} (bundles shipped in the image) or {PublishedRootConfigKey} "
                + "(the CI-published, framework-identity-keyed root) if this deployment is meant to "
                + "consume a bake.";

        if (tally.BundlesSeen == 0)
            return head
                + $"{tally.SourcesPresent} bundle source(s) exist ({where}) but hold no bundle for "
                + $"framework identity {PrebuiltAssemblySeeder.LiveFrameworkMvid} — nothing was "
                + "published for this build, so these types compile in-mesh.";

        if (offeredButUncovered.Count == 0)
            return head
                + $"All {tally.BundlesSeen} bundle(s) under {where} name only NodeTypes OUTSIDE "
                + "this set, so none of these types was ever a candidate. Either the bake did not "
                + "cover this package, or the paths it baked differ from the paths this install "
                + "wrote — compare the bundle manifests' node paths with the names above.";

        return head
            + $"A bundle entry under {where} DID name {offeredButUncovered.Count} of them "
            + $"({string.Join(", ", offeredButUncovered.Take(NamedUncoveredPaths))}) across "
            + $"{tally.BundlesSeen} bundle(s), so those bytes were present and did not land"
            + (tally.BundlesDeclined > 0
                ? $"; {tally.BundlesDeclined} bundle(s) were declined WHOLE on framework identity "
                  + $"(live: {PrebuiltAssemblySeeder.LiveFrameworkMvid})"
                : string.Empty)
            + (tally.BundlesHollow > 0 ? $"; {tally.BundlesHollow} carried no assemblies" : string.Empty)
            + (tally.Faulted > 0 ? $"; {tally.Faulted} could not be read" : string.Empty)
            + ". The per-entry decline is logged above with its reason.";
    }

    /// <summary>
    /// What one seeding pass did, split by the only distinction that matters operationally:
    /// assemblies this pass actually ADOPTED (bytes uploaded, record stamped, the type's hub
    /// activated to do it) versus assemblies that were ALREADY CURRENT and therefore cost nothing.
    ///
    /// <para>The split has to be reported, not just acted on. <c>deploy/aks/values.aks.yaml</c>
    /// makes the boot coverage report the replacement for the retired bake readiness gate — "an
    /// identity mismatch declines every bundle wholesale and shows up as a coverage collapse in the
    /// logs on the FIRST pod of a bad roll". A skip-when-current optimisation that reported only
    /// the newly-adopted count would drive that number to zero on every healthy steady-state boot
    /// and destroy the signal. <see cref="Covered"/> is therefore what callers see, and the log
    /// names both halves.</para>
    /// </summary>
    internal readonly record struct SeedTally(int Adopted, int AlreadyCurrent, int FilteredOut = 0)
    {
        /// <summary>Assemblies this bundle has BACKED on the store — adopted now or already there.
        /// This is the coverage number; it is unchanged by the skip optimisation.</summary>
        public int Covered => Adopted + AlreadyCurrent;

        /// <summary>Bundle SOURCES this pass was pointed at (the image's <c>prebuilt/</c>, the
        /// CI-published identity directory) — the denominator of "was there anywhere to look".</summary>
        public int SourcesConsulted { get; init; }

        /// <summary>…of those, the ones that exist on disk.</summary>
        public int SourcesPresent { get; init; }

        /// <summary>Bundle files opened.</summary>
        public int BundlesSeen { get; init; }

        /// <summary>Bundles declined WHOLE on framework identity.</summary>
        public int BundlesDeclined { get; init; }

        /// <summary>Bundles carrying no assemblies (or no readable manifest).</summary>
        public int BundlesHollow { get; init; }

        /// <summary>Bundles that could not be read, plus whole sources that faulted.</summary>
        public int Faulted { get; init; }

        /// <summary>Sources skipped because this hub is LEAVING (#3129).</summary>
        public int Leaving { get; init; }

        public static SeedTally operator +(SeedTally left, SeedTally right) =>
            new(left.Adopted + right.Adopted, left.AlreadyCurrent + right.AlreadyCurrent,
                left.FilteredOut + right.FilteredOut)
            {
                SourcesConsulted = left.SourcesConsulted + right.SourcesConsulted,
                SourcesPresent = left.SourcesPresent + right.SourcesPresent,
                BundlesSeen = left.BundlesSeen + right.BundlesSeen,
                BundlesDeclined = left.BundlesDeclined + right.BundlesDeclined,
                BundlesHollow = left.BundlesHollow + right.BundlesHollow,
                Faulted = left.Faulted + right.Faulted,
                Leaving = left.Leaving + right.Leaving,
            };
    }

    /// <summary>
    /// The NodeType nodes this mesh holds, as one snapshot: the PATHS (which types exist at all)
    /// and, when the snapshot came from the mesh-wide enumeration, the NODES themselves.
    ///
    /// <para>The nodes are what make the deviation check possible — a node carries its
    /// <see cref="MeshNode.Version"/> and its <see cref="NodeTypeDefinition"/>, which together are
    /// the entire record half of "has this bundle entry already been adopted". They come for free:
    /// the boot path already ran this exact query for the paths and threw the rest away.</para>
    ///
    /// <para>An EMPTY <see cref="Nodes"/> map means "no record snapshot available", and the
    /// deviation check then answers "deviates" for everything — the install/push caller
    /// (<see cref="SeedForTypes(IMessageHub, System.Collections.Generic.IReadOnlyCollection{string}, Microsoft.Extensions.Logging.ILogger, string, string)"/>) supplies paths rather than a query, and it is called precisely
    /// when content just changed, so re-adopting is the right answer there anyway.</para>
    /// </summary>
    private sealed record TypeSnapshot(
        ImmutableHashSet<string> Paths,
        ImmutableDictionary<string, MeshNode> Nodes);

    /// <summary>The shared seeding core: the bundle files <paramref name="enumerateBundles"/>
    /// resolves (on the pool's blocking leg), through the one bundle pipeline.
    /// <paramref name="typePathFilter"/> replaces the mesh-wide NodeType enumeration when the
    /// caller already knows which types it is consuming for (#1707 slice 3).
    /// <paramref name="onMatched"/> witnesses every type path a bundle entry NAMED, before anything
    /// can decline it — the half that lets a shortfall say whether the bytes were even here.</summary>
    private static IObservable<SeedTally> SeedBundles(
        IMessageHub mesh, string dir, Func<List<string>> enumerateBundles, ILogger? logger,
        ImmutableHashSet<string>? typePathFilter = null, Action<string>? onCovered = null,
        Action<string>? onMatched = null)
        => Observable.Defer(() =>
        {
            // 🚨 #3129 — NO ADOPTION PASS STARTS ON A HUB THAT IS LEAVING. Every route into
            // seeding converges here (boot, on-demand from the compile watcher, a push's recompile,
            // an install), and every one of them stamps NodeType nodes the whole deployment
            // shares. A pod under SIGTERM is not the pod that will serve those types; on the roll
            // measured in #3129 the terminating pod re-ran this pass on every access for 25
            // minutes, and each run replaced the live build's coordinates with a stale bundle's,
            // which the owner then refused and cleared. Answering 0 — "nothing adopted", the
            // caller's ordinary compile-instead signal — is the same rule #3109 gave
            // BuildupActions. A pass already in flight stops at its next node boundary: each
            // PrebuiltAssemblySeeder.Seed re-asks at subscribe time.
            if (mesh.IsLeaving())
            {
                logger?.LogInformation(
                    "ShippedPrebuiltBundles: this hub is LEAVING (#3129: shutting down, or hosted by a "
                    + "process that has begun stopping) — no adoption pass starts on it; the next "
                    + "generation seeds its own bundles");
                return Observable.Return(default(SeedTally) with { SourcesConsulted = 1, Leaving = 1 });
            }

            if (!Directory.Exists(dir))
            {
                logger?.LogDebug(
                    "ShippedPrebuiltBundles: no bundle directory at {Directory} — nothing to seed", dir);
                return Observable.Return(default(SeedTally) with { SourcesConsulted = 1 });
            }

            var meshService = mesh.ServiceProvider.GetService<IMeshService>();
            if (meshService is null && typePathFilter is null)
            {
                logger?.LogDebug("ShippedPrebuiltBundles: no IMeshService registered — nothing to seed");
                return Observable.Return(default(SeedTally)
                    with { SourcesConsulted = 1, SourcesPresent = 1 });
            }
            var accessService = mesh.ServiceProvider.GetService<AccessService>();
            var pool = mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>().Get("prebuilt:files");
            var store = mesh.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;
            var startedAt = DateTimeOffset.UtcNow;

            // ONE enumeration of the NodeType nodes this mesh actually holds — the filter that
            // keeps a bundle for content this deployment never imported (an image ships one
            // content set; a mesh serves a subset) from parking the boot on per-path waits for
            // nodes that do not exist. A caller-supplied set (install/push) skips the query.
            //
            // 🚨 The NODES are kept, not just their paths. The same snapshot then answers the far
            // more valuable question — "has this entry already been adopted?" — from the record it
            // was already carrying. Keeping it costs nothing; throwing it away cost 43 hub
            // activations and 13.5 s on every memex-cloud boot.
            var snapshot = typePathFilter is not null
                ? Observable.Return(new TypeSnapshot(
                    typePathFilter, ImmutableDictionary<string, MeshNode>.Empty))
                : meshService!
                    .Query<MeshNode>(MeshQueryRequest
                        // Every NodeType definition — a catalog, mesh-wide by nature (#3202).
                        .FromQuery(MeshWideQuery.OfType(MeshNode.NodeTypePath)))
                    .Take(1)
                    .Timeout(EnumerationBudget)
                    .Select(change =>
                    {
                        var nodes = change.Items
                            .Where(n => !string.IsNullOrEmpty(n.Path))
                            .GroupBy(n => n.Path!, StringComparer.OrdinalIgnoreCase)
                            .ToImmutableDictionary(
                                g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                        return new TypeSnapshot(
                            nodes.Keys.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase), nodes);
                    });

            // 🚨 System-scoped end-to-end: reading NodeType records across every partition and
            // stamping adopted builds is framework infrastructure, exactly like the sweep this
            // runs in front of.
            //
            // 🚨 RunAsSystem, never `Observable.Using(AccessContextScope.AsSystem, …)` (#1444/#1790).
            // `AccessContextScope.AsSystem(x)` IS `x.ImpersonateAsSystem()`, so the helper hides the
            // shape rather than changing it: `Using` opens the AsyncLocal on the SUBSCRIBING thread
            // and disposes it wherever the inner observable terminates — here an IoPool thread —
            // leaving the bootstrap subscriber latched as `system-security`. RunAsSystem opens and
            // closes inside one Subscribe; the whole cold pipeline below stays in the work factory,
            // so the IoPool work and every stamp write are still issued as System.
            return accessService.RunAsSystem(
                    () => pool
                        .InvokeBlocking(_ => enumerateBundles())
                        .SelectMany(bundles =>
                        {
                            if (bundles.Count == 0)
                            {
                                logger?.LogDebug(
                                    "ShippedPrebuiltBundles: {Directory} holds no bundles", dir);
                                return Observable.Return(default(SeedTally)
                                    with { SourcesConsulted = 1, SourcesPresent = 1 });
                            }

                            return snapshot
                                .SelectMany(existing => bundles
                                    .Select(bundle => SeedBundle(
                                        mesh, pool, store, bundle, existing, logger, onCovered,
                                        onMatched))
                                    .Concat()
                                    .Aggregate(default(SeedTally), (total, one) => total + one)
                                    .Do(tally =>
                                    {
                                        logger?.LogInformation(
                                            "ShippedPrebuiltBundles: {Covered} prebuilt assembly(ies) from "
                                            + "{Bundles} shipped bundle(s) under {Directory} are backed by "
                                            + "the assembly store — {Adopted} adopted now, {Current} already "
                                            + "current and skipped WITHOUT activating their NodeType hubs — "
                                            + "in {Elapsed}",
                                            tally.Covered, bundles.Count, dir, tally.Adopted,
                                            tally.AlreadyCurrent, DateTimeOffset.UtcNow - startedAt);
                                        // 🚨 A mount that backed NOTHING needs its reason at the SAME level as
                                        // the summary, or the summary is unactionable. "0 of N, 0 declined" has
                                        // exactly one cause left once identity is ruled out: every bundle named
                                        // only NodeTypes absent from this mesh, and the per-bundle line saying so
                                        // is LogDebug — invisible wherever this actually matters (CI and prod both
                                        // run at Information). Diagnosing one 0-of-37 boot on the Education gate
                                        // cost a source read of this file to learn the filter even existed, which
                                        // is precisely what the "Loud, so an operator can see WHY an image that
                                        // ships bundles still compiled" comment below is meant to prevent.
                                        // ONE aggregate line, only in the pathological case — never per bundle.
                                        if (tally.Covered == 0 && tally.FilteredOut > 0)
                                            logger?.LogInformation(
                                                "ShippedPrebuiltBundles: nothing was backed, and {Filtered} of "
                                                + "{Bundles} bundle(s) under {Directory} named only NodeTypes this "
                                                + "mesh does not hold. This mesh's NodeType snapshot carries "
                                                + "{Types} path(s). A snapshot of 0 means the content is not "
                                                + "imported YET, which is normal for a mesh that installs after "
                                                + "boot: adoption then depends entirely on the install-time "
                                                + "re-seed (IPrebuiltAssemblyConsumer.SeedForTypes), not on this "
                                                + "pass. Any bundle NOT counted here exited for a reason already "
                                                + "logged above (identity decline, hollow bundle, fault). The "
                                                + "sweep compiles whatever stays uncovered",
                                                tally.FilteredOut, bundles.Count, dir, existing.Paths.Count);
                                    })
                                    .Select(tally => tally with
                                    {
                                        SourcesConsulted = 1,
                                        SourcesPresent = 1,
                                        BundlesSeen = bundles.Count,
                                    }));
                        }))
                // The seeding is an optimisation in front of the sweep — a fault here must degrade
                // to "compile as today", never hold or fail the boot. Loud, so an operator can see
                // WHY an image that ships bundles still compiled.
                .Catch<SeedTally, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "ShippedPrebuiltBundles: seeding failed — the sweep will compile instead "
                        + "(the shipped bundles under {Directory} were not adopted)", dir);
                    return Observable.Return(default(SeedTally)
                        with { SourcesConsulted = 1, SourcesPresent = 1, Faulted = 1 });
                });
        });

    /// <summary>
    /// One bundle: read its MANIFEST, gate on the framework MVID once, and seed only the entries
    /// that actually deviate from what the mesh already records and the store already holds.
    ///
    /// <para>🚨 <b>Manifest first, payload last.</b> The manifest is a few KB naming every entry's
    /// node path and dependency record; the assemblies are the bundle's entire weight. Reading the
    /// manifest alone is enough to answer the whole adoption question, so a bundle whose types are
    /// all current is answered without decompressing a single assembly — on top of the per-type hub
    /// activation and store upload that <see cref="PrebuiltAssemblySeeder.Seed(MeshWeaver.Messaging.IMessageHub, string, byte[], byte[], string, Microsoft.Extensions.Logging.ILogger, System.Collections.Generic.IReadOnlyDictionary{string, string}, string)"/> would cost.</para>
    ///
    /// <para>Emits the tally; folds its own faults to zero.</para>
    /// </summary>
    private static IObservable<SeedTally> SeedBundle(
        IMessageHub mesh,
        IIoPool pool,
        IAssemblyStore store,
        string bundlePath,
        TypeSnapshot snapshot,
        ILogger? logger,
        Action<string>? onCovered = null,
        Action<string>? onMatched = null)
        => pool
            .InvokeBlocking(_ => Plugin.Packaging.BundleReader.ReadManifest(bundlePath))
            .SelectMany(manifest =>
            {
                if (PrebuiltAssemblySeeder.DeclineReason(manifest?.FrameworkMvid) is { } reason)
                {
                    logger?.LogInformation(
                        "ShippedPrebuiltBundles: bundle {Bundle} DECLINED whole: {Reason} — "
                        + "the sweep compiles instead", Path.GetFileName(bundlePath), reason);
                    return Observable.Return(default(SeedTally) with { BundlesDeclined = 1 });
                }
                if (manifest!.Assemblies is not { Count: > 0 } assemblies)
                {
                    // 🚨 On a require-prebuilt mesh an empty bundle is an ERROR, loudly named: the
                    // types it should have covered will not compile here (that is the flag's whole
                    // point), so "nothing to adopt" is not a degraded-but-fine state — it is the
                    // distribution lane shipping a hollow artifact, and the fix is a rebake of the
                    // bundle for this identity, never anything on this mesh. The sweep itself
                    // continues either way: one hollow bundle must not stop the good ones from
                    // seeding (#2193 §A).
                    if (PrebuiltAssemblySeeder.RequirePrebuilt(mesh.ServiceProvider))
                        logger?.LogError(
                            "ShippedPrebuiltBundles: bundle {Bundle} carries no assemblies (or no "
                            + "readable manifest) and this mesh sets {Key} — the types it should "
                            + "cover on framework {Identity} will NOT compile here. Fix: rebake and "
                            + "republish the bundle for this framework identity (MeshWeaver#2193).",
                            Path.GetFileName(bundlePath),
                            PrebuiltAssemblySeeder.RequirePrebuiltConfigKey,
                            PrebuiltAssemblySeeder.LiveFrameworkMvid);
                    else
                        logger?.LogWarning(
                            "ShippedPrebuiltBundles: bundle {Bundle} carries no assemblies (or no "
                            + "readable manifest) — nothing to adopt", Path.GetFileName(bundlePath));
                    return Observable.Return(default(SeedTally) with { BundlesHollow = 1 });
                }

                var present = assemblies
                    .Where(a => snapshot.Paths.Contains(a.NodePath))
                    .ToList();
                if (present.Count < assemblies.Count)
                    logger?.LogDebug(
                        "ShippedPrebuiltBundles: bundle {Bundle} names {Absent} NodeType(s) this "
                        + "mesh does not hold — skipped (an image ships one content set; a mesh "
                        + "serves a subset)",
                        Path.GetFileName(bundlePath), assemblies.Count - present.Count);
                if (present.Count == 0)
                    // 🚨 COUNTED, not just skipped. Every other zero-coverage exit here — an
                    // identity decline, a hollow bundle, a fault — already says so at Information
                    // or louder. This one is the silent one, so it is the only one the aggregate
                    // line below may attribute a zero to; a bare `default` would make "0 covered"
                    // indistinguishable from "0 covered because everything was DECLINED", and the
                    // line would then assert the opposite of what happened.
                    return Observable.Return(new SeedTally(0, 0, FilteredOut: 1));

                // 🚨 #3429 — every entry that NAMED a requested type is witnessed HERE, before
                // anything can decline it. That is what separates "the bytes were never here" from
                // "the bytes were here and did not land" — the distinction a shortfall line could
                // otherwise only guess at, and the guess that cost #3429 its investigation.
                if (onMatched is not null)
                    foreach (var entry in present)
                        onMatched(entry.NodePath);

                // Sequential (Concat, never Merge) for the same reason NodeTypeBakeStatus.Probe is:
                // the store is typically a shared network volume or blob container, and a fan-out
                // of lookups across every type at startup is the cold burst this whole mechanism
                // exists to remove. Each probe is a glob or a blob-exists.
                return present
                    .Select(entry => IsAlreadyCurrent(store, snapshot, mesh, entry, logger)
                        .Select(current => (Entry: entry, Current: current)))
                    .Concat()
                    .ToList()
                    .SelectMany(checks =>
                    {
                        var deviating = checks
                            .Where(c => !c.Current)
                            .Select(c => c.Entry.NodePath)
                            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
                        var alreadyCurrent = checks.Count - deviating.Count;
                        // An already-current entry is COVERED — the store holds its bytes under the
                        // record the mesh already carries. It counts toward Covered, so it must be
                        // witnessed here too, or the shortfall would name a type that is fine.
                        if (onCovered is not null)
                            foreach (var check in checks)
                                if (check.Current)
                                    onCovered(check.Entry.NodePath);

                        if (deviating.IsEmpty)
                        {
                            logger?.LogDebug(
                                "ShippedPrebuiltBundles: bundle {Bundle}: all {Current} NodeType(s) "
                                + "already carry this build and the store holds their bytes — no "
                                + "assembly read, no hub activated, no write",
                                Path.GetFileName(bundlePath), alreadyCurrent);
                            return Observable.Return(new SeedTally(0, alreadyCurrent));
                        }

                        return pool
                            .InvokeBlocking(_ => Plugin.Packaging.BundleReader
                                .ReadFile(bundlePath, deviating))
                            .SelectMany(payload => SeedPayloads(
                                mesh, bundlePath, manifest.FrameworkMvid,
                                payload.Assemblies, alreadyCurrent, logger, onCovered));
                    });
            })
            .Catch<SeedTally, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "ShippedPrebuiltBundles: bundle {Bundle} could not be read — skipped "
                    + "(the sweep compiles instead)", Path.GetFileName(bundlePath));
                return Observable.Return(default(SeedTally) with { Faulted = 1 });
            });

    /// <summary>
    /// Whether this bundle entry is ALREADY on the store under the record the mesh already holds,
    /// so seeding it would change nothing.
    ///
    /// <para>Two witnesses, and both are required. The RECORD
    /// (<see cref="PrebuiltAssemblySeeder.IsAlreadyAdopted"/>, which defers to
    /// <see cref="NodeTypeBakeStatus.Classify"/>) says the last adoption stamped exactly what this
    /// entry would stamp; the STORE says the bytes are still there. Trusting the record alone is
    /// the <see cref="BakeState.BytesMissing"/> trap — a cleared, remounted or partially-restored
    /// assembly volume leaves every record pristine over bytes that are gone, and a skip decided on
    /// it would leave those types permanently unbuilt.</para>
    ///
    /// <para>Fails SAFE: no node, unreadable content, no recorded version, or a store that throws
    /// all answer "not current", so the entry is seeded exactly as it is today.</para>
    /// </summary>
    private static IObservable<bool> IsAlreadyCurrent(
        IAssemblyStore store,
        TypeSnapshot snapshot,
        IMessageHub mesh,
        Plugin.Packaging.BundleReader.AssemblyRef entry,
        ILogger? logger)
    {
        if (!snapshot.Nodes.TryGetValue(entry.NodePath, out var node))
            return Observable.Return(false);

        // .ContentAs, never a cast: the enumeration crosses hubs, and a NodeTypeDefinition that
        // arrives as an untyped JsonElement (a TypeRegistry without the discriminator) would read
        // as null under `is` and silently re-seed everything.
        var definition = node.ContentAs<NodeTypeDefinition>(mesh.JsonSerializerOptions);
        if (definition is null)
            return Observable.Return(false);

        // 🚨 Probe at LastCompiledVersion, never at node.Version — that is the key the bytes were
        // uploaded under, and the key NodeTypeBakeStatus.ProbeOne will ask about in a moment. (The
        // two differ by design: Seed stamps the version it read BEFORE its own write, and that
        // write bumps the node.) No recorded version means no key to ask about, which Classify
        // already reads as NeverBuilt — resolved here as "not current", so it is seeded.
        if (definition.LastCompiledVersion is not { } version || version < 0)
            return Observable.Return(false);

        return store
            .TryGetAssemblyPath(entry.NodePath, version)
            .Take(1)
            .Select(path => PrebuiltAssemblySeeder.IsAlreadyAdopted(
                definition,
                storeHasBytes: !string.IsNullOrEmpty(path),
                entry.Dependencies,
                liveDependencyIdOf: NodeTypeCompilationHelpers.DependencyIdResolverOf(mesh),
                liveToolchainId: NodeTypeCompilationHelpers.ProcessToolchainId))
            .Catch<bool, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "ShippedPrebuiltBundles: assembly store lookup failed for {NodePath} — "
                    + "re-seeding it (an unreadable store must never read as 'already there')",
                    entry.NodePath);
                return Observable.Return(false);
            });
    }

    /// <summary>Seed the extracted payloads — the unchanged half of the pipeline.</summary>
    private static IObservable<SeedTally> SeedPayloads(
        IMessageHub mesh,
        string bundlePath,
        string? frameworkMvid,
        IReadOnlyList<Plugin.Packaging.BundleReader.Payload> assemblies,
        int alreadyCurrent,
        ILogger? logger,
        Action<string>? onCovered = null)
        => assemblies
            .Select(a => PrebuiltAssemblySeeder
                // 🚨 #2813 — a.SourceFingerprint is the producer's statement of which sources these
                // bytes came from; the owning hub compares it against its own live set and refuses
                // a provably-stale adoption. Null from a legacy bundle, which still adopts (as
                // AdoptedUnverified).
                .Seed(mesh, a.NodePath, a.Assembly, a.Pdb, frameworkMvid, logger, a.Dependencies,
                    a.SourceFingerprint)
                .Take(1)
                .Timeout(SeedBudget)
                .Do(adopted =>
                {
                    if (adopted)
                        onCovered?.Invoke(a.NodePath);
                })
                .Catch<bool, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "ShippedPrebuiltBundles: seeding {NodePath} from {Bundle} did not "
                        + "complete — the sweep compiles it instead",
                        a.NodePath, Path.GetFileName(bundlePath));
                    return Observable.Return(false);
                }))
            .Concat()
            .Count(adopted => adopted)
            .Select(adopted => new SeedTally(adopted, alreadyCurrent))
            .Do(tally => logger?.LogInformation(
                "ShippedPrebuiltBundles: bundle {Bundle}: adopted {Adopted}/{Deviating} "
                + "prebuilt assembly(ies); {Current} were already current",
                Path.GetFileName(bundlePath), tally.Adopted, assemblies.Count, tally.AlreadyCurrent));
}
