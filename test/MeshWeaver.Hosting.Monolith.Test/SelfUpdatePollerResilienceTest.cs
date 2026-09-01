using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.GitSync;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Resilience tests for the self-update poller against a REAL monolith mesh. Pins the 2026-07-23
/// memex-cloud prod defect: the policy read (the hub-cache <c>SubscribeRequest</c> to
/// <c>Admin/UpdatePolicy</c>) faulted with a <see cref="TimeoutException"/>, the error OnError'd
/// through <c>.Switch()</c> into the terminal Subscribe, and the poller was dead for the life of the
/// pod — the pod stopped self-updating exactly when the update was what would have recovered it.
/// Faults are injected at the exact seam they surfaced through in prod
/// (<see cref="SelfUpdateHostedService.ReadPolicyStream"/>); everything else — hub, workspace,
/// <c>EnsureExists</c> seeding, the live node stream, <c>stream.Update</c> processing — is real.
/// The only fakes are the two documented external-IO seams (ACR REST, k8s PATCH).
/// </summary>
public class SelfUpdatePollerResilienceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // AddGitHubSyncTypes registers the BuildCompletion satellite the self-update watch reacts
        // to. Types only, no credentials — the production registration rather than a copy.
        => base.ConfigureMesh(builder).AddUpdatePolicyType().AddGitHubSyncTypes();

    /// <summary>Fake registry (the documented injectable IO seam): a ci build newer than anything
    /// installed plus an even "older" clean release — Continuous picks the ci tag, Stable the clean
    /// one, so each policy produces a DISTINCT positive signal (no negative-assertion waiting).</summary>
    private sealed class FakeAcrTagLister : IAcrTagLister
    {
        public const string CiTag = "9999.0.0-ci.1";
        public const string StableTag = "8888.0.0";

        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([CiTag, StableTag]);
    }

    /// <summary>Fake k8s patcher (the documented injectable IO seam) — records every applied tag,
    /// in order.</summary>
    private sealed class RecordingUpdater : IDeploymentUpdater
    {
        private readonly ReplaySubject<string> _patched = new();
        private ImmutableList<string> _tags = ImmutableList<string>.Empty;

        public IObservable<string> Patched => _patched;
        public ImmutableList<string> Tags => _tags;
        public bool CanPatch => true;

        /// <summary>No stamp: these fakes exercise the roll decision, not the floor —
        /// the floor's own behaviour is pinned in SelfUpdateRollFloorTest.</summary>
        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) =>

            Task.FromResult<DateTimeOffset?>(null);


        public Task PatchToVersionAsync(string versionTag, CancellationToken ct)
        {
            ImmutableInterlocked.Update(ref _tags, tags => tags.Add(versionTag));
            _patched.OnNext(versionTag);
            return Task.CompletedTask;
        }
    }

    /// <summary>Injects the prod fault shape at startup: the FIRST subscription of the live policy
    /// read throws the hub-cache SubscribeRequest <see cref="TimeoutException"/>; every later
    /// (re)subscription runs the REAL stream untouched.</summary>
    private sealed class FaultingFirstReadService(
        IMessageHub hub,
        IAcrTagLister acr,
        IDeploymentUpdater updater,
        SelfUpdateOptions options,
        ILogger<SelfUpdateHostedService>? logger)
        : SelfUpdateHostedService(hub, acr, updater, options, logger)
    {
        private int _subscriptions;

        public int ReadSubscriptions => Volatile.Read(ref _subscriptions);

        protected override IObservable<UpdatePolicyContent> ReadPolicyStream() =>
            Interlocked.Increment(ref _subscriptions) == 1
                ? Observable.Throw<UpdatePolicyContent>(new TimeoutException(
                    "No response received in hub cache/test within 00:01:00 for request SubscribeRequest "
                    + $"→ target {UpdatePolicyNodeType.NodePath}"))
                : base.ReadPolicyStream();
    }

    /// <summary>Lets the test fault the live policy read MID-LIFE (after it has emitted): each
    /// (re)subscription gets the real stream merged with a fresh fault channel; erroring the current
    /// channel errors the whole read, exactly like a live hub-cache subscription dropping.</summary>
    private sealed class MidLifeFaultService(
        IMessageHub hub,
        IAcrTagLister acr,
        IDeploymentUpdater updater,
        SelfUpdateOptions options,
        ILogger<SelfUpdateHostedService>? logger)
        : SelfUpdateHostedService(hub, acr, updater, options, logger)
    {
        private Subject<UpdatePolicyContent>? _current;

        public void InjectFault(Exception fault) => Volatile.Read(ref _current)?.OnError(fault);

        protected override IObservable<UpdatePolicyContent> ReadPolicyStream()
        {
            var channel = new Subject<UpdatePolicyContent>();
            Volatile.Write(ref _current, channel);
            return base.ReadPolicyStream().Merge(channel);
        }
    }

    /// <summary>
    /// Injects the #1020 prod fault shape: the availability BOOKKEEPING write to
    /// <c>Admin/UpdatePolicy</c> times out. On one production portal that node's hub was unreachable (its
    /// <c>SubscribeRequest</c> never answered — the silo's routing was wedged), so every tick died
    /// here 30 s in. Everything else — the registry check, the version pick, the k8s patch — was
    /// healthy, which is why the install looked fine and drifted 37 h.
    /// </summary>
    private sealed class FaultingRecordService(
        IMessageHub hub,
        IAcrTagLister acr,
        IDeploymentUpdater updater,
        SelfUpdateOptions options,
        ILogger<SelfUpdateHostedService>? logger)
        : SelfUpdateHostedService(hub, acr, updater, options, logger)
    {
        private int _attempts;

        public int RecordAttempts => Volatile.Read(ref _attempts);

        protected override IObservable<Unit> RecordAvailable(string tag)
        {
            Interlocked.Increment(ref _attempts);
            return Observable.Throw<Unit>(new TimeoutException(
                $"Update aborted: no initial state arrived for '{UpdatePolicyNodeType.NodePath}' within 30s."));
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Failed_availability_write_never_blocks_the_roll_forward()
    {
        var ct = TestContext.Current.CancellationToken;
        var acr = new FakeAcrTagLister();
        var updater = new RecordingUpdater();
        var options = new SelfUpdateOptions
        {
            RetryInterval = TimeSpan.FromMilliseconds(500),
            DefaultPolicy = UpdatePolicyKind.Continuous,
        };
        var service = new FaultingRecordService(
            Mesh, acr, updater, options,
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>());

        await service.StartAsync(CancellationToken.None);
        try
        {
            // The bookkeeping write fails on EVERY tick — exactly the production shape. Pre-fix the patch
            // was chained after it with .SelectMany, so the tick errored into the poller's warning
            // sink and NOTHING was ever applied: this await timed out while the registry check kept
            // succeeding. The roll-forward must happen regardless.
            var applied = await updater.Patched
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(ct);
            applied.Should().Be(FakeAcrTagLister.CiTag);

            // …and it really went THROUGH the failing write, not around it.
            service.RecordAttempts.Should().BeGreaterThan(0);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Poller_survives_faulted_first_read_and_processes_a_policy_change()
    {
        var ct = TestContext.Current.CancellationToken;
        var acr = new FakeAcrTagLister();
        var updater = new RecordingUpdater();
        var options = new SelfUpdateOptions
        {
            // A first-read fault is the ESTABLISHING case, so this is the interval that paces the
            // recovery — short here so the test observes it promptly. RetryInterval is deliberately
            // left at its production default: it must not be what gets this install moving again.
            PolicyEstablishRetryInterval = TimeSpan.FromMilliseconds(500),
            DefaultPolicy = UpdatePolicyKind.Continuous,
        };
        var service = new FaultingFirstReadService(
            Mesh, acr, updater, options,
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>());

        await service.StartAsync(CancellationToken.None);
        try
        {
            // 1. The first live read FAULTS (TimeoutException — the prod shape) right after the seed.
            //    Nothing can proceed on a guessed policy (#2731/#2797 removed the synthetic default
            //    emission), so EVERY trigger waits on the re-read: the roll below is proof the
            //    faulted read came back on its own. Pre-fix the fault terminated the whole
            //    subscription permanently; pre-#PolicyEstablishRetryInterval it came back only after
            //    the 6 h RetryInterval, which is the same outage with a longer name.
            var firstTag = await updater.Patched
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(ct);
            firstTag.Should().Be(FakeAcrTagLister.CiTag);

            // 2. LATER processes an UpdatePolicy change: flip the (now-seeded) node to Stable via
            //    the canonical stream.Update. Only the RE-ESTABLISHED live read can deliver this —
            //    the first read subscription died with the injected fault — so the Stable-only
            //    clean-release patch is the positive proof of both recovery and processing.
            var jsonOptions = Mesh.JsonSerializerOptions;
            await Mesh.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                .Update(node => node with
                {
                    Content = UpdatePolicyNodeType.ParseContent(node.Content, jsonOptions) with
                    {
                        Policy = UpdatePolicyKind.Stable,
                    },
                })
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(ct);

            var stableTag = await updater.Patched
                .Where(tag => tag == FakeAcrTagLister.StableTag)
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(ct);
            stableTag.Should().Be(FakeAcrTagLister.StableTag);

            // The recovery really went through the injected fault + at least one resubscription.
            service.ReadSubscriptions.Should().BeGreaterThanOrEqualTo(2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task MidLife_fault_reestablishes_silently_and_never_reapplies_the_default_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var acr = new FakeAcrTagLister();
        var updater = new RecordingUpdater();
        var options = new SelfUpdateOptions
        {
            RetryInterval = TimeSpan.FromMilliseconds(500),
            EventCoalesceWindow = TimeSpan.FromMilliseconds(50),
            DefaultPolicy = UpdatePolicyKind.Continuous, // default ≠ the node's Stable — the wrong-policy tell
        };

        // The install is PINNED to Stable before the poller ever starts (admin-set policy).
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
            {
                NodeType = UpdatePolicyNodeType.NodeType,
                Name = "Update Policy",
                State = MeshNodeState.Active,
                Content = new UpdatePolicyContent { Policy = UpdatePolicyKind.Stable },
            })
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .Await(ct);

        var service = new MidLifeFaultService(
            Mesh, acr, updater, options,
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>());

        await service.StartAsync(CancellationToken.None);
        try
        {
            // Startup runs under the PERSISTED policy (Stable), never the configured default —
            // the default is no longer prepended to the policy stream at all (#2731/#2797), so the
            // old "by-design fallback window" in which a pinned install could be patched from the
            // wrong policy is gone from the startup pass as well as from the retry. Checks are
            // event-driven, so the second Stable patch is driven by a build completion rather
            // than by waiting out a timer.
            // 🚨 The startup pass must be OBSERVED before publishing: StartAsync subscribes via
            // SubscribeOn(TaskPool), so it returns before the build-completion watch is established,
            // and a publication racing that subscription is absorbed as the watch's BASELINE rather
            // than seen as a new build. Waiting for the first patch proves the watch is live.
            await updater.Patched.FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await(ct);

            await SelfUpdateEventDriver.PublishBuildAsync(Mesh, "MeshWeaver", runNumber: 1);
            // ONE publication is ONE check now — the old "two patches" wait was a proxy for "the
            // interval has ticked enough times", which no longer exists. What the test proves is
            // unchanged and asserted at the end: nothing after the fault runs under the default.
            await updater.Patched
                .Where(tag => tag == FakeAcrTagLister.StableTag)
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(ct);
            var beforeFault = updater.Tags.Count;

            // Fault the LIVE read mid-life — the prod shape (hub-cache subscription drops while the
            // policy is Stable). The retry must re-establish the read silently: pre-fix it re-emitted
            // the DEFAULT policy (Continuous), re-drove Switch, and immediately polled under the
            // wrong policy — patching a ci build onto a Stable-pinned install.
            service.InjectFault(new TimeoutException(
                "No response received in hub cache/test within 00:01:00 for request SubscribeRequest "
                + $"→ target {UpdatePolicyNodeType.NodePath}"));

            // Positive signal: checks continue past the fault (two more Stable patches — the window
            // in which the pre-fix wrong-policy tick would land, since the retry re-establishes the
            // read after one RetryInterval). Each check is now driven by a publication, so the
            // events that must survive a mid-life fault are emitted explicitly.
            await SelfUpdateEventDriver.PublishBuildAsync(Mesh, "MeshWeaver", runNumber: 2);
            await SelfUpdateEventDriver.PublishBuildAsync(Mesh, "MeshWeaver.Plugins", runNumber: 1);
            await updater.Patched
                .Skip(beforeFault)
                .Where(tag => tag == FakeAcrTagLister.StableTag)
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(ct);

            // The wrong-policy window is GONE: nothing after the fault may run under the default
            // (Continuous ⇒ CiTag). Every post-fault patch is the Stable pick.
            var afterFault = updater.Tags.Skip(beforeFault).ToList();
            afterFault.Should().NotBeEmpty();
            afterFault.Should().OnlyContain(tag => tag == FakeAcrTagLister.StableTag);

            // ...and neither may the STARTUP pass (#2731/#2797). Asserted over EVERY patch, so the
            // window this test was written to close is now closed at both ends.
            updater.Tags.Should().OnlyContain(tag => tag == FakeAcrTagLister.StableTag,
                "the poller must never evaluate under a policy the install has not persisted");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 🚨 The memex-cloud defect, at production cadence (2026-09-01). A first policy read that
    /// faults must NOT park this install on <see cref="SelfUpdateOptions.RetryInterval"/>: every
    /// trigger — startup, build completion, policy change and the safety net — is gated on that
    /// first emission, so until it lands the install performs no checks AT ALL and the safety net
    /// cannot bound it, because the safety net is behind the same gate.
    ///
    /// <para>What it cost: memex.meshweaver.cloud served <c>rc8.ci.6829</c> while ACR had reached
    /// <c>rc9.ci.7231</c> — about 400 builds — with its pinned module set advanced past the image
    /// (modules "contributing nothing"), memory at 89% and satellite CI gates failing on its bundle
    /// endpoints. Two pods of the same build proved the mechanism: the one that logged <c>policy
    /// stream faulted; re-establishing in 06:00:00</c> had run ZERO checks, its sibling two.</para>
    ///
    /// <para>🚨 The pre-existing first-read test cannot see this, and that is the point of adding a
    /// second one: it sets <c>RetryInterval</c> itself to 500 ms, so it proves recovery HAPPENS
    /// while saying nothing about recovery happening usefully. Here RetryInterval keeps its
    /// production default, so the only thing that can make this test pass is the establishing
    /// cadence.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task A_faulted_FIRST_policy_read_recovers_at_the_establish_cadence_not_the_six_hour_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var acr = new FakeAcrTagLister();
        var updater = new RecordingUpdater();
        var options = new SelfUpdateOptions
        {
            // PRODUCTION value, deliberately: if the fix regresses, this is the wait, and the test
            // fails by timeout rather than by passing for the wrong reason.
            RetryInterval = TimeSpan.FromHours(6),
            PolicyEstablishRetryInterval = TimeSpan.FromMilliseconds(200),
            DefaultPolicy = UpdatePolicyKind.Continuous,
        };
        var service = new FaultingFirstReadService(
            Mesh, acr, updater, options,
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>());

        await service.StartAsync(CancellationToken.None);
        try
        {
            // The positive signal is a ROLL: it can only happen after a policy has actually been
            // read, which can only happen after the faulted first read was re-established.
            var applied = await updater.Patched
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(ct);
            applied.Should().Be(FakeAcrTagLister.CiTag);
            service.ReadSubscriptions.Should().BeGreaterThanOrEqualTo(2,
                "the recovery must have gone through the injected fault and a re-subscription");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The decision itself, both directions and in the right order: fast while no policy has been
    /// read (the install is inert), paced once one has (the value is retained and checks continue).
    /// Pure, so it pins the intent without waiting on a mesh.
    /// </summary>
    [Fact]
    public void PolicyRetryDelay_is_fast_while_establishing_and_paced_once_established()
    {
        var options = new SelfUpdateOptions();

        options.PolicyRetryDelay(policyEstablished: false).Should().Be(options.PolicyEstablishRetryInterval);
        options.PolicyRetryDelay(policyEstablished: true).Should().Be(options.RetryInterval);
        options.PolicyEstablishRetryInterval.Should().BeLessThan(options.RetryInterval,
            "an install with no policy yet is INERT — it must not wait a re-establishment interval");
    }
}
