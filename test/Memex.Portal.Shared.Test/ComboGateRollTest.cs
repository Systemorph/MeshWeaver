#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
/// 🚨 <b>The combo gate (#2274) as the POLLER experiences it, against a real monolith mesh.</b>
///
/// <para>The gate was fully built, documented and tested — and called by NOTHING. Its verdict type,
/// its recorder, its reader and the settings tab that renders it all existed while
/// <c>comboVerifications</c> stayed <c>[]</c> on every instance and the poller rolled regardless.
/// The acceptance criterion for closing that gap is not "the fold is correct" (that is
/// <see cref="ComboClearanceTest"/>) — it is that a RED verdict actually STOPS a roll, and that the
/// verifier is genuinely RUN and its verdict RECORDED where an admin looks.</para>
///
/// <para>So the assertions here are positive and observable in both directions: a refusal is
/// asserted as a HOLD written to <c>Admin/UpdatePolicy</c> plus an unpatched updater (never as "no
/// patch happened", which passes against a poller that simply died), and every non-refusing state is
/// asserted as a roll that DID land. Delete the wiring in
/// <c>SelfUpdateHostedService.ComboThenApply</c> and
/// <see cref="ARedVerdict_BlocksTheRoll_AndNamesTheModuleThatWouldBreak"/> plus
/// <see cref="TheVerifierIsRun_ItsRedVerdictIsRecorded_AndTheRollIsRefused"/> both go red.</para>
///
/// <para>Only the documented seams are injected: the availability gate (so the OTHER gate never
/// decides anything here), the ACR list and the k8s PATCH. The hub, the workspace, the policy node,
/// the live stream, every <c>stream.Update</c>, the combo assembler and
/// <see cref="InstanceComboVerifier"/> itself are real.</para>
/// </summary>
public class ComboGateRollTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string CandidateTag = "9999.0.0-ci.1";

    /// <summary>🚨 <see cref="TestTimeouts.Convergence"/>, never a literal: a hand-written 30 s is
    /// both a guess about machine speed AND the framework's own write bound, so a test that waits
    /// it gives up one second before the mesh can explain itself (#2819).</summary>
    private static TimeSpan Budget => TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // AddGitHubSyncTypes registers the BuildCompletion satellite the self-update watch reacts
        // to — types only, the same production registration rather than a duplicate declaration.
        => base.ConfigureMesh(builder).AddUpdatePolicyType().AddGitHubSyncTypes();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    // ══════════════════════════════════════════════════════════════════════════
    //  Red — the refusal, and the whole point of the issue
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 <b>The acceptance criterion.</b> A candidate whose recorded verdict says a module this
    /// instance RUNS fails against it must not be rolled — and the refusal must be visible where an
    /// admin looks, naming the module. This is the exact state memex.systemorph.com was in: rolling
    /// forward aborted the host with a <c>MissingMethodException</c>, and nothing asked the question
    /// that would have said so.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ARedVerdict_BlocksTheRoll_AndNamesTheModuleThatWouldBreak()
    {
        await Seed();
        await Record(Red());
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, ComboGate());

        content.IsHeld(CandidateTag).Should().BeTrue(
            "a refusal that leaves no trace is the silent freeze this gate must never become");
        content.HeldReason.Should().Contain("Widget",
            "an unnamed refusal is unactionable — the operator has to know WHICH module breaks");
        content.HeldReason.Should().Contain("AddTracking");
        content.HeldIndeterminate.Should().BeFalse(
            "the gate LOOKED and found an incompatibility — that is a candidate to re-verify, not "
            + "an availability incident to fix");
        content.LastCheckVerdict.Should().Contain("BLOCKED by the combo gate");

        updater.Tags.Should().BeEmpty(
            "an image that cannot serve this instance's modules must not be rolled");
    }

    /// <summary>
    /// 🚨 <b>The refusal is a hold, not a freeze.</b> It is re-decided from scratch on every check,
    /// so a candidate that has since been re-verified clears with nothing to un-stick by hand —
    /// which is the property that makes refusing safe to do at all. An instance that quietly stops
    /// updating for weeks is its own outage, worse than the roll it prevented.
    ///
    /// <para>The stale hold is SEEDED rather than produced by an earlier check, so what is under
    /// test is the clearing itself and not the sequencing of two pollers.</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AReVerifiedCandidate_ClearsAStaleHold_AndRolls()
    {
        await Seed(held: true);
        await Record(Green());
        var updater = new RecordingUpdater();

        var cleared = await RunOneCheck(updater, ComboGate(), c => !c.IsHeld(CandidateTag));

        updater.Tags.Should().Contain(CandidateTag);
        cleared.HeldReason.Should().BeNull(
            "a resolved hold must disappear from the admin tab, not linger as a stale scare");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Green — and the two states that are NEITHER
    // ══════════════════════════════════════════════════════════════════════════

    [Fact(Timeout = 240_000)]
    public async Task AGreenVerdict_Clears_AndTheRollLands()
    {
        await Seed();
        await Record(Green());
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, ComboGate());

        updater.Tags.Should().Contain(CandidateTag, "a verified candidate must not be held");
        content.IsHeld(CandidateTag).Should().BeFalse();
        content.LastCheckVerdict.Should().NotContain("UNVERIFIED",
            "a Green IS the clearance — qualifying it would make the qualification meaningless");
    }

    /// <summary>
    /// 🚨 <b>NotVerifiable is NEITHER.</b> It does not clear — treating "we could not find out" as
    /// "all clear" is the outage this gate exists to prevent — and it does not refuse, because
    /// refusing the first time evidence is missing would freeze every instance in the fleet. The
    /// roll rests on the other gates, and the state is DURABLE on the policy node so an operator can
    /// see that an unverified roll was taken.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ANotVerifiableVerdict_NeitherClearsNorRefuses_AndTheUnverifiedRollIsRecorded()
    {
        await Seed();
        await Record(NotVerifiable());
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, ComboGate());

        updater.Tags.Should().Contain(CandidateTag,
            "refusing on 'we could not find out' would brick self-update on every install whose "
            + "producer cannot run");
        content.IsHeld(CandidateTag).Should().BeFalse("nothing refused this build");
        content.LastCheckVerdict.Should().Contain("UNVERIFIED",
            "an unverified roll that leaves no durable trace is indistinguishable from a verified "
            + "one — and a log line depends on a level a deployment may never have set");
        content.LastCheckVerdict.Should().Contain("could NOT answer");
        content.LastCheckVerdict.Should().Contain("docker is not available",
            "the caveats naming WHY it could not answer are what make the state actionable");
    }

    /// <summary>
    /// The state every install in the fleet is in today: no verdict at all. Also neither — and its
    /// recorded sentence is DIFFERENT from NotVerifiable's, because "nothing has asked" and "we
    /// asked and could not find out" are different incidents with different fixes.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task NoVerdictAtAll_DoesNotClearAndDoesNotRefuse_AndSaysWhichItIs()
    {
        await Seed();
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, ComboGate());

        updater.Tags.Should().Contain(CandidateTag);
        content.IsHeld(CandidateTag).Should().BeFalse();
        content.LastCheckVerdict.Should().Contain("UNVERIFIED");
        content.LastCheckVerdict.Should().Contain("no combo verification has been recorded");
        content.LastCheckVerdict.Should().Contain("mw-combo-verify",
            "an absence nobody can act on is how a gate stays unwired for months");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The wiring pin: InstanceComboVerifier is actually RUN
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 <b>The defect this issue is about was that nothing CALLED the verifier.</b> This drives the
    /// real pipeline on a host that can produce: this instance's combo →
    /// <see cref="InstanceComboAssembler"/> materialises the module →
    /// <see cref="InstanceComboVerifier"/> runs the gate inside the candidate and folds the evidence
    /// → <see cref="UpdatePolicyNodeType.RecordVerification"/> lands the verdict where the Updates
    /// tab reads it → the poller refuses the roll.
    ///
    /// <para>Only the two things a portal pod genuinely cannot have are faked — docker and the
    /// module repository (<see cref="IComboGateRunner"/>). The assembler, the verifier, the fold, the
    /// node write and the roll decision are all real, so removing ANY link of that chain fails this
    /// test: no recorded verdict, or a roll that lands.</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task TheVerifierIsRun_ItsRedVerdictIsRecorded_AndTheRollIsRefused()
    {
        await Seed();
        var workRoot = Path.Combine(
            Path.GetTempPath(), $"combo-gate-test-{Guid.NewGuid():N}"[..38]);
        var updater = new RecordingUpdater();
        var runner = new FakeGateRunner(workRoot, ModuleFailsToInstall());

        try
        {
            var content = await RunOneCheck(
                updater,
                new ProducingComboGate(Mesh, runner, OneModuleCombo()),
                c => c.VerificationFor(CandidateTag) is not null);

            // 1. The verifier RAN — a verdict exists that nothing but the fold could have produced.
            var verdict = content.VerificationFor(CandidateTag);
            verdict.Should().NotBeNull(
                "the whole defect was that InstanceComboVerifier had no caller — a recorded verdict "
                + "is the only proof it now has one");
            verdict!.Verdict.Should().Be(ComboVerdictKind.Red);
            verdict.VerifiedPlatform.Should().Be(DockerPlatform,
                "a verdict is about ONE architecture and must say which");
            var module = verdict.Modules.Single();
            module.ModuleId.Should().Be("Widget");
            module.Outcome.Should().Be(ModuleVerificationOutcome.Failed);
            module.ResolvedCommit.Should().Be(Sha,
                "the verdict names its exact input — these files, at this commit");
            module.Failures.Should().ContainSingle().Which.Should().Contain("AddTracking");

            // 2. And that verdict REFUSED the roll.
            updater.Tags.Should().BeEmpty();
            content.IsHeld(CandidateTag).Should().BeTrue();
            content.HeldReason.Should().Contain("Widget");
        }
        finally
        {
            if (Directory.Exists(workRoot))
                Directory.Delete(workRoot, recursive: true);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Harness
    // ══════════════════════════════════════════════════════════════════════════

    private const string DockerPlatform = "linux/amd64";
    private static readonly string Sha = new('a', 40);

    /// <summary>The gate the poller consults when nothing on this host can produce — the portal-pod
    /// shape, and the one every instance in the fleet runs today.</summary>
    private ComboVerificationGate ComboGate() => new(Mesh);

    /// <summary>
    /// Starts the poller, waits for ONE check to have been evaluated AND reported, then reads the
    /// policy content the check produced.
    ///
    /// <para>🚨 Waits for the service to have EVALUATED, never for a state to appear inside a bound:
    /// this service is event-driven, so "the state is not there yet" and "the first check has not
    /// run yet" are indistinguishable from outside, and on a loaded shard the second is what
    /// actually happens — reported as if it were a wrong verdict.</para>
    /// </summary>
    private async Task<UpdatePolicyContent> RunOneCheck(
        RecordingUpdater updater,
        ComboVerificationGate combo,
        Func<UpdatePolicyContent, bool>? settled = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new GatedSelfUpdateService(
            Mesh, new FakeAcrTagLister(), updater, FastPoll(),
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(),
            new AlwaysAvailable(Mesh, new ConfigurationBuilder().Build()), combo);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.Evaluations.FirstAsync().Timeout(Budget).Await(ct);
            // The check's own verdict stamp is the last write of a check, so a content carrying it
            // carries every earlier write of that check too.
            return await WaitForContent(
                settled ?? (c => c.LastCheckVerdict is not null));
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
    };

    /// <summary>Fake registry (the documented IO seam): one build newer than anything installed.</summary>
    private sealed class FakeAcrTagLister : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([CandidateTag]);
    }

    /// <summary>Fake k8s patcher (the documented IO seam) — records every applied tag.</summary>
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

    /// <summary>
    /// The availability gate, pinned to "nothing to enforce here". The two gates are INDEPENDENT and
    /// this suite is about the combo one — leaving the availability gate free to decide would make
    /// every assertion here ambiguous about which gate produced the outcome.
    /// </summary>
    private sealed class AlwaysAvailable(IMessageHub hub, IConfiguration configuration)
        : ReleaseAvailabilityService(hub, configuration)
    {
        public override IObservable<UpdatabilityVerdict> IsUpdatable(string? targetVersion) =>
            Observable.Return(UpdatabilityVerdict.NotEnforced(
                "this test host consumes no CI bakes"));
    }

    /// <summary>A host that CAN produce a verdict: docker and the module repository, faked — the two
    /// things a portal pod genuinely does not have. Everything between them is real.</summary>
    private sealed class FakeGateRunner(string workRoot, GateRunReport report) : IComboGateRunner
    {
        public string WorkRoot => workRoot;

        public ComboAssemblyOptions Options { get; } = new();

        public IObservable<CandidateGateRun> Run(string imageRef, string root) =>
            Observable.Return(new CandidateGateRun
            {
                ExitCode = report.Packages.All(p => p.Success) ? 0 : 1,
                ImageDigest = "sha256:4a63eda",
                Platform = DockerPlatform,
                Report = report,
                LogTail = "",
            });

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string gitRef, string? subdirectory, string accessToken) =>
            Observable.Return(new RepoSnapshot(Sha,
            [
                // index.json is what makes the folder DISCOVERABLE to the gate — without it the
                // verifier refuses to run the gate at all and folds to NotVerifiable.
                new RepoFile("index.json", "{\"id\":\"Widget\",\"name\":\"Widget\"}"),
                new RepoFile("Thing.json", "{\"nodeType\":\"NodeType\"}"),
            ]));
    }

    /// <summary>The gate on such a host. Only the two seams are overridden; the assembler, the
    /// verifier, the fold and the verdict write are the production ones.</summary>
    private sealed class ProducingComboGate(
        IMessageHub hub, IComboGateRunner runner, InstanceCombo combo)
        : ComboVerificationGate(hub)
    {
        protected override IComboGateRunner? ResolveRunner() => runner;

        protected override IObservable<InstanceCombo>? ReadCombo() => Observable.Return(combo);
    }

    /// <summary>One module, pinned to an EXACT commit — the production majority shape, and the only
    /// one the assembler materialises without an explicit allow-moving opt-in.</summary>
    private static InstanceCombo OneModuleCombo() => new()
    {
        ReadAt = DateTimeOffset.UtcNow,
        Modules =
        [
            new ModuleCoordinate
            {
                ModuleId = "Widget",
                Name = "Widget",
                GitSync = new GitSyncCoordinate
                {
                    ConfigPath = "Widget/_GitSync",
                    RepositoryUrl = "https://github.com/acme/widgets",
                    Branch = "main",
                    LastSyncCommitSha = Sha,
                    LastSyncedAt = DateTimeOffset.UtcNow,
                },
            },
        ],
    };

    /// <summary>The gate report of a module that cannot bind against the candidate — the shape the
    /// memex.systemorph.com trap actually took.</summary>
    private static GateRunReport ModuleFailsToInstall() => new()
    {
        Packages =
        [
            new GateRunPackage
            {
                Id = "Widget",
                NodeCount = 1,
                InstallError =
                    "MissingMethodException: no overload for 'AddTracking' taking 3 arguments",
            },
        ],
    };

    /// <summary>The poller with both gates supplied directly. Production resolves them from the
    /// mesh's service provider; injecting keeps the test from rebuilding the mesh to register two
    /// singletons, without changing which code path decides.</summary>
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
        /// <summary>Surfaces the base class's per-check completion. The base member is
        /// <c>protected internal</c> and this assembly is not in its friend list, so a derived-class
        /// forward is the local way to reach it.</summary>
        public IObservable<Unit> Evaluations => ChecksReported;

        protected override ReleaseAvailabilityService? ResolveAvailabilityGate() => gate;

        protected override ComboVerificationGate? ResolveComboGate() => combo;
    }

    // ── verdicts ──

    private static ComboVerification Base() => new()
    {
        CandidateTag = CandidateTag,
        ImageRef = $"meshweaver.azurecr.io/memex-portal-ai:{CandidateTag}",
        ImageDigest = "sha256:4a63eda",
        VerifiedPlatform = DockerPlatform,
        VerifiedAt = DateTimeOffset.UtcNow,
    };

    private static ComboVerification Red() => Base() with
    {
        Verdict = ComboVerdictKind.Red,
        Modules =
        [
            new ModuleVerification
            {
                ModuleId = "Widget",
                Outcome = ModuleVerificationOutcome.Failed,
                ResolvedCommit = Sha,
                Failures = ["Widget/Thing: compile failed — no overload for 'AddTracking'"],
            },
        ],
    };

    private static ComboVerification Green() => Base() with
    {
        VerifiedAt = DateTimeOffset.UtcNow.AddMinutes(1),
        Verdict = ComboVerdictKind.Green,
        Modules =
        [
            new ModuleVerification
            {
                ModuleId = "Widget",
                Outcome = ModuleVerificationOutcome.Passed,
                ResolvedCommit = Sha,
            },
        ],
    };

    private static ComboVerification NotVerifiable() => Base() with
    {
        Verdict = ComboVerdictKind.NotVerifiable,
        Caveats = ["the gate could not run: docker is not available on this host"],
    };

    // ── mesh helpers (the ComboVerdictRecordingTest shapes) ──

    private Task Seed(bool held = false)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var content = new UpdatePolicyContent { Policy = UpdatePolicyKind.Continuous };
        if (held)
            content = content with
            {
                HeldTag = CandidateTag,
                HeldReason = "'Widget' does not compile against it",
                HeldAt = DateTimeOffset.UtcNow.AddHours(-1),
            };
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = content,
        };
        // System scope opened/closed SYNCHRONOUSLY around the subscribe — impersonation is an
        // AsyncLocal, so Observable.Using would restore it on the wrong thread.
        return Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return (IDisposable)meshService.CreateNode(node).Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
    }

    private Task Record(ComboVerification verdict) =>
        UpdatePolicyNodeType.RecordVerification(Mesh, verdict)
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

    /// <summary>The first reconciled content matching <paramref name="predicate"/> — the
    /// wait-on-the-condition read, never a bare first emission (which can predate the write).</summary>
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
