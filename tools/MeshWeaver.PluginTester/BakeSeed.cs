using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The GATE's view of a bake it is asked to CONSUME (#1763): the bundle directory a
/// <c>mw-compiler compile</c> run wrote, read through the consumers' own codec
/// (<see cref="BundleReader.ReadManifest(string)"/>) before a mesh is built.
///
/// <para>This is the half that turns "bake" and "gate" from one fused pass into a producer and a
/// consumer. The producer emits assemblies with no mesh; the gate stands one up and proves the
/// BAKED BYTES render and pass their <c>Tests</c> areas — which is a strictly stronger claim than
/// the fused pass ever made, because the bytes it judges are the bytes that ship.</para>
///
/// <para>🚨 <b>Everything here is checked BEFORE the mesh boots, and a problem is a usage error
/// (exit 2), not a red gate.</b> A gate pointed at a bake it cannot consume silently compiles
/// everything and passes — indistinguishable from a gate that consumed the bake perfectly. That is
/// the #1814 failure shape one level down, and the cheapest place to catch it is here.</para>
/// </summary>
/// <param name="Directory">The bake directory.</param>
/// <param name="FrameworkIdentity">The identity recorded beside the bundles
/// (<see cref="BakeOutput.FrameworkMvidFile"/>).</param>
/// <param name="Bundles">The bundle files, ordinal by path.</param>
/// <param name="DeclaredTypePaths">Every NodeType path the bundles carry bytes for.</param>
public sealed record BakeSeed(
    string Directory,
    string FrameworkIdentity,
    ImmutableArray<string> Bundles,
    ImmutableSortedSet<string> DeclaredTypePaths)
{
    /// <summary>
    /// Reads a bake directory, or returns the reason it cannot be consumed. Never throws for a
    /// content reason — the caller turns a problem into a usage error with the flag in it.
    /// </summary>
    /// <param name="directory">The directory <c>mw-compiler compile --output</c> wrote.</param>
    /// <param name="liveFrameworkIdentity">The identity THIS process resolves — the address the
    /// gate will look under. A bake published under a different one is inert here.</param>
    public static (BakeSeed? Seed, string? Problem) Read(string directory, string liveFrameworkIdentity)
    {
        var full = Path.GetFullPath(directory);
        if (!System.IO.Directory.Exists(full))
            return (null, $"'{full}' does not exist — nothing to consume. It must be the "
                + "directory a `mw-compiler compile <root> --output <dir>` run wrote.");

        var identityFile = Path.Combine(full, BakeOutput.FrameworkMvidFile);
        if (!File.Exists(identityFile))
            return (null, $"'{full}' carries no {BakeOutput.FrameworkMvidFile} — it is not a bake "
                + "directory (or the bake died before it sealed one).");
        var identity = File.ReadAllText(identityFile).Trim();
        if (identity.Length == 0)
            return (null, $"'{identityFile}' is empty — the bake recorded no framework identity, "
                + "so nothing in this directory can be addressed.");

        // 🚨 THE ADDRESS CHECK, in-process. The framework identity is an ADDRESS: a bake publishes
        // under the identity ITS host resolved and a consumer only ever looks under the identity IT
        // resolves. When they differ every assembly is DECLINED, one by one, with a reason nobody
        // reads — and the gate compiles the whole tree and goes green, having verified none of the
        // bytes that ship. Refusing up front makes the mismatch the loud, named thing it is.
        if (!string.Equals(identity, liveFrameworkIdentity, StringComparison.Ordinal))
            return (null,
                $"the bake in '{full}' is keyed to framework identity '{identity}' but this "
                + $"process resolves '{liveFrameworkIdentity}'. Every assembly would be declined "
                + "and the gate would compile the tree itself — passing without ever judging the "
                + "baked bytes. Bake and gate must run in the SAME image and on the SAME "
                + "architecture (`mw-plugin-test framework-identity <app-dir> --expect <identity>` "
                + "reports the difference).");

        var bundles = System.IO.Directory
            .EnumerateFiles(full, "*.zip", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToImmutableArray();
        if (bundles.Length == 0)
            return (null, $"'{full}' holds no *.zip bundles — the bake produced nothing to "
                + "consume. A gate asked to consume a bake and handed an empty one has no way to "
                + "tell that apart from a bake it consumed perfectly.");

        // 🚨 OrdinalIgnoreCase, matching ShippedPrebuiltBundles.SeedForTypes' own path set
        // exactly. The accounting below decides a VERDICT by comparing this set with what
        // the installer asked for, so a comparer stricter than the seeder's would report a
        // shortfall for an assembly the seeder had happily adopted under a case difference.
        var declared = ImmutableSortedSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in bundles)
        {
            BundleReader.Manifest? manifest;
            try
            {
                manifest = BundleReader.ReadManifest(bundle);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return (null, $"'{bundle}' is not a readable bundle — {ex.GetType().Name}: {ex.Message}");
            }
            if (manifest?.Assemblies is null)
                return (null, $"'{bundle}' carries no assembly manifest — it is not a "
                    + "prebuilt-assembly bundle.");
            // A bundle keyed to a different identity than the directory's own marker cannot be
            // adopted here either, and it means the directory mixes producers.
            if (manifest.FrameworkMvid is { Length: > 0 } bundleIdentity
                && !string.Equals(bundleIdentity, identity, StringComparison.Ordinal))
                return (null, $"'{bundle}' is keyed to '{bundleIdentity}' while the directory "
                    + $"declares '{identity}' — the bake directory mixes producers.");
            foreach (var assembly in manifest.Assemblies)
                declared.Add(assembly.NodePath);
        }

        return (new BakeSeed(full, identity, bundles, declared.ToImmutable()), null);
    }

    /// <summary>A one-line account for the run header.</summary>
    public string Describe() =>
        $"{Bundles.Length} bundle(s), {DeclaredTypePaths.Count} assembly(ies), "
        + $"framework={FrameworkIdentity}";
}

/// <summary>
/// The gate's <see cref="IPrebuiltAssemblyConsumer"/>: adoption restricted to ONE bake directory,
/// with the accounting the gate's postcondition needs.
///
/// <para>🚨 It delegates to <see cref="ShippedPrebuiltBundles.SeedForTypes(MeshWeaver.Messaging.IMessageHub, System.Collections.Generic.IReadOnlyCollection{string}, Microsoft.Extensions.Logging.ILogger, string, string)"/> — the SAME consumption
/// implementation a portal runs — rather than re-reading the bundles itself. A gate that proved a
/// bake adoptable through a second, gate-only reader would prove nothing about the reader that
/// actually runs in production; the framework gate, the per-type dependency-record gate and the
/// already-current skip all have to be the ones that ship.</para>
///
/// <para>Wired into the gate mesh's services, so <c>PackageInstaller</c>'s existing
/// adopt-before-compile step (#1707 slice 3) picks it up with no new call site: the installer asks
/// for the types it just wrote, this answers with the bake's bytes, and the release requests that
/// follow settle without Roslyn. That is the whole "the gate CONSUMES a bake" change — the gate
/// stops being a producer and becomes the runtime judge of someone else's bytes.</para>
/// </summary>
/// <param name="mesh">Accessor for the gate mesh hub. A FUNCTION, not the hub: this consumer is
/// constructed before the mesh exists (it has to be registered into the collection the mesh is
/// built from), and it is registered as a single INSTANCE so its accounting is one object rather
/// than one per container that happens to resolve the interface.</param>
/// <param name="seed">The bake being consumed.</param>
public sealed class BakeSeedConsumer(
    Func<IMessageHub> mesh,
    BakeSeed seed) : IPrebuiltAssemblyConsumer
{
    /// <summary>The logger category every adoption line rides — named once so the gate can raise
    /// exactly this category to Information and nothing else (see GateMesh.Create).</summary>
    public const string LogCategory = "bake-seed";

    private readonly object gate = new();
    // Same comparer as BakeSeed.DeclaredTypePaths and as the seeder's own path set — the three
    // are intersected to reach a verdict and must agree on what "the same node path" means.
    private ImmutableSortedSet<string> requested =
        ImmutableSortedSet.Create<string>(StringComparer.OrdinalIgnoreCase);
    // The paths the seeder actually BACKED, witnessed one by one as it backs them. A count alone
    // cannot answer "which one declined", and that is the only question a shortfall raises.
    private ImmutableSortedSet<string> covered =
        ImmutableSortedSet.Create<string>(StringComparer.OrdinalIgnoreCase);
    private int adopted;

    /// <summary>Every NodeType path an install asked this consumer about, ordinal.</summary>
    public ImmutableSortedSet<string> Requested
    {
        get { lock (gate) return requested; }
    }

    /// <summary>
    /// Adoption EVENTS across the whole run — the sum of the per-call counts the seeder returned.
    ///
    /// <para>🚨 This is NOT the number of assemblies adopted, and it must never be reported as one.
    /// A path covered under two packages is seeded twice and counted twice, which is how a gate
    /// reported the impossible <c>adopted 92 of 90</c> (#2697). Use <see cref="AdoptedPaths"/> for
    /// any verdict or log line; this stays because "how many times did the seeder act" is a
    /// different, occasionally useful fact.</para>
    /// </summary>
    public int Adopted => Volatile.Read(ref adopted);

    /// <summary>
    /// The number of DISTINCT baked assemblies this run adopted, counted over the same set the
    /// DECLINED list is the complement of — so <c>AdoptedPaths + declined == expected</c> holds by
    /// construction rather than by luck.
    /// </summary>
    public int AdoptedPaths => AdoptedAmong(seed.DeclaredTypePaths, Requested, Covered);

    /// <summary>
    /// The adopted set: everything the bake declared AND the install requested AND the seeder
    /// backed. Pure, so the accounting can be pinned without standing up a mesh.
    /// </summary>
    public static int AdoptedAmong(
        ImmutableSortedSet<string> declared,
        ImmutableSortedSet<string> requested,
        ImmutableSortedSet<string> covered)
        => declared.Intersect(requested).Intersect(covered).Count;

    /// <summary>Every NodeType path the seeder BACKED from this bake — adopted now or already
    /// current. The witness behind <see cref="Shortfall"/>'s named verdict.</summary>
    public ImmutableSortedSet<string> Covered
    {
        get { lock (gate) return covered; }
    }

    /// <summary>The bake this consumer serves.</summary>
    public BakeSeed Seed => seed;

    /// <inheritdoc />
    public IObservable<int> SeedForTypes(IReadOnlyCollection<string> typePaths)
        => Observable.Defer(() =>
        {
            lock (gate)
                requested = requested.Union(typePaths);
            var hub = mesh();
            var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(LogCategory);
            return ShippedPrebuiltBundles
                .SeedForTypes(hub, typePaths, logger, imageDirectory: seed.Directory,
                    // The gate consumes exactly the bake it was pointed at. Null here falls back to
                    // the host's PreWarm:PrebuiltBundleRoot configuration, which for the gate mesh
                    // is empty by construction (its IConfiguration is a bare ConfigurationBuilder)
                    // — so there is only ever one source and "the gate adopted N" stays
                    // attributable to the bake under test.
                    publishedRoot: null,
                    // Witnessed per path, on the seeder's own pool threads — hence the lock.
                    onCovered: path =>
                    {
                        lock (gate) covered = covered.Add(path);
                    })
                .Do(count => Interlocked.Add(ref adopted, count));
        });

    /// <summary>
    /// The postcondition: what the bake declared for the types the run actually installed, versus
    /// what was adopted. Null when the gate consumed everything it should have.
    ///
    /// <para>🚨 <b>A gate that consumed nothing must not look like a gate that consumed
    /// everything.</b> Adoption is invisible in a gate verdict by construction — a type the gate
    /// compiled itself renders and tests exactly like a type it adopted — so without this the
    /// entire consuming half could silently stop working and every run would stay green. That is
    /// the same shape as a skipped CI job wearing a passing tick.</para>
    /// </summary>
    public string? Shortfall() =>
        DescribeShortfall(seed.DeclaredTypePaths, Requested, Covered, seed.Directory);

    /// <summary>
    /// <see cref="Shortfall"/>'s verdict as a PURE function of the four facts it rests on, so the
    /// accounting can be pinned without standing up a mesh (the same seam
    /// <c>PrebuiltAssemblySeeder.DeclineReason</c> exposes for the same reason).
    /// </summary>
    /// <param name="declared">Every path the bake carries bytes for.</param>
    /// <param name="requested">Every path the install asked the consumer about.</param>
    /// <param name="covered">Every path the seeder actually backed.</param>
    /// <param name="directory">The bake directory, for the no-overlap message.</param>
    /// <remarks>🚨 There is deliberately no <c>adopted</c> parameter. It used to take the seeder's
    /// running EVENT count and report it as-is, so a path covered under two packages counted twice
    /// and the verdict could read <c>adopted 92 of 90</c> — an impossible sentence in the one line
    /// meant to be trusted (#2697). The adopted number is now DERIVED from the same sets the
    /// DECLINED list comes from, so the arithmetic cannot disagree with the naming.</remarks>
    public static string? DescribeShortfall(
        ImmutableSortedSet<string> declared,
        ImmutableSortedSet<string> requested,
        ImmutableSortedSet<string> covered,
        string directory)
    {
        var expected = declared.Intersect(requested);
        if (expected.Count == 0)
            return declared.Count == 0
                ? null
                : $"the bake in '{directory}' declares assemblies for "
                  + $"{declared.Count} NodeType(s), NONE of which this run installed "
                  + "— the gate judged none of the baked bytes. Bake and gate must be staged from "
                  + "the same tree (declared: "
                  + $"{string.Join(", ", declared.Take(5))}"
                  + (declared.Count > 5 ? ", …" : "") + ").";
        var missing = expected.Except(covered);
        if (missing.Count == 0)
            return null;
        // 🚨 NAME them. This used to say WHICH assembly declined "is not knowable here", because
        // the seeder reported only a COUNT; it now witnesses every path it backs, so the single
        // question a shortfall raises is answered in the verdict itself. That matters because the
        // per-assembly reason it deferred to is logged at Information and THE GATE MESH RUNS AT
        // WARNING — the pointer led to lines the gate never writes, which is why a 1-of-87
        // shortfall was undiagnosable from a CI log (MeshWeaver.Crm main, red from 2026-08-28
        // 21:54 for ~12 h with every per-type verdict green). A verdict carries its own evidence.
        return $"the gate adopted {expected.Intersect(covered).Count} of {expected.Count} baked "
            + "assembly(ies) for the types "
            + $"it installed — {missing.Count} were DECLINED and compiled locally, so the gate did "
            + "not judge the bytes the bake shipped for them. DECLINED: "
            + $"{string.Join(", ", missing.Take(20))}"
            + (missing.Count > 20 ? ", …" : "")
            + ". The per-assembly reason (framework identity, the per-type dependency record, or a "
            + $"payload the bundle's manifest names but does not carry) is logged above under the "
            + $"'{LogCategory}' category, which the gate raises to Information whenever it consumes "
            + "a bake; outside the gate, enable that category to read it.";
    }
}
