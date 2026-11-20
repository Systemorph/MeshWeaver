using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// A bundle source the deployment DECLARES beyond the two the boot seeding reads from disk (the
/// image's own <c>prebuilt/</c> shelf and a registry-fed published root): its bundles are offered
/// BEFORE the boot sweep judges a single NodeType, exactly as those two are.
///
/// <para>🚨 <b>Why this is an ordering seam, not a convenience.</b> The boot sweep judges a module's
/// NodeType in a partition this mesh does not track by one question — "does a bundle for this
/// framework identity name this type?" — and parks it when the answer is no (MeshWeaver#3583). An
/// installation whose modules come ONLY from a declared plugin registry
/// (<c>PluginCatalog:Registries</c>, no <c>bundles</c> block, so no registry-fed root on disk) had
/// its bundles adopted only by the default-install pass, and that pass is sequenced AFTER the bake
/// (<see cref="PreWarmCompletion"/>, #1114). So the sweep answered "no bundle names this type" for
/// every such type, counted each one as a regression and refused readiness; the publication gate
/// then refused the adoption stamps the install pass offered, BECAUSE readiness was refused, and
/// the replica could never recover (measured on the dev instance's first platform-4 pod,
/// 2026-11-20: 53 regressions, then <c>adopted 0/25</c> for the very package that held them).</para>
///
/// <para><b>Invariant restored:</b> the sweep judges a type only after every bundle source the
/// deployment declares has been OFFERED for it. An adoption made here is an ordinary
/// <c>PrebuiltAssemblySeeder</c> adoption: on a provisional process its shared stamp is HELD by the
/// <c>MeshPublicationGate</c> until the bake passes and discarded if it fails, while this process's
/// own <c>NodeTypeAdoptionRegistry</c> carries it so the sweep counts the type as baked. No gate is
/// relaxed: the framework identity and platform-range checks are the seeder's, unchanged.</para>
///
/// <para>Contract: COLD; emits ONCE (the number of assemblies adopted) and completes; NEVER faults
/// and is bounded by its own budgets — a source that cannot be reached answers 0 and says why, and
/// the sweep then judges as it did before this seam existed.</para>
/// </summary>
public interface IBootBundleSource
{
    /// <summary>What the log line calls this source.</summary>
    string Name { get; }

    /// <summary>Offers this source's bundles; emits the adopted-assembly count once.</summary>
    IObservable<int> SeedBeforeSweep();
}

/// <summary>
/// Runs every registered <see cref="IBootBundleSource"/> in registration order — the third stage of
/// the boot seeding, after the image's bundles and the published root (see
/// <c>DynamicTypePreWarmerHostedService</c>).
/// </summary>
public static class BootBundleSources
{
    /// <summary>
    /// Seeds every declared source of <paramref name="mesh"/>, one after the other, and emits the
    /// total adopted once. A source that faults in spite of its contract is logged and counted as
    /// zero: the seeding stage must never fault or wedge the warm-up behind it.
    /// </summary>
    /// <param name="mesh">The mesh hub whose container holds the sources.</param>
    /// <param name="logger">Optional.</param>
    public static IObservable<int> SeedAll(IMessageHub mesh, ILogger? logger)
        => Observable.Defer(() =>
        {
            var sources = mesh.ServiceProvider.GetServices<IBootBundleSource>().ToArray();
            if (sources.Length == 0)
                return Observable.Return(0);
            return sources
                .Select(source => source.SeedBeforeSweep()
                    .Take(1)
                    .DefaultIfEmpty(0)
                    .Do(adopted => logger?.LogInformation(
                        "Boot bundle source {Source}: {Adopted} prebuilt assembl(ies) adopted before the "
                        + "sweep judges any NodeType", source.Name, adopted))
                    .Catch<int, Exception>(ex =>
                    {
                        logger?.LogWarning(ex,
                            "Boot bundle source {Source} FAULTED although its contract is never to — "
                            + "counted as 0 adopted; the sweep judges its types as if it had offered "
                            + "nothing", source.Name);
                        return Observable.Return(0);
                    }))
                .Concat()
                .Sum();
        });
}
