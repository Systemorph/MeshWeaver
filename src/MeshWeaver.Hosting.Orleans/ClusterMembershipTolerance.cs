using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// The Orleans membership (failure-detection) tolerance every MeshWeaver silo runs with —
/// issue #6395.
///
/// <para><b>What happened.</b> On memex (2026-10-09 20:43:48Z, during a roll) two peers voted a
/// healthy silo Dead after it missed their direct probes; the silo read the verdict at its next
/// table refresh and killed itself (MembershipTableManager.KillMyselfLocally). Its IAmAlive row
/// was still being written (20:44:06Z, after the votes): the process was alive, only its probe
/// answers were late. Until this class the repository configured no ClusterMembershipOptions
/// outside test fixtures, so production ran Orleans' defaults (ProbeTimeout 5 s, suspicion after
/// NumMissedProbesLimit 3 misses, death on NumVotesForDeathDeclaration 2 votes): about 15 s of
/// unanswered probes killed a silo.</para>
///
/// <para><b>What this changes.</b> ProbeTimeout — both the probe period and each probe's timeout —
/// is raised to 10 s, so a silo must leave probes unanswered for about 30 s before it is
/// suspected: a long GC pause, a thread-pool stall or a pod network blip during a roll no longer
/// suffices. The missed-probe limit and the vote threshold are pinned at 3 and 2 deliberately:
/// Orleans caps the votes it requires at half the active silos, so a higher threshold changes
/// nothing in a cluster of four, and where it does apply it can leave a crashed silo unevicted
/// while its monitors are themselves rolling.</para>
///
/// <para><b>The cost.</b> A silo that really crashed (OOM kill, node loss) is evicted after about
/// 30 s instead of about 15 s, so calls to its grains fail over that much later. A rolling update
/// is not affected: a stopping silo announces its own ShuttingDown/Dead status and never waits on
/// probes.</para>
///
/// <para><b>What is not proven.</b> Why the probes went unanswered is not in the logs; settling it
/// needs the pod's CPU/GC metrics and the suspecting silos' probe-failure lines for
/// 20:43:30–20:43:50Z. This is tolerance, not a cure: a silo stalled for longer than the window
/// is still evicted, as it should be.</para>
///
/// <para>The baseline is inserted FIRST in the service collection, so every explicit
/// <c>Configure&lt;ClusterMembershipOptions&gt;</c> — a host's, a deployment's, a test fixture's
/// aggressive fast-failure settings — runs after it and wins, whatever the call order. Pinned by
/// ClusterMembershipToleranceTest.</para>
/// </summary>
internal static class ClusterMembershipTolerance
{
    /// <summary>The probe period and per-probe timeout: 10 s.</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Consecutive missed probes before a peer suspects a silo: 3.</summary>
    internal const int NumMissedProbesLimit = 3;

    /// <summary>Suspicion votes that declare a silo dead: 2 (Orleans caps it at half the active silos).</summary>
    internal const int NumVotesForDeathDeclaration = 2;

    /// <summary>
    /// Registers the MeshWeaver baseline for <c>ClusterMembershipOptions</c> at the FRONT of the
    /// service collection, so any later explicit configuration overrides it. Silo-only: the
    /// options are read by the silo's membership service alone.
    /// </summary>
    /// <param name="services">The silo's service collection.</param>
    /// <returns>The same service collection for further chaining.</returns>
    internal static IServiceCollection AddMeshWeaverClusterMembershipTolerance(this IServiceCollection services)
    {
        services.Insert(0, ServiceDescriptor.Singleton<IConfigureOptions<global::Orleans.Configuration.ClusterMembershipOptions>>(
            new ConfigureOptions<global::Orleans.Configuration.ClusterMembershipOptions>(Apply)));
        return services;
    }

    /// <summary>Applies the MeshWeaver baseline to the options.</summary>
    /// <param name="options">The membership options to configure.</param>
    internal static void Apply(global::Orleans.Configuration.ClusterMembershipOptions options)
    {
        options.ProbeTimeout = ProbeTimeout;
        options.NumMissedProbesLimit = NumMissedProbesLimit;
        options.NumVotesForDeathDeclaration = NumVotesForDeathDeclaration;
    }
}