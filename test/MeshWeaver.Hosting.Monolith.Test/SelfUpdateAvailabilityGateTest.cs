using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

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

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType();

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
        PollInterval = TimeSpan.FromMilliseconds(500),
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

            // The bake lands. Nothing is un-stuck by hand — the hold is re-evaluated every tick, and
            // that is precisely what makes refusing safe.
            gate.Allow();

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
        ReleaseAvailabilityService gate)
        : SelfUpdateHostedService(hub, acr, updater, options, logger)
    {
        protected override ReleaseAvailabilityService? ResolveAvailabilityGate() => gate;
    }
}
