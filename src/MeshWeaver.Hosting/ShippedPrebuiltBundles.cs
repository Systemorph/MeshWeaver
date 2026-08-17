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
                logger);
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
            return SeedBundles(mesh, dir, () => CompletePublishedBundlesOf(dir, logger), logger);
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
        foreach (var sourceDir in Directory
                     .EnumerateDirectories(identityDirectory)
                     .OrderBy(d => d, StringComparer.Ordinal))
        {
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

            var seeds = new List<IObservable<int>>
            {
                SeedBundles(mesh, imageDir,
                    () => Directory
                        .EnumerateFiles(imageDir, "*.zip", SearchOption.TopDirectoryOnly)
                        .OrderBy(f => f, StringComparer.Ordinal)
                        .ToList(),
                    logger, paths),
            };
            if (!string.IsNullOrWhiteSpace(publishedRoot))
            {
                var identityDir = Path.Combine(publishedRoot, PrebuiltAssemblySeeder.LiveFrameworkMvid);
                seeds.Add(SeedBundles(mesh, identityDir,
                    () => CompletePublishedBundlesOf(identityDir, logger), logger, paths));
            }
            return seeds.Concat().Aggregate(0, (total, adopted) => total + adopted);
        });

    /// <summary>The shared seeding core: the bundle files <paramref name="enumerateBundles"/>
    /// resolves (on the pool's blocking leg), through the one bundle pipeline.
    /// <paramref name="typePathFilter"/> replaces the mesh-wide NodeType enumeration when the
    /// caller already knows which types it is consuming for (#1707 slice 3).</summary>
    private static IObservable<int> SeedBundles(
        IMessageHub mesh, string dir, Func<List<string>> enumerateBundles, ILogger? logger,
        ImmutableHashSet<string>? typePathFilter = null)
        => Observable.Defer(() =>
        {
            if (!Directory.Exists(dir))
            {
                logger?.LogDebug(
                    "ShippedPrebuiltBundles: no bundle directory at {Directory} — nothing to seed", dir);
                return Observable.Return(0);
            }

            var meshService = mesh.ServiceProvider.GetService<IMeshService>();
            if (meshService is null && typePathFilter is null)
            {
                logger?.LogDebug("ShippedPrebuiltBundles: no IMeshService registered — nothing to seed");
                return Observable.Return(0);
            }
            var accessService = mesh.ServiceProvider.GetService<AccessService>();
            var pool = mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>().Get("prebuilt:files");
            var startedAt = DateTimeOffset.UtcNow;

            // ONE enumeration of the NodeType nodes this mesh actually holds — the filter that
            // keeps a bundle for content this deployment never imported (an image ships one
            // content set; a mesh serves a subset) from parking the boot on per-path waits for
            // nodes that do not exist. A caller-supplied set (install/push) skips the query.
            var existingPaths = typePathFilter is not null
                ? Observable.Return(typePathFilter)
                : meshService!
                    .Query<MeshNode>(MeshQueryRequest
                        .FromQuery($"nodeType:{MeshNode.NodeTypePath}"))
                    .Take(1)
                    .Timeout(EnumerationBudget)
                    .Select(change => change.Items
                        .Where(n => !string.IsNullOrEmpty(n.Path))
                        .Select(n => n.Path!)
                        .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase));

            // 🚨 System-scoped end-to-end: reading NodeType records across every partition and
            // stamping adopted builds is framework infrastructure, exactly like the sweep this
            // runs in front of. Observable.Using holds the scope across the live subscription.
            return Observable.Using(
                    () => AccessContextScope.AsSystem(accessService),
                    _ => pool
                        .InvokeBlocking(_ => enumerateBundles())
                        .SelectMany(bundles =>
                        {
                            if (bundles.Count == 0)
                            {
                                logger?.LogDebug(
                                    "ShippedPrebuiltBundles: {Directory} holds no bundles", dir);
                                return Observable.Return(0);
                            }

                            return existingPaths
                                .SelectMany(existing => bundles
                                    .Select(bundle => SeedBundle(mesh, pool, bundle, existing, logger))
                                    .Concat()
                                    .Aggregate(0, (total, adopted) => total + adopted))
                                .Do(adopted => logger?.LogInformation(
                                    "ShippedPrebuiltBundles: adopted {Adopted} prebuilt assembly(ies) "
                                    + "from {Bundles} shipped bundle(s) under {Directory} in {Elapsed}",
                                    adopted, bundles.Count, dir, DateTimeOffset.UtcNow - startedAt));
                        }))
                // The seeding is an optimisation in front of the sweep — a fault here must degrade
                // to "compile as today", never hold or fail the boot. Loud, so an operator can see
                // WHY an image that ships bundles still compiled.
                .Catch<int, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "ShippedPrebuiltBundles: seeding failed — the sweep will compile instead "
                        + "(the shipped bundles under {Directory} were not adopted)", dir);
                    return Observable.Return(0);
                });
        });

    /// <summary>One bundle: read, gate on the framework MVID once, seed every payload whose
    /// NodeType exists on this mesh. Emits the adopted count; folds its own faults to 0.</summary>
    private static IObservable<int> SeedBundle(
        IMessageHub mesh,
        IIoPool pool,
        string bundlePath,
        ImmutableHashSet<string> existingTypePaths,
        ILogger? logger)
        => pool
            .InvokeBlocking(_ => Plugin.Packaging.BundleReader.Read(File.ReadAllBytes(bundlePath)))
            .SelectMany(payload =>
            {
                var (manifest, assemblies) = payload;
                if (PrebuiltAssemblySeeder.DeclineReason(manifest?.FrameworkMvid) is { } reason)
                {
                    logger?.LogInformation(
                        "ShippedPrebuiltBundles: bundle {Bundle} DECLINED whole: {Reason} — "
                        + "the sweep compiles instead", Path.GetFileName(bundlePath), reason);
                    return Observable.Return(0);
                }
                if (assemblies.Count == 0)
                {
                    logger?.LogWarning(
                        "ShippedPrebuiltBundles: bundle {Bundle} carries no assemblies (or no "
                        + "readable manifest) — nothing to adopt", Path.GetFileName(bundlePath));
                    return Observable.Return(0);
                }

                var present = assemblies
                    .Where(a => existingTypePaths.Contains(a.NodePath))
                    .ToList();
                if (present.Count < assemblies.Count)
                    logger?.LogDebug(
                        "ShippedPrebuiltBundles: bundle {Bundle} names {Absent} NodeType(s) this "
                        + "mesh does not hold — skipped (an image ships one content set; a mesh "
                        + "serves a subset)",
                        Path.GetFileName(bundlePath), assemblies.Count - present.Count);
                if (present.Count == 0)
                    return Observable.Return(0);

                return present
                    .Select(a => PrebuiltAssemblySeeder
                        .Seed(mesh, a.NodePath, a.Assembly, a.Pdb, manifest!.FrameworkMvid, logger,
                            a.Dependencies)
                        .Take(1)
                        .Timeout(SeedBudget)
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
                    .Do(adopted => logger?.LogInformation(
                        "ShippedPrebuiltBundles: bundle {Bundle}: adopted {Adopted}/{Present} "
                        + "prebuilt assembly(ies)",
                        Path.GetFileName(bundlePath), adopted, present.Count));
            })
            .Catch<int, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "ShippedPrebuiltBundles: bundle {Bundle} could not be read — skipped "
                    + "(the sweep compiles instead)", Path.GetFileName(bundlePath));
                return Observable.Return(0);
            });
}
