using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Sizes every test silo's grain-directory cache for a TEST cluster instead of a production one —
/// the dominant term in this assembly's heap, and the root of issue #2346.
///
/// <para><b>What it costs to leave at the default.</b> <see cref="GrainDirectoryOptions.CacheSize"/>
/// defaults to <see cref="GrainDirectoryOptions.DEFAULT_CACHE_SIZE"/> = 1,000,000, and Orleans
/// PRE-ALLOCATES the LRU's backing <c>ConcurrentDictionary</c> at that capacity. Every silo therefore
/// allocates a ~9.3 MB bucket array the moment it starts. That is right for a production silo tracking
/// a million grains; a test cluster tracks dozens. This assembly stands up ONE cluster PER TEST CLASS
/// — measured in a heap dump taken 95 s into a local run: <b>194 LruGrainDirectoryCache instances,
/// all of them LIVE, whose bucket arrays alone hold 1.80 GB — 62% of the 2.89 GB on the heap</b>
/// (~9.3 MB each), and every one of those arrays is a Large Object Heap allocation, so it is gen-2
/// traffic by construction.</para>
///
/// <para><b>Why that FAILS TESTS rather than merely wasting memory.</b> The xUnit test host runs
/// workstation GC (Orleans logs <c>ServerGC=False</c> at every silo start), so a multi-GB gen-2 heap
/// means multi-second BLOCKING pauses that stop the whole process — every cluster, every hub, every
/// test's own wait. CI caught Orleans' own health monitor naming them inside the shard-0 run that
/// failed <c>OrleansGrainTeardownStragglerTest</c>:</para>
/// <code>
/// LocalSiloHealthMonitor: .NET Thread Pool is exhibiting delays of 1.9080052s.
/// Watchdog: .NET Runtime Platform stalled for 00:00:03.55. Total GC Pause duration
///           during that period: 00:00:02.74. We are now using a total of 3775MB memory.
///           Collection counts per generation: 0: 1709, 1: 735, 2: 143
/// </code>
/// <para>A whole-process stall of that size expires whichever in-test budget happens to be open at
/// that moment — which is exactly the "rotating cast" on #2346: one condition, a different named
/// victim every run (<c>PartitionRootActivationTest</c>'s 5 s ping budget,
/// <c>OrleansGrainTeardownStragglerTest</c>'s 55 s token, and — decisively —
/// <c>RoutingBackpressureShapeTest</c>, which has no cluster and no network at all and can only be
/// broken by a process-wide stall).</para>
///
/// <para><b>Measured, both arms, back to back</b> (same machine, same half-hour, whole assembly per
/// iteration, <c>DOTNET_PROCESSOR_COUNT=4</c>, single variable = the one
/// <c>AddSiloBuilderConfigurator</c> line):</para>
/// <list type="bullet">
///   <item><b>Peak RSS per run — 4204 / 4248 / 4320 / 4357 / 4423 / 4460 MB without →
///   2041 / 2246 / 2267 / 2273 / 2352 / 2561 / 2853 / 3080 MB with.</b> Median 4.34 GB → 2.31 GB.
///   The ~2 GB that disappears is the 1.80 GB of bucket arrays the heap dump named — prediction and
///   measurement agree, which is the whole reason to trust the mechanism.</item>
///   <item>Wall clock is UNCHANGED on an 18-core dev box — 74.9–98.8 s without, 75.8–92.4 s with.
///   (An earlier "30% faster" reading of mine was machine load; it is retracted here rather than
///   left standing. The memory number is the one that holds.)</item>
///   <item>Flake rate: <b>2 failures / 17 runs without, 6 / 24 with</b> (12% vs 25%) — not
///   distinguishable at this sample size (Fisher p ≈ 0.4), and the families OVERLAP, which is the part
///   that actually attributes: <c>OrleansGrainTeardownStragglerTest</c> (#2301's "activation never
///   leaves the catalog") failed in BOTH arms, and the delegation family failed in both too
///   (<c>OrleansThreadStreamingTest</c> without, <c>OrleansNodeChangePropagationTest</c> with; plus one
///   <c>PodHubTransportTest</c>). Six named tests across 41 runs, no two arms sharing a majority
///   member: that is the #2346 population, which this change neither causes nor cures. Recorded rather
///   than rounded off, so a later measurement has a baseline to beat — and because the only mechanism
///   by which a SMALLER cache could touch these at all is timing (less GC, faster silo start
///   reshuffles interleavings); it cannot evict, so it cannot change an answer.</item>
/// </list>
///
/// <para><b>Not a tuning knob.</b> Nothing here raises a bound to make a test pass; it stops a test
/// fixture from allocating a production-scale buffer ~194 times in one process. The cache is a pure
/// optimisation — a miss costs an in-cluster directory lookup — and no test cluster in this assembly
/// comes within orders of magnitude of <see cref="CacheSize"/> distinct grains, so nothing is evicted
/// and no behaviour changes.</para>
///
/// <para>Applied centrally by <see cref="OrleansTestCluster.DeployAsync"/>, so a new test class that
/// takes the normal path inherits it without doing anything; it is registered BEFORE the caller's own
/// configurators, so a test that needs a different size can still set one. Ten classes build their own
/// <c>TestClusterBuilder</c> instead and opt in on the line after they create it —
/// <c>TestClusterSizingGuard</c> is what keeps that from being something anyone has to
/// remember.</para>
/// </summary>
public class TestGrainDirectorySizing : ISiloConfigurator
{
    /// <summary>
    /// Entries the directory cache is sized for.
    ///
    /// <para>Deliberately NOT the smallest number that would work. 50,000 is 20× below Orleans'
    /// production default — which is where ~95% of the memory saving already is (~400 KB of buckets
    /// per silo instead of ~9.3 MB) — while staying so far above any test cluster's grain count that
    /// the cache can never EVICT. That matters: eviction is the only way a smaller cache could change
    /// observable behaviour at all (a miss re-queries the directory), and picking a size where it
    /// cannot happen removes that question instead of arguing about it.</para>
    /// </summary>
    internal const int CacheSize = 50_000;

    public void Configure(ISiloBuilder siloBuilder) =>
        siloBuilder.Configure<GrainDirectoryOptions>(options => options.CacheSize = CacheSize);
}
