using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
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
        => base.ConfigureMesh(builder).AddUpdatePolicyType();

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

    [Fact(Timeout = 60000)]
    public async Task Poller_survives_faulted_first_read_and_processes_a_policy_change()
    {
        var ct = TestContext.Current.CancellationToken;
        var acr = new FakeAcrTagLister();
        var updater = new RecordingUpdater();
        var options = new SelfUpdateOptions
        {
            // The poll interval doubles as the fault-resubscribe delay — short so the test observes
            // the recovery promptly.
            PollInterval = TimeSpan.FromMilliseconds(500),
            DefaultPolicy = UpdatePolicyKind.Continuous,
        };
        var service = new FaultingFirstReadService(
            Mesh, acr, updater, options,
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>());

        await service.StartAsync(CancellationToken.None);
        try
        {
            // 1. The first live read FAULTS (TimeoutException — the prod shape) right after the seed.
            //    The startup default (Continuous) still drives the first poll, and the faulted read
            //    must retry silently in the background. Pre-fix the fault terminated the whole
            //    subscription permanently.
            var firstTag = await updater.Patched
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(ct);
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
                .ToTask(ct);

            var stableTag = await updater.Patched
                .Where(tag => tag == FakeAcrTagLister.StableTag)
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(ct);
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
            PollInterval = TimeSpan.FromMilliseconds(500),
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
            .ToTask(ct);

        var service = new MidLifeFaultService(
            Mesh, acr, updater, options,
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>());

        await service.StartAsync(CancellationToken.None);
        try
        {
            // Startup: the default (Continuous) drives polling ONCE until the live Stable emission
            // arrives (by-design fallback window), then Stable-only patches. Wait for TWO Stable
            // patches so any in-flight startup Continuous tick has long since drained, then snapshot.
            await updater.Patched
                .Where(tag => tag == FakeAcrTagLister.StableTag)
                .Take(2).LastAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(ct);
            var beforeFault = updater.Tags.Count;

            // Fault the LIVE read mid-life — the prod shape (hub-cache subscription drops while the
            // policy is Stable). The retry must re-establish the read silently: pre-fix it re-emitted
            // the DEFAULT policy (Continuous), re-drove Switch, and immediately polled under the
            // wrong policy — patching a ci build onto a Stable-pinned install.
            service.InjectFault(new TimeoutException(
                "No response received in hub cache/test within 00:01:00 for request SubscribeRequest "
                + $"→ target {UpdatePolicyNodeType.NodePath}"));

            // Positive signal: polling continues past the fault (two more Stable patches — a window
            // in which the pre-fix wrong-policy tick would land, since the retry fires after one
            // PollInterval with an immediate first tick).
            await updater.Patched
                .Skip(beforeFault)
                .Where(tag => tag == FakeAcrTagLister.StableTag)
                .Take(2).LastAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(ct);

            // The wrong-policy window is GONE: nothing after the fault may run under the default
            // (Continuous ⇒ CiTag). Every post-fault patch is the Stable pick.
            var afterFault = updater.Tags.Skip(beforeFault).ToList();
            afterFault.Should().NotBeEmpty();
            afterFault.Should().OnlyContain(tag => tag == FakeAcrTagLister.StableTag);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
