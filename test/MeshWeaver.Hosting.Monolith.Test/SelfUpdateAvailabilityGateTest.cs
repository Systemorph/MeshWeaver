using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Data;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.GitSync;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The deployment gate (#1754) as the POLLER experiences it, against a real monolith mesh: a newer
/// tag that the release-availability check refuses must not be rolled, and the refusal must be
/// VISIBLE on <c>Admin/UpdatePolicy</c> rather than a silence.
///
/// <para>🚨 The second property is the one that matters most. A gate whose refusal is invisible
/// produces exactly the outage it was built to prevent — an environment that quietly stops updating
/// for weeks — so the hold is asserted as a positive, observable write, never as "nothing happened".
/// The clearing case is asserted the same way: the roll DOES land once the verdict flips, which is
/// what makes the refusal safe to make at all (nothing has to be un-stuck by hand).</para>
///
/// <para>Only the gate's verdict is injected here, at the documented
/// <see cref="ReleaseAvailabilityService.IsUpdatable"/> seam, plus the two long-standing external-IO
/// seams (ACR REST, k8s PATCH). The hub, the workspace, the policy-node seeding, the live stream and
/// every <c>stream.Update</c> are real — and the VERDICT itself is pinned separately, against a real
/// artifact store, by <c>ReleaseAvailabilityTest</c> / <c>PublishedBundleCatalogueTest</c>.</para>
/// </summary>
public class SelfUpdateAvailabilityGateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string CandidateTag = "9999.0.0-ci.1";

    /// <summary>
    /// 🚨 This host CONSUMES CI BAKES — it configures a published bundle root, exactly as a
    /// production portal does. That is load-bearing for every unwired-gate case below.
    ///
    /// <para>The gate's applicability is decided from configuration
    /// (<see cref="ReleaseAvailabilityService.NotApplicableReason"/>): a deployment with no bundle
    /// root already compiles its content at every boot, so a registered gate answers
    /// <c>NotEnforced</c> for it and its ABSENCE is therefore not an unanswered question. Only a
    /// deployment that actually consumes bakes has something an unwired gate has failed to
    /// verify — so a test host that configured nothing could only ever assert the trivial
    /// case.</para>
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // AddGitHubSyncTypes registers the BuildCompletion satellite — the record the workflow_run
        // webhook writes and the self-update watch reacts to. Types only, no credentials: the same
        // production registration rather than a duplicate declaration that could drift from it.
        => base.ConfigureMesh(builder).AddUpdatePolicyType().AddGitHubSyncTypes()
            .ConfigureServices(services => services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ShippedPrebuiltBundles.PublishedRootConfigKey] = "/data/prebuilt-bundles",
                }).Build()));

    /// <summary>Fake registry (the documented IO seam): one build newer than anything installed.</summary>
    private sealed class FakeAcrTagLister : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([CandidateTag]);
    }

    /// <summary>Fake k8s patcher (the documented IO seam) — records every applied tag.</summary>
    private sealed class RecordingUpdater : IDeploymentUpdater
    {
        private readonly ReplaySubject<string> patched = new();
        private ImmutableList<string> tags = ImmutableList<string>.Empty;

        public IObservable<string> Patched => patched;
        public ImmutableList<string> Tags => tags;
        public bool CanPatch => true;

        /// <summary>No stamp: these fakes exercise the roll decision, not the floor —
        /// the floor's own behaviour is pinned in SelfUpdateRollFloorTest.</summary>
        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) =>

            Task.FromResult<DateTimeOffset?>(null);


        public Task PatchToVersionAsync(string versionTag, CancellationToken ct)
        {
            ImmutableInterlocked.Update(ref tags, current => current.Add(versionTag));
            patched.OnNext(versionTag);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The gate seam: answers whatever verdict the test currently wants, so the poller's HANDLING of
    /// a verdict is pinned without also staging an Azure Files layout.
    /// </summary>
    private sealed class SteerableGate(IMessageHub hub, IConfiguration configuration)
        : ReleaseAvailabilityService(hub, configuration)
    {
        private UpdatabilityVerdict verdict = Held();

        public void Allow() => Volatile.Write(ref verdict, Allowed());

        public override IObservable<UpdatabilityVerdict> IsUpdatable(string? targetVersion) =>
            Observable.Return(Volatile.Read(ref verdict));

        private static UpdatabilityVerdict Held() => ReleaseAvailability.IsUpdatable(
            new ReleaseTarget(CandidateTag, "starget"),
            [new RequiredPackage("Store", "Store")],
            ReleaseArtifacts.Of([]));

        private static UpdatabilityVerdict Allowed() => ReleaseAvailability.IsUpdatable(
            new ReleaseTarget(CandidateTag, "starget"),
            [new RequiredPackage("Store", "Store")],
            ReleaseArtifacts.Of(["Store.zip"]));
    }

    /// <summary>
    /// Seeds <c>Admin/UpdatePolicy</c> before the assertions subscribe to it — the SAME idempotent
    /// call the poller makes at startup. Without it the test races the poller's own seeding and the
    /// point read answers <c>No node found</c>: reading a maybe-absent node by path is exactly what
    /// <c>EnsureExists</c> (a storm-safe query) exists to avoid.
    /// </summary>
    private Task SeedPolicyNode() =>
        UpdatePolicyNodeType
            .EnsureExists(
                Mesh,
                Mesh.ServiceProvider.GetService<AccessService>(),
                UpdatePolicyKind.Continuous)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask(TestContext.Current.CancellationToken);

    private static SelfUpdateOptions FastPoll() => new()
    {
        RetryInterval = TimeSpan.FromMilliseconds(500),
        // Coalescing is a production concern (one check for a burst of publications); a test that
        // drives one event must not wait out the real window.
        EventCoalesceWindow = TimeSpan.FromMilliseconds(50),
        DefaultPolicy = UpdatePolicyKind.Continuous,
    };

    [Fact(Timeout = 60000)]
    public async Task AHeldReleaseIsNotRolled_AndTheHoldIsWrittenWhereTheUpdatesTabReadsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var updater = new RecordingUpdater();
        var gate = new SteerableGate(Mesh, new ConfigurationBuilder().Build());
        await SeedPolicyNode();
        var service = new GatedSelfUpdateService(
            Mesh, new FakeAcrTagLister(), updater, FastPoll(),
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(), gate);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // POSITIVE signal for a refusal: the hold lands on the policy node. Waiting for "no
            // patch" alone would pass against a poller that had simply died.
            var held = await Mesh.GetWorkspace()
                .GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                .Select(node => UpdatePolicyNodeType.Parse(node, Mesh.JsonSerializerOptions))
                .Where(content => content.IsHeld(CandidateTag))
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(ct);

            held.HeldReason.Should().Contain("Store",
                "the refusal must name the package that blocks it — an unnamed hold is unactionable");
            held.HeldIndeterminate.Should().BeFalse(
                "this is a package that cannot survive the release, not a catalogue we could not read");
            held.HeldAt.Should().NotBeNull();

            updater.Tags.Should().BeEmpty("a held release must not be rolled");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task WhenTheMissingArtifactArrives_TheNextTickRolls_AndClearsTheHold()
    {
        var ct = TestContext.Current.CancellationToken;
        var updater = new RecordingUpdater();
        var gate = new SteerableGate(Mesh, new ConfigurationBuilder().Build());
        await SeedPolicyNode();
        var service = new GatedSelfUpdateService(
            Mesh, new FakeAcrTagLister(), updater, FastPoll(),
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(), gate);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Mesh.GetWorkspace()
                .GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                .Select(node => UpdatePolicyNodeType.Parse(node, Mesh.JsonSerializerOptions))
                .Where(content => content.IsHeld(CandidateTag))
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(ct);

            // The bake lands...
            gate.Allow();

            // ...and the PUBLICATION that carries it is the event that re-decides the hold. The check
            // is event-driven now, so nothing is re-evaluated on a timer: what makes refusing safe is
            // that the very act of the missing artifact becoming available is itself a build
            // completion, and this watch reacts to ANY repository's — platform or module.
            await SelfUpdateEventDriver.PublishBuildAsync(Mesh, "MeshWeaver.Plugins", runNumber: 1);

            var applied = await updater.Patched
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(ct);
            applied.Should().Be(CandidateTag);

            var cleared = await Mesh.GetWorkspace()
                .GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                .Select(node => UpdatePolicyNodeType.Parse(node, Mesh.JsonSerializerOptions))
                .Where(content => content.HeldTag is null)
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(ct);

            cleared.HeldReason.Should().BeNull(
                "a resolved hold must disappear from the admin tab, not linger as a stale scare");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 🚨 <b>An UNWIRED gate is a hold, not a pass.</b> The poller used to log at Information and
    /// roll — so the one host where nothing checked anything was also the one host that never
    /// refused. A gate that cannot run must never look like a gate that passed, and #1754's own
    /// rule is that "cannot determine" is not "clear to proceed".
    ///
    /// <para>As with every other hold, "no patch happened" is not the assertion — that would pass
    /// against a poller that had simply died. The positive signal is the hold WRITTEN where the
    /// Updates tab reads it, flagged INDETERMINATE: this is a wiring defect to fix, never a verdict
    /// about the release.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AnUnwiredGate_HoldsTheRoll_AndSaysItCouldNotLook()
    {
        var ct = TestContext.Current.CancellationToken;
        var updater = new RecordingUpdater();
        await SeedPolicyNode();
        var service = new GatedSelfUpdateService(
            Mesh, new FakeAcrTagLister(), updater, FastPoll(),
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(), gate: null);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var held = await Mesh.GetWorkspace()
                .GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                .Select(node => UpdatePolicyNodeType.Parse(node, Mesh.JsonSerializerOptions))
                .Where(content => content.IsHeld(CandidateTag))
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(ct);

            held.HeldIndeterminate.Should().BeTrue(
                "an unwired gate is the ABSENCE of a verdict — an availability failure to fix, "
                + "never an incompatibility to re-bake");
            held.HeldReason.Should().Contain("no release-availability gate is registered");
            held.HeldReason.Should().Contain("not clearance to proceed");

            updater.Tags.Should().BeEmpty("an unverified release must not be rolled");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 🚨 <b>"Cannot verify" and "verified as nothing to verify" are DIFFERENT states, and only the
    /// first may hold.</b> This is the case the first cut of the unwired-gate hold swept in, caught
    /// by <c>SelfUpdateRollFloorTest</c>: a deployment that consumes no CI bakes already compiles
    /// its content at every boot, so a REGISTERED gate answers <c>NotEnforced</c> for it — its
    /// absence is the same answer reached from configuration, not an unanswered question.
    ///
    /// <para>Holding here would have frozen an environment the gate was never going to protect,
    /// and — because the manual roll honours the same verdict — an install with no roll history
    /// could never have taken its first update at all. A fail-closed rule drawn one state too wide
    /// is its own outage.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AnUnwiredGate_OnADeploymentThatConsumesNoBakes_IsNotEnforced_AndRolls()
    {
        var ct = TestContext.Current.CancellationToken;
        var updater = new RecordingUpdater();
        await SeedPolicyNode();
        // This service reads a configuration with NO published bundle root — the one difference
        // from AnUnwiredGate_HoldsTheRoll_AndSaysItCouldNotLook, and the whole distinction.
        var service = new GatedSelfUpdateService(
            Mesh, new FakeAcrTagLister(), updater, FastPoll(),
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(),
            gate: null, configuration: new ConfigurationBuilder().Build());

        await service.StartAsync(CancellationToken.None);
        try
        {
            var applied = await updater.Patched
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(ct);
            applied.Should().Be(CandidateTag);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The interaction the two gates have with each other: a NEVER-ROLLED install, on a deployment
    /// that does consume bakes, with no gate registered.
    ///
    /// <para>It HOLDS — and the two gates are independent, which is the point. The pacing floor
    /// (#1778) asks "has this install rolled too recently?" and a never-rolled install passes it
    /// trivially; the availability gate asks "can the packages this environment deploys survive the
    /// target?" and an unwired gate has not answered that. Passing the first does not make the
    /// second verifiable. Pinned because the tempting reading — "no history, so nothing can be
    /// wrong, so let it through" — would reopen exactly the hole #1754 closed, on the instance
    /// least likely to be watched.</para>
    ///
    /// <para>It is not a brick: the hold is re-evaluated every tick and clears the moment the gate
    /// is registered, it is named on <c>Admin/UpdatePolicy</c> and logged at Error, and
    /// <see cref="SelfUpdateOptions.AllowUnverifiedRoll"/> is the deliberate one-line opt-out. No
    /// host in this repo can reach the state at all — <c>AddSelfUpdate</c> registers the gate
    /// unconditionally.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task NeverRolled_WithTheGateUnavailable_StillHolds()
    {
        var ct = TestContext.Current.CancellationToken;
        var updater = new RecordingUpdater();   // LastRolledAtAsync → null: never rolled.
        await SeedPolicyNode();
        var service = new GatedSelfUpdateService(
            Mesh, new FakeAcrTagLister(), updater,
            FastPoll() with { MinRollInterval = TimeSpan.FromHours(1) },
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(), gate: null);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var held = await Mesh.GetWorkspace()
                .GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                .Select(node => UpdatePolicyNodeType.Parse(node, Mesh.JsonSerializerOptions))
                .Where(content => content.IsHeld(CandidateTag))
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(ct);

            held.HeldIndeterminate.Should().BeTrue(
                "clearing the PACING floor says nothing about whether the release is AVAILABLE — "
                + "the two gates answer different questions");
            updater.Tags.Should().BeEmpty();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The escape hatch is DELIBERATE and CONFIGURED, in the shape this repo already uses for
    /// <c>PreWarm:AllowUnprovenBake</c>: an operator can still roll unverified, but only by setting
    /// the key — never by omission, and never silently.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AnUnwiredGate_RollsOnlyWhenTheOperatorOptedInDeliberately()
    {
        var ct = TestContext.Current.CancellationToken;
        var updater = new RecordingUpdater();
        await SeedPolicyNode();
        var service = new GatedSelfUpdateService(
            Mesh, new FakeAcrTagLister(), updater,
            FastPoll() with { AllowUnverifiedRoll = true },
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(), gate: null);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var applied = await updater.Patched
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(ct);
            applied.Should().Be(CandidateTag);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The poller with the gate supplied directly. The production poller resolves it from the mesh's
    /// service provider; injecting it here keeps the test from rebuilding the whole mesh just to
    /// register one singleton, without changing which code path decides.
    /// </summary>
    private sealed class GatedSelfUpdateService(
        IMessageHub hub,
        IAcrTagLister acr,
        IDeploymentUpdater updater,
        SelfUpdateOptions options,
        ILogger<SelfUpdateHostedService>? logger,
        ReleaseAvailabilityService? gate,
        IConfiguration? configuration = null)
        : SelfUpdateHostedService(hub, acr, updater, options, logger)
    {
        protected override ReleaseAvailabilityService? ResolveAvailabilityGate() => gate;

        /// <summary>Present a deployment that consumes no CI bakes when the test asks for one —
        /// the one key the unwired-gate distinction turns on.</summary>
        protected override IConfiguration? ResolveConfiguration() =>
            configuration ?? base.ResolveConfiguration();
    }
}
