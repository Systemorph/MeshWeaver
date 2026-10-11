#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The platform rolls independently of its packages</b> — maintainer, 2026-10-11:
/// <i>"again, we want to roll platform independently of packages"</i>.
///
/// <para>Measured on partnerre.meshweaver.cloud that day: <c>Admin/UpdatePolicy</c> read
/// <c>HOLDING 3.1.10502 — the framework identity of release 3.1.10502 is not resolvable — no content
/// bake has been published for it, so nothing can be shown adoptable; cannot determine availability,
/// which is not clearance to proceed</c> (<c>heldIndeterminate=true</c>), while every package update
/// on the same instance was held because it needed platform &gt;= 3.1.10493 — a deadlock between
/// platform and packages. memex.systemorph.com ran 3.1.10502 fine.</para>
///
/// <para>These tests drive the REAL poller against a real monolith mesh with the availability gate
/// answering exactly that verdict (and the strict-mode missing-bake one), and assert the rule from
/// both sides: the roll LANDS on the newest admitted tag, and the package answer is not lost — it is
/// on <c>Admin/UpdatePolicy</c> as an advisory (no hold), and on the check verdict.</para>
/// </summary>
public class PlatformRollsIndependentlyOfPackagesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string CandidateTag = "9999.0.0-ci.1";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType().AddGitHubSyncTypes();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    // ══════════════════════════════════════════════════════════════════════════
    //  The pure rule
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The partnerre verdict, through the shared predicate: an unresolvable framework
    /// identity makes every package Indeterminate and the package verdict NOT updatable.</summary>
    private static UpdatabilityVerdict UnresolvableIdentity() =>
        ReleaseAvailability.IsUpdatable(
            new ReleaseTarget(CandidateTag, null),
            [new RequiredPackage("Feedback", "Feedback"), new RequiredPackage("Crm", "Crm")],
            ReleaseArtifacts.Of([]));

    /// <summary>A missing content bake on an instance that opts into the strict mode — the one
    /// place a missing bake is still a package refusal.</summary>
    private static UpdatabilityVerdict MissingBakeUnderRequirePrebuilt() =>
        ReleaseAvailability.IsUpdatable(
            new ReleaseTarget(CandidateTag, "s20261011missingbake000000000000"),
            [new RequiredPackage("Feedback", "Feedback"), new RequiredPackage("Crm", "Crm")],
            ReleaseArtifacts.Of(["Crm"]),
            new ReleaseGatePolicy(RequirePrebuilt: true));

    [Fact]
    public void ThePackageVerdictStillRefuses_TheRollVerdictProceeds_WithTheReasonAsAnAdvisory()
    {
        var packages = UnresolvableIdentity();
        packages.IsUpdatable.Should().BeFalse("the predicate answers the PACKAGE question truthfully");
        packages.IsIndeterminate.Should().BeTrue();

        var roll = packages.ForPlatformRoll();

        roll.IsUpdatable.Should().BeTrue("a package answer never holds the platform roll");
        roll.HoldReason.Should().BeNull();
        roll.Blockers.Should().BeEmpty();
        roll.WaivedHold.Should().Be(packages.HoldReason);
        roll.Lagging.Select(p => p.Package).Order(StringComparer.Ordinal).Should().Equal(["Crm", "Feedback"]);
        roll.Advisories[0].Should().Contain("independently of packages")
            .And.Contain("could NOT be determined")
            .And.Contain("framework identity of release 9999.0.0-ci.1 is not resolvable");
        roll.IsIndeterminate.Should().BeTrue("the packages' own answer is kept, not rewritten");

        roll.ForPlatformRoll().Should().BeSameAs(roll, "the waiver is idempotent");
        var clear = ReleaseAvailability.IsUpdatable(
            new ReleaseTarget(CandidateTag, "sid"), [new RequiredPackage("Crm", "Crm")], ReleaseArtifacts.Of(["Crm"]));
        clear.ForPlatformRoll().Should().BeSameAs(clear, "an updatable verdict comes back unchanged");
    }

    [Fact]
    public void AMissingBakeUnderRequirePrebuilt_NamesThePackageThatDoesNotFollow()
    {
        var packages = MissingBakeUnderRequirePrebuilt();
        packages.IsUpdatable.Should().BeFalse();

        var roll = packages.ForPlatformRoll();

        roll.IsUpdatable.Should().BeTrue();
        roll.Lagging.Should().ContainSingle().Which.Package.Should().Be("Feedback");
        roll.Advisories[0].Should().Contain("1 package(s) are not available").And.Contain("Feedback");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The poller
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 THE ACCEPTANCE CRITERION — partnerre's exact verdict. Before 2026-10-11 the poller wrote
    /// <c>HeldTag = 3.1.10502, heldIndeterminate = true</c> and patched nothing; now the roll lands,
    /// no hold is recorded, and the reason is on <c>Admin/UpdatePolicy</c> as the advisory the
    /// Updates and About tabs render.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AnIndeterminateVerdict_RollsTheNewestTag_WithTheReasonAsAnAdvisory()
    {
        await Seed(TestContext.Current.CancellationToken, staleHold: true);
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, UnresolvableIdentity());

        updater.Tags.Should().Contain(CandidateTag,
            "the platform rolls independently of packages — an indeterminate package verdict is not a hold");
        content.IsHeld(CandidateTag).Should().BeFalse("a package answer leaves no hold on the platform");
        content.HeldReason.Should().BeNull("the stale hold the old poller wrote is cleared on the roll");
        content.HeldIndeterminate.Should().BeFalse();
        content.HasAdvisoriesFor(CandidateTag).Should().BeTrue(
            "the package answer is not lost — the Updates/About tabs still list what does not follow");
        content.Advisories.Should().Contain("independently of packages")
            .And.Contain("framework identity of release 9999.0.0-ci.1 is not resolvable");
        content.LastCheckVerdict.Should().Contain("PACKAGE ADVISORY",
            "the roll over a package answer is said on the check verdict, the durable half");
        content.LastCheckVerdict.Should().NotContain("HOLDING");

        PlatformUpdateStatus.Derive(content with { LatestAvailableTag = CandidateTag }, "1.0.0")
            .Availability.Should().NotBe(PlatformUpdateAvailability.UpdateHeld,
                "the About tab must no longer say 'Update held — a package … is not available'");
    }

    /// <summary>A missing content bake under <c>Modules:RequirePrebuilt</c> — the last place a
    /// missing bake still refuses — rolls the same way, naming the package.</summary>
    [Fact(Timeout = 240_000)]
    public async Task AMissingBakeUnderRequirePrebuilt_RollsTheNewestTag_NamingThePackage()
    {
        await Seed(TestContext.Current.CancellationToken);
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, MissingBakeUnderRequirePrebuilt());

        updater.Tags.Should().Contain(CandidateTag);
        content.IsHeld(CandidateTag).Should().BeFalse();
        content.Advisories.Should().Contain("Feedback");
        content.LastCheckVerdict.Should().Contain("PACKAGE ADVISORY").And.Contain("Feedback");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Harness (the ComboGateRollTest shape)
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<UpdatePolicyContent> RunOneCheck(RecordingUpdater updater, UpdatabilityVerdict verdict)
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new GatedSelfUpdateService(
            Mesh, new FakeAcrTagLister(), updater, FastPoll(),
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(),
            new FixedVerdict(Mesh, new ConfigurationBuilder().Build(), verdict),
            new ComboVerificationGate(Mesh));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.Evaluations.FirstAsync().Timeout(Budget).Await(ct);
            return await WaitForContent(c => c.LastCheckVerdict is not null);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static SelfUpdateOptions FastPoll() => new()
    {
        RetryInterval = TimeSpan.FromMilliseconds(500),
        EventCoalesceWindow = TimeSpan.FromMilliseconds(50),
        DefaultPolicy = UpdatePolicyKind.Continuous,
        DefaultPattern = "*-ci*",
    };

    private sealed class FakeAcrTagLister : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([CandidateTag]);
    }

    private sealed class RecordingUpdater : IDeploymentUpdater
    {
        private ImmutableList<string> tags = ImmutableList<string>.Empty;

        public ImmutableList<string> Tags => tags;
        public bool CanPatch => true;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct)
        {
            ImmutableInterlocked.Update(ref tags, current => current.Add(versionTag));
            return Task.CompletedTask;
        }
    }

    /// <summary>The availability gate pinned to one verdict, for every question it is asked — the
    /// candidate selection and the gate both.</summary>
    private sealed class FixedVerdict(IMessageHub hub, IConfiguration configuration, UpdatabilityVerdict verdict)
        : ReleaseAvailabilityService(hub, configuration)
    {
        public override IObservable<UpdatabilityVerdict> IsUpdatable(string? targetVersion) =>
            Observable.Return(verdict);

        public override IObservable<RollSelectionOutcome> SelectRollTarget(
            string? currentVersion,
            IReadOnlyList<string>? candidatesNewestFirst = null,
            Func<string, string?>? condemned = null,
            UpdatePolicyKind policy = UpdatePolicyKind.Continuous,
            bool requireCiGreen = true) =>
            RollSelection.Select(
                new RollSelectionInputs(
                    "test", currentVersion, [.. candidatesNewestFirst ?? []],
                    PluginInventory.Of(verdict.Packages.Select(p => new RequiredPackage(p.Package, p.Package)), "test")),
                _ => Observable.Return(verdict));
    }

    private sealed class GatedSelfUpdateService(
        IMessageHub hub,
        IAcrTagLister acr,
        IDeploymentUpdater updater,
        SelfUpdateOptions options,
        ILogger<SelfUpdateHostedService>? logger,
        ReleaseAvailabilityService? gate,
        ComboVerificationGate? combo)
        : SelfUpdateHostedService(hub, acr, updater, options, logger)
    {
        public IObservable<Unit> Evaluations => ChecksReported;

        protected override ReleaseAvailabilityService? ResolveAvailabilityGate() => gate;

        protected override ComboVerificationGate? ResolveComboGate() => combo;
    }

    /// <summary>Seeds the policy node — optionally with the hold the pre-2026-10-11 poller wrote on
    /// partnerre, so the test also proves the roll CLEARS it.</summary>
    private Task Seed(CancellationToken cancellationToken, bool staleHold = false)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var content = new UpdatePolicyContent { Policy = UpdatePolicyKind.Continuous, Pattern = "*-ci*" };
        if (staleHold)
            content = content with
            {
                HeldTag = CandidateTag,
                HeldReason = UnresolvableIdentity().HoldReason,
                HeldIndeterminate = true,
                HeldAt = DateTimeOffset.UtcNow.AddHours(-1),
            };
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = content,
        };
        return Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return (IDisposable)meshService.CreateNode(node).Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(cancellationToken);
    }

    private Task<UpdatePolicyContent> WaitForContent(Func<UpdatePolicyContent, bool> predicate) =>
        Observable.Create<UpdatePolicyContent>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return Mesh.GetWorkspace()
                        .GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                        .Where(node => node is not null)
                        .Select(node => UpdatePolicyNodeType.Parse(node, Mesh.JsonSerializerOptions))
                        .Subscribe(observer);
            })
            .Where(predicate)
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
}
