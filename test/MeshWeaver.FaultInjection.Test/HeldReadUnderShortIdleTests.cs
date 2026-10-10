using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>Case 7 — a held read under a short idle window survives its owner's silo being killed</b>
/// (MeshWeaver#6048, the port of MeshWeaver.Plugins#2403's resumer case). The read is held on silo 0
/// while the idle sweep runs every 100 ms over a 500 ms window — the #6048 settings — so an entry the
/// sweep wrongly treated as idle would be released many times over before the kill, and with it the
/// heartbeat that is the only thing re-activating the owner on the survivor.
///
/// <para><b>Negative control:</b> with <c>Entry.IsIdleCandidate</c> / <c>Entry.TryMarkIdleEvicted</c>
/// in <c>MeshNodeStreamCache</c> ignoring the live-subscriber count, the owner is never re-activated
/// and the first assertion fails after 36 s with "emitted nothing at all" — exactly the #6048 symptom.
/// See Doc/Architecture/FaultInjectionHarness.</para>
/// </summary>
public abstract class AHeldReadUnderAShortIdleWindow(FaultInjectionCluster mesh)
{
    /// <summary>Whether the read is held through the hub's <c>GetMeshNodeStream</c> handle (the
    /// resumer's shape) rather than the cache's raw stream.</summary>
    protected abstract bool ThroughHandle { get; }

    [Fact(Timeout = 180_000)]
    public async Task SurvivesItsOwnerSiloBeingKilled_AndReActivatesTheOwnerOnTheSurvivor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var held = await HeldRead.Arrange(mesh, owner: 1, holder: 0, "idle-held", ct,
            throughHandle: ThroughHandle);

        // Several idle windows pass with the read held. The elapsed time IS the subject (the sweep
        // must have had every chance to release the entry); nothing is waited for to propagate.
        await Task.Delay(ShortIdleSiloConfigurator.Window * 6, ct);

        await mesh.Kill(1);
        await Observable.Interval(TimeSpan.FromMilliseconds(250)).StartWith(0L)
            .Select(_ => mesh.Hub(0).GetHostedHub(new Address(held.Path), HostedHubCreation.Never))
            .Where(h => h is not null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a read HELD under a short idle window keeps heart-beating its owner, so the owner "
                  + "re-activates on the survivor after its silo is killed (#6048)", ct);
        await held.Write("v2").Should().Within(TestTimeouts.Convergence)
            .Emit("the write after the move commits on the re-activated owner", ct);
        await held.Delivers("v2").Should().Within(TestTimeouts.Convergence)
            .Emit("the held read delivers the next write (#6048)", ct);
    }
}

/// <summary>Held through the node stream cache's shared read.</summary>
public class AHeldReadUnderAShortIdleWindowSurvivesItsOwnerSiloBeingKilledTest(
    AHeldReadUnderAShortIdleWindowSurvivesItsOwnerSiloBeingKilledTest.Cluster mesh)
    : AHeldReadUnderAShortIdleWindow(mesh), IClassFixture<AHeldReadUnderAShortIdleWindowSurvivesItsOwnerSiloBeingKilledTest.Cluster>
{
    /// <inheritdoc />
    protected override bool ThroughHandle => false;

    /// <summary>Its own cluster: the case kills a silo.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(ShortIdleSiloConfigurator);
    }
}

/// <summary>Held through <c>hub.GetMeshNodeStream(path)</c>, as the running-action resumer holds it.</summary>
public class AHandleHeldReadUnderAShortIdleWindowSurvivesItsOwnerSiloBeingKilledTest(
    AHandleHeldReadUnderAShortIdleWindowSurvivesItsOwnerSiloBeingKilledTest.Cluster mesh)
    : AHeldReadUnderAShortIdleWindow(mesh), IClassFixture<AHandleHeldReadUnderAShortIdleWindowSurvivesItsOwnerSiloBeingKilledTest.Cluster>
{
    /// <inheritdoc />
    protected override bool ThroughHandle => true;

    /// <summary>Its own cluster: the case kills a silo.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(ShortIdleSiloConfigurator);
    }
}

/// <summary>
/// The read-then-hold race (#6048): a process that reads a node ONCE — a listing, a precondition check —
/// and then holds it lands its hold at some point of the idle sweep's release of that first read's
/// entry. Sixteen nodes, each held after a different gap across the 500 ms window, then one kill: every
/// owner must re-activate on the survivor, so no gap may leave a hold without a heartbeat.
/// </summary>
public class AReadThenHoldAcrossTheIdleReleaseSurvivesItsOwnerSiloBeingKilledTest(
    AReadThenHoldAcrossTheIdleReleaseSurvivesItsOwnerSiloBeingKilledTest.Cluster mesh)
    : IClassFixture<AReadThenHoldAcrossTheIdleReleaseSurvivesItsOwnerSiloBeingKilledTest.Cluster>
{
    [Fact(Timeout = 300_000)]
    public async Task EveryGap_KeepsTheHoldHeartBeating_AndEveryOwnerReActivatesOnTheSurvivor()
    {
        var ct = TestContext.Current.CancellationToken;
        var gaps = Enumerable.Range(0, 16).Select(i => TimeSpan.FromMilliseconds(i * 50)).ToArray();
        var holds = ImmutableList<HeldRead>.Empty;
        try
        {
            foreach (var gap in gaps)
                holds = holds.Add(await HeldRead.Arrange(mesh, owner: 1, holder: 0, $"read-then-hold-{(int)gap.TotalMilliseconds}",
                    ct, throughHandle: true, priorReadGap: gap));

            await mesh.Kill(1);
            foreach (var (held, gap) in holds.Zip(gaps))
                await Observable.Interval(TimeSpan.FromMilliseconds(250)).StartWith(0L)
                    .Select(_ => mesh.Hub(0).GetHostedHub(new Address(held.Path), HostedHubCreation.Never))
                    .Where(h => h is not null)
                    .Should().Within(TestTimeouts.Convergence)
                    .Emit($"the read held {gap.TotalMilliseconds} ms after a one-shot read keeps heart-beating "
                          + "its owner, so the owner re-activates on the survivor (#6048)", ct);
        }
        finally
        {
            foreach (var held in holds)
                held.Dispose();
        }
    }

    /// <summary>Its own cluster: the case kills a silo.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(ShortIdleSiloConfigurator);
    }
}

/// <summary>The portal silo with the read-stream idle sweep shortened to the #6048 settings.</summary>
public class ShortIdleSiloConfigurator : FaultInjectionSiloConfigurator
{
    /// <summary>The idle window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureAdditional(MeshBuilder builder)
        => builder.ConfigureServices(services => services.AddSingleton(new MeshNodeStreamCacheOptions
        {
            ReadStreamIdleExpiration = Window,
            ReadStreamSweepInterval = TimeSpan.FromMilliseconds(100),
        }));
}
