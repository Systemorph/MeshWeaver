using System;
using System.Collections.Generic;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Resilience test for the self-update poller against a REAL monolith mesh. Pins the 2026-07-23
/// memex-cloud prod defect: the FIRST policy read (the hub-cache <c>SubscribeRequest</c> to
/// <c>Admin/UpdatePolicy</c>) faulted with a <see cref="TimeoutException"/>, the error OnError'd
/// through <c>.Switch()</c> into the terminal Subscribe, and the poller was dead for the life of the
/// pod — the pod stopped self-updating exactly when the update was what would have recovered it.
/// The fault is injected at the exact seam it surfaced through in prod
/// (<see cref="SelfUpdateHostedService.CreatePolicySource"/>); everything else — hub, workspace,
/// <c>EnsureExists</c> seeding, the live node stream, <c>stream.Update</c> processing — is real.
/// The only fakes are the two documented external-IO seams (ACR REST, k8s PATCH).
/// </summary>
public class SelfUpdatePollerResilienceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType();

    /// <summary>Fake registry (the documented injectable IO seam): a ci build newer than anything
    /// installed plus an even "older" clean release — Continuous picks the ci tag, Stable the clean
    /// one, so a policy flip produces a DIFFERENT positive signal (no negative-assertion waiting).</summary>
    private sealed class FakeAcrTagLister : IAcrTagLister
    {
        public const string CiTag = "9999.0.0-ci.1";
        public const string StableTag = "8888.0.0";

        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([CiTag, StableTag]);
    }

    /// <summary>Fake k8s patcher (the documented injectable IO seam) — records every applied tag.</summary>
    private sealed class RecordingUpdater : IDeploymentUpdater
    {
        private readonly ReplaySubject<string> _patched = new();
        public IObservable<string> Patched => _patched;
        public bool CanPatch => true;

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct)
        {
            _patched.OnNext(versionTag);
            return Task.CompletedTask;
        }
    }

    /// <summary>Injects the prod fault shape: the FIRST subscription of the policy source throws the
    /// hub-cache SubscribeRequest <see cref="TimeoutException"/>; every later (re)subscription runs
    /// the REAL pipeline untouched.</summary>
    private sealed class FaultingFirstReadService(
        IMessageHub hub,
        IAcrTagLister acr,
        IDeploymentUpdater updater,
        SelfUpdateOptions options,
        ILogger<SelfUpdateHostedService>? logger)
        : SelfUpdateHostedService(hub, acr, updater, options, logger)
    {
        private int _subscriptions;

        public int PolicySourceSubscriptions => Volatile.Read(ref _subscriptions);

        protected override IObservable<UpdatePolicyContent> CreatePolicySource() =>
            Interlocked.Increment(ref _subscriptions) == 1
                ? Observable.Throw<UpdatePolicyContent>(new TimeoutException(
                    "No response received in hub cache/test within 00:01:00 for request SubscribeRequest "
                    + $"→ target {UpdatePolicyNodeType.NodePath}"))
                : base.CreatePolicySource();
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
            // 1. The first policy read FAULTS (TimeoutException — the prod shape). The poller must
            //    log + resubscribe, seed Admin/UpdatePolicy via EnsureExists, poll the registry
            //    under the default Continuous policy, and patch to the newest ci build. Pre-fix
            //    this hangs forever: the fault terminated the subscription permanently.
            var firstTag = await updater.Patched
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(ct);
            firstTag.Should().Be(FakeAcrTagLister.CiTag);

            // The recovery really went through the injected fault + one resubscription.
            service.PolicySourceSubscriptions.Should().BeGreaterThanOrEqualTo(2);

            // 2. LATER processes an UpdatePolicy change: flip the (now-existing) node to Stable via
            //    the canonical stream.Update. The re-established live node stream must react —
            //    Stable ignores ci builds, so the next pick is the clean release: a distinct,
            //    positive signal that the change was processed.
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
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
