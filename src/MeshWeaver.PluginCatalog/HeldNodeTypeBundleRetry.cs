using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Hosting;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>A bake that lands AFTER its source is adopted when it lands</b>: the registry
/// reconcile's held-NodeType lane.
///
/// <para><b>The shape, as measured</b> (an enterprise client's test instance, 2026-10-18). A
/// repository merged a change to one NodeType at 06:52Z. The registry reconcile landed the new
/// SOURCE at 06:56Z. The bake for this framework identity was not published yet, so the bundle
/// that named the type carried the previous source fingerprint. It was declined (#2813). Because
/// the instance only uses prebuilt bytes, the type went into a delivery hold and kept serving its
/// last build. That is correct. The bake was published at 07:43Z. After that nothing asked again,
/// and the type stayed held for the life of the process:</para>
/// <list type="bullet">
///   <item>the hold lifts only on a source change or on a release request "after the rebake"
///   (<c>PrebuiltAssemblySeeder.UntrackedPartitionParkReason</c>), and no component issued that
///   request;</item>
///   <item>the content lane's identity gate compares <c>ModuleVersion</c>, a hash over the
///   SOURCE. A rebake does not move it, so every later pass read the package as up to date;</item>
///   <item>the module lane (<c>ReconcileModules</c>) visits only packages that declare a compiled
///   module;</item>
///   <item>a registry-fed published root is the snapshot <c>bundle-fetch</c> pulled when the pod
///   started, and the seal-arrival trigger listens for announcements that only the fleet's own
///   instances receive.</item>
/// </list>
///
/// <para><b>The repair follows the registry's own index, on every reconcile pass.</b> The
/// boot pass, every module-published broadcast and every safety-net tick re-read the bundle index
/// anyway. On each pass, this lane asks the index about every package that has a NodeType in a
/// delivery hold (<see cref="NodeTypeLiveRecordCensus.AwaitingBundle"/>). When the bundle the
/// index advertises for that package differs from the one this process last tried, it adopts the
/// bundle through <see cref="PluginBundleClient.Adopt"/>. That is the same download and the same
/// per-assembly gates as an install, so a bundle whose fingerprint still disagrees is declined by
/// name exactly as before, and the hold stands. A bundle that matches is adopted, the record names
/// the new bytes, and the hold lifts. No timer or poll is added, and nothing is compiled.</para>
///
/// <para><b>Bounded.</b> A package is downloaded again only when the index names a different
/// bundle for it (by its digest-addressed artifact, else by version and URL), or when
/// <see cref="ReattemptAfter"/> has passed since the last attempt. That covers a registry that
/// re-shelves new bytes under an unchanged reference. A pass with nothing held costs one
/// in-memory read.</para>
/// </summary>
public sealed class HeldNodeTypeBundleRetry
{
    /// <summary>How long an unchanged advertised bundle is trusted to be the one already tried.</summary>
    public static readonly TimeSpan ReattemptAfter = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, Attempt> attempts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One adoption this process made for one package from one registry.</summary>
    /// <param name="BundleKey">The advertised bundle it tried (<see cref="BundleKeyOf"/>).</param>
    /// <param name="At">When.</param>
    public sealed record Attempt(string BundleKey, DateTimeOffset At);

    /// <summary>One package this pass adopts again, and why.</summary>
    /// <param name="Package">The package id.</param>
    /// <param name="BundleKey">The advertised bundle.</param>
    /// <param name="HeldTypes">The held NodeType paths of that package.</param>
    /// <param name="Because">Why now.</param>
    public sealed record Retry(string Package, string BundleKey, ImmutableArray<string> HeldTypes, string Because);

    /// <summary>
    /// The advertised bundle as one comparable key: the digest-addressed artifact when the registry
    /// names one (it moves with every rebake), else version and URL. Pure.
    /// </summary>
    /// <param name="bundle">The index entry.</param>
    public static string BundleKeyOf(PluginBundleClient.BundleRef bundle)
        => bundle.Artifact is { Length: > 0 } artifact
            ? artifact
            : $"{bundle.Version}|{bundle.Url}";

    /// <summary>
    /// 🚨 THE DECISION, pure: which packages this pass adopts again. For each package of a held
    /// type that the index advertises: when nothing was tried for it yet, when the advertised bundle
    /// differs from the one tried, or when the last attempt is older than
    /// <paramref name="reattemptAfter"/>. A package the index does not advertise is not retried:
    /// there is nothing to adopt, and the install lane already names that miss.
    /// </summary>
    /// <param name="heldTypePaths">NodeType paths in a delivery hold.</param>
    /// <param name="advertised">The index's bundles.</param>
    /// <param name="lastAttempt">What this process last tried per package, from THIS registry.</param>
    /// <param name="now">The clock.</param>
    /// <param name="reattemptAfter">How long an unchanged bundle counts as already tried.</param>
    public static ImmutableArray<Retry> Plan(
        IEnumerable<string> heldTypePaths,
        IEnumerable<PluginBundleClient.BundleRef> advertised,
        Func<string, Attempt?> lastAttempt,
        DateTimeOffset now,
        TimeSpan reattemptAfter)
    {
        ArgumentNullException.ThrowIfNull(heldTypePaths);
        ArgumentNullException.ThrowIfNull(advertised);
        ArgumentNullException.ThrowIfNull(lastAttempt);

        var byPackage = heldTypePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => (Package: RegistryShippedBuildSource.PackageOf(p), Path: p))
            .Where(x => x.Package is not null)
            .GroupBy(x => x.Package!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Path).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);
        if (byPackage.Count == 0)
            return [];

        var bundles = advertised
            .Where(b => b is not null && !string.IsNullOrWhiteSpace(b.Plugin))
            .GroupBy(b => b.Plugin, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var plan = ImmutableArray.CreateBuilder<Retry>();
        foreach (var (package, held) in byPackage.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!bundles.TryGetValue(package, out var bundle))
                continue;
            var key = BundleKeyOf(bundle);
            var last = lastAttempt(package);
            string? because = last switch
            {
                null => "no bundle was tried for it in this process yet",
                _ when !string.Equals(last.BundleKey, key, StringComparison.Ordinal) =>
                    $"the registry now advertises {key}, not {last.BundleKey} as tried at {last.At:u}",
                _ when now - last.At >= reattemptAfter =>
                    $"the bundle tried at {last.At:u} is unchanged, but {reattemptAfter.TotalHours:0.#} h have passed",
                _ => null,
            };
            if (because is not null)
                plan.Add(new Retry(package, key, held, because));
        }
        return plan.ToImmutable();
    }

    /// <summary>The held NodeType paths the live census names, or empty when no census was taken
    /// on this replica (a host without the pre-warm's standing catalog subscription).</summary>
    /// <param name="hub">The mesh hub.</param>
    public static ImmutableArray<string> HeldTypes(IMessageHub hub)
        => hub.ServiceProvider.GetService<NodeTypeBakeReportRegistry>()?.LiveRecords?.AwaitingBundle
           ?? ImmutableArray<string>.Empty;

    /// <summary>
    /// One pass for one registry: plan from the census and the index, then adopt each planned
    /// package in turn. Emits how many assemblies were adopted. Never faults: an index that cannot
    /// be read and a failed adoption are logged, and the next pass asks again.
    /// </summary>
    /// <param name="hub">The mesh hub.</param>
    /// <param name="bundles">The registry's bundle client for this pass.</param>
    /// <param name="registryUrl">The registry, which keys the attempts.</param>
    /// <param name="registryName">For log copy.</param>
    /// <param name="installedPackages">Package ids served by this registry — only those are asked.</param>
    /// <param name="logger">Where the outcome is said.</param>
    public IObservable<int> Run(
        IMessageHub hub,
        PluginBundleClient bundles,
        string registryUrl,
        string registryName,
        IReadOnlyCollection<string> installedPackages,
        ILogger? logger)
        => Observable.Defer(() =>
        {
            var served = installedPackages.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            var held = HeldTypes(hub)
                .Where(p => RegistryShippedBuildSource.PackageOf(p) is { } package && served.Contains(package))
                .ToImmutableArray();
            if (held.IsEmpty)
                return Observable.Return(0);

            return bundles.FetchIndex()
                .Take(1)
                .SelectMany(index =>
                {
                    var plan = Plan(
                        held, index.Bundles ?? [],
                        package => attempts.TryGetValue(Key(registryUrl, package), out var a) ? a : null,
                        DateTimeOffset.UtcNow, ReattemptAfter);
                    if (plan.IsEmpty)
                    {
                        logger?.LogDebug(
                            "[RegistryUpdate] {Name}: {Count} NodeType(s) are held for a bundle, and the index "
                            + "advertises nothing new for their packages — the holds stand.",
                            registryName, held.Length);
                        return Observable.Return(0);
                    }
                    return plan
                        .Select(retry => AdoptOne(bundles, registryUrl, registryName, retry, logger))
                        .ToObservable()
                        .Concat()
                        .Sum();
                })
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex,
                        "[RegistryUpdate] {Name}: the bundle index could not be read for the {Count} NodeType(s) "
                        + "held for a bundle — the next pass asks again.", registryName, held.Length);
                    return Observable.Return(0);
                });
        });

    private IObservable<int> AdoptOne(
        PluginBundleClient bundles, string registryUrl, string registryName, Retry retry, ILogger? logger)
        => Observable.Defer(() =>
        {
            // Recorded BEFORE the download, so a pass that faults mid-transfer does not make the
            // next pass download again for the same bytes; the re-attempt bound still applies.
            attempts[Key(registryUrl, retry.Package)] = new Attempt(retry.BundleKey, DateTimeOffset.UtcNow);
            logger?.LogInformation(
                "[RegistryUpdate] {Name}: {Package} has {Count} NodeType(s) held for a bundle ({Types}) — "
                + "adopting its advertised bundle {Bundle} again, because {Because}.",
                registryName, retry.Package, retry.HeldTypes.Length, string.Join(", ", retry.HeldTypes.Take(8)),
                retry.BundleKey, retry.Because);
            return bundles.Adopt(retry.Package)
                .Take(1)
                .DefaultIfEmpty(0)
                .Do(adopted => logger?.LogInformation(
                    "[RegistryUpdate] {Name}: {Package}: adopted {Adopted} assembly(ies) from {Bundle} for the "
                    + "held NodeType(s). A type whose bytes still disagree with its source stays held, and "
                    + "its own record says why.",
                    registryName, retry.Package, adopted, retry.BundleKey))
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex,
                        "[RegistryUpdate] {Name}: adopting {Package}'s bundle {Bundle} for its held NodeType(s) "
                        + "failed — the holds stand; a later pass asks again.",
                        registryName, retry.Package, retry.BundleKey);
                    return Observable.Return(0);
                });
        });

    private static string Key(string registryUrl, string package) => $"{registryUrl.TrimEnd('/')}|{package}";
}
