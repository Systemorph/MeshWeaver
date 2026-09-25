using System.Diagnostics.CodeAnalysis;
using Orleans.GrainDirectory;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// 🚨 Issue #5037. Routes every <c>[StatelessWorker]</c> grain type to
/// <see cref="StatelessWorkerGrainDirectory"/>, so that PLACING one never consults the cluster's
/// distributed (DHT) grain directory.
///
/// <para><b>The defect this removes.</b> Orleans 10's
/// <c>PlacementService.PlacementWorker.ExecutePlacementAsync</c> begins EVERY placement with
/// <c>GrainLocator.Lookup(grainId)</c> — including a stateless worker's, although
/// <c>StatelessWorkerPlacement.IsUsingGrainDirectory</c> is <c>false</c> and no stateless-worker
/// activation is ever registered there. For a type on the default directory that lookup is
/// <c>LocalGrainDirectory.LookupAsync</c>, which forwards to whichever silo OWNS the grain id's hash
/// on the directory ring. So reaching <c>routing/default</c> — a grain
/// <c>StatelessWorkerDirector</c> then places back on the CALLING silo — waited on a remote silo's
/// directory partition to answer "not registered". Whenever that owner could not answer (a silo
/// that died without leaving, which membership takes ~75 s to declare with the portal's probe
/// settings; a silo just joined the ring and still booting; one stalled on its thread pool) the
/// lookup outlived the 30 s <c>Orleans.Placement</c> Polly budget and EVERY message queued behind
/// the same placement work item was rejected at once — the 331 deliveries dropped inside ~50 ms on
/// 2026-09-20 22:25:14Z. The lookup's answer can only ever be "none", so the whole dependency bought
/// nothing.</para>
///
/// <para><b>Why a resolver, not a <c>[GrainDirectory]</c> attribute.</b> The attribute travels in the
/// CLUSTER manifest: during a rolling deploy a silo still on the previous image would read the new
/// grain property and look for a keyed directory it never registered
/// (<c>KeyNotFoundException: Could not resolve grain directory</c>). A resolver is consulted only by
/// the silo that registered it, so each silo decides for itself and a mixed-version cluster behaves
/// exactly as its parts did — the new silos stop asking, the old ones keep asking.</para>
///
/// <para><b>Why by placement strategy rather than by naming <c>RoutingGrain</c>.</b> The property it
/// reads is Orleans' own declaration that the type never uses the directory; applying the local
/// answer to every such type is the declaration taken at its word, and a stateless worker added
/// later is covered without anyone remembering this file.</para>
///
/// <para>Full reference: <c>Doc/Architecture/RoutingEntryPoint</c>.</para>
/// </summary>
internal sealed class StatelessWorkerGrainDirectoryResolver : IGrainDirectoryResolver
{
    /// <summary>
    /// The value Orleans writes under <see cref="WellKnownGrainTypeProperties.PlacementStrategy"/>
    /// for a <c>[StatelessWorker]</c> type — the placement strategy's type name, which Orleans keeps
    /// internal, hence the literal (the test pins it against a live silo's manifest).
    /// </summary>
    internal const string StatelessWorkerPlacementName = "StatelessWorkerPlacement";

    private readonly StatelessWorkerGrainDirectory directory = new();

    /// <inheritdoc />
    public bool TryResolveGrainDirectory(
        GrainType grainType,
        GrainProperties? properties,
        [NotNullWhen(true)] out IGrainDirectory? grainDirectory)
    {
        if (IsStatelessWorker(properties))
        {
            grainDirectory = directory;
            return true;
        }

        grainDirectory = null;
        return false;
    }

    /// <summary>
    /// True when the grain type's manifest properties declare the stateless-worker placement.
    /// </summary>
    /// <param name="properties">The grain type's properties; null when the type is unknown here.</param>
    /// <returns>Whether the type is placed as a stateless worker.</returns>
    internal static bool IsStatelessWorker(GrainProperties? properties) =>
        properties is not null
        && properties.Properties.TryGetValue(WellKnownGrainTypeProperties.PlacementStrategy, out var strategy)
        && string.Equals(strategy, StatelessWorkerPlacementName, StringComparison.Ordinal);
}

/// <summary>
/// The directory a stateless worker resolves to: answered in-process, holding nothing.
///
/// <para>This is not a stub standing in for a real directory. It is the exact answer the DHT gave for
/// a stateless worker — Orleans never registers one (<c>StatelessWorkerPlacement.IsUsingGrainDirectory</c>
/// is false, so <c>ActivationData</c> skips registration) — minus the remote hop that could fail.
/// <see cref="Lookup"/> therefore reports "no activation registered", and placement proceeds straight
/// to <c>StatelessWorkerDirector</c>, which prefers the calling silo.</para>
///
/// <para><see cref="Register"/> is never reached for a stateless worker. Should it ever be, answering
/// with the caller's own address ("you are the registered activation") is the only reply that cannot
/// redirect a message somewhere else — a stateless worker has a local activation per silo by design.</para>
/// </summary>
internal sealed class StatelessWorkerGrainDirectory : IGrainDirectory
{
    private static readonly Task<GrainAddress?> NotRegistered = Task.FromResult<GrainAddress?>(null);

    /// <inheritdoc />
    public Task<GrainAddress?> Lookup(GrainId grainId) => NotRegistered;

    /// <inheritdoc />
    public Task<GrainAddress?> Register(GrainAddress address) => Task.FromResult<GrainAddress?>(address);

    /// <inheritdoc />
    public Task Unregister(GrainAddress address) => Task.CompletedTask;

    /// <inheritdoc />
    public Task UnregisterSilos(List<SiloAddress> siloAddresses) => Task.CompletedTask;
}
