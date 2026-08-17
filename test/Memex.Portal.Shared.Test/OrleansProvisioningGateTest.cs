using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Distributed;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins <see cref="OrleansProvisioningGate"/> — the consume-side half of the Orleans provisioning
/// contract (#1798). The produce side (<c>OrleansClusteringSetup.VerifyProvisionedAsync</c>)
/// asserts the same key set before reporting success; these two independent checks of one contract
/// are what makes "the migration skipped Orleans" impossible to discover only as a crash loop.
/// </summary>
public class OrleansProvisioningGateTest
{
    /// <summary>
    /// A connection string nothing listens on — the gate gets connection-refused immediately on
    /// loopback. Same device as <see cref="DbVersionGateTest"/>.
    /// </summary>
    private const string Unreachable =
        "Host=127.0.0.1;Port=59322;Username=nobody;Password=nothing;Database=nowhere;Timeout=2";

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => StopRequested = true;
    }

    private static OrleansProvisioningGate Gate(RecordingLifetime lifetime, bool grainStorage = true)
        => new(Unreachable, grainStorage, lifetime, NullLogger<OrleansProvisioningGate>.Instance);

    /// <summary>
    /// The gate's reason to exist: a silo that cannot VERIFY its clustering store must not join a
    /// cluster. Failing open here would reproduce #1798 — the portal starts, and the first thing
    /// the AdoNet provider does is throw a LINQ exception naming nothing.
    /// </summary>
    [Fact]
    public async Task AnUnverifiableOrleansDatabase_FailsClosed()
    {
        var lifetime = new RecordingLifetime();

        await Gate(lifetime).StartAsync(TestContext.Current.CancellationToken);

        lifetime.StopRequested.Should().BeTrue(
            "a silo configured for AdoNet clustering must refuse to start when it cannot confirm "
            + "the orleans database is provisioned — otherwise the failure surfaces as a crash "
            + "loop on 'Sequence contains no elements', which names no table, key or container");
    }

    /// <summary>
    /// 🚨 The regression pin inherited from issue #1183 via <see cref="DbVersionGate"/>: a
    /// cancellation of the HOST's startup token means shutdown raced startup (a rollout replacing
    /// the pod), which is not a provisioning verdict. Folding it into the catch-all would log a
    /// critical "refusing to start" for a process that was merely told to stop.
    /// </summary>
    [Fact]
    public async Task AStartupAbortedByShutdown_Propagates_InsteadOfFailingTheGate()
    {
        var lifetime = new RecordingLifetime();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Gate(lifetime).StartAsync(cts.Token));

        lifetime.StopRequested.Should().BeFalse(
            "an aborted startup is not a provisioning failure — the gate must not StopApplication "
            + "for a host that is already tearing down");
    }

    /// <summary>
    /// The gate asks for exactly what the silo configures. <c>requiresGrainStorage</c> tracks
    /// whether <c>Program.cs</c> wired an AdoNet <c>PubSubStore</c>, so the gate can never demand
    /// rows a deployment does not use — the shape that would turn a correct Localhost-PubSub
    /// deployment into a startup refusal.
    /// </summary>
    [Fact]
    public void RequiredKeys_TrackWhatTheSiloActuallyConfigures()
    {
        var membershipOnly = OrleansProvisioningGate.RequiredKeys(requiresGrainStorage: false);
        var withStorage = OrleansProvisioningGate.RequiredKeys(requiresGrainStorage: true);

        // Clustering is on whenever this gate is registered, so membership keys are always required.
        Assert.Equal(OrleansProvisioningGate.MembershipQueryKeys, membershipOnly);

        // A deployment with an in-memory PubSubStore never reads the grain-storage queries —
        // demanding them would turn a correct Localhost-PubSub deployment into a startup refusal.
        Assert.DoesNotContain(membershipOnly,
            k => OrleansProvisioningGate.GrainStorageQueryKeys.Contains(k));

        Assert.All(OrleansProvisioningGate.MembershipQueryKeys, k => Assert.Contains(k, withStorage));
        Assert.All(OrleansProvisioningGate.GrainStorageQueryKeys, k => Assert.Contains(k, withStorage));

        // The two key sets are disjoint — a duplicate would mean one list drifted into the other.
        Assert.Equal(withStorage.Length, withStorage.Distinct().Count());
    }

    /// <summary>
    /// 🚨 The four grain-storage keys are exactly the ones <c>AdoNetGrainStorage.Init</c> loads
    /// with <c>.Single()</c>. That <c>.Single()</c> is why a missing row presents as
    /// <c>Sequence contains no elements</c> instead of anything actionable, and it is the whole
    /// reason this gate exists — so the set is pinned literally rather than being free to drift
    /// with a refactor.
    /// </summary>
    [Fact]
    public void TheGrainStorageKeys_AreTheOnesAdoNetGrainStorageLoadsWithSingle()
    {
        Assert.Equal(
            new[]
            {
                "WriteToStorageKey",
                "ReadFromStorageKey",
                "ClearStorageKey",
                "DeleteStorageKey",
            },
            OrleansProvisioningGate.GrainStorageQueryKeys);
    }
}
