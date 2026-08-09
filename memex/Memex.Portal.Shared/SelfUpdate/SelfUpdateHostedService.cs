using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// The platform self-update poller. Reactively (no async/await; the network/IO leaves route through
/// the <c>Http</c> <see cref="IIoPool"/>) polls the registry a few times a day, picks the update
/// target per the live <c>Admin/UpdatePolicy</c>, and — when the install runs in Kubernetes — patches
/// its own portal + migration Deployments to the new version so k8s rolls them. Outside Kubernetes it
/// records the available version for detect-and-notify. Mirrors <c>ShippedReleaseSeedHostedService</c>
/// (raw <see cref="IHostedService"/>, <c>SubscribeOn(TaskPoolScheduler.Default)</c>, one subscription).
/// Not sealed: <see cref="ReadPolicyStream"/> and <see cref="RecordAvailable"/> are the
/// fault-injection seams for the resilience test — they are the only two mesh touches this poller
/// makes, and therefore the only two ways a degraded mesh could ever stop an install rolling forward.
///
/// <para>🔁 wedges-to-zero, the standing invariant here: NEITHER mesh touch may gate the roll-forward.
/// The registry poll and the k8s PATCH speak only to ACR and the API server, so they keep working
/// while the mesh is degraded — and a fresh image is precisely what recovers a degraded pod. The
/// policy READ was decoupled in #611; the availability WRITE in #1020.</para>
/// </summary>
public class SelfUpdateHostedService : IHostedService
{
    private readonly IMessageHub _hub;
    private readonly IAcrTagLister _acr;
    private readonly IDeploymentUpdater _updater;
    private readonly SelfUpdateOptions _options;
    private readonly ILogger<SelfUpdateHostedService>? _logger;
    private readonly IIoPool _http;
    private IDisposable? _subscription;

    public SelfUpdateHostedService(
        IMessageHub hub,
        IAcrTagLister acr,
        IDeploymentUpdater updater,
        SelfUpdateOptions options,
        ILogger<SelfUpdateHostedService>? logger = null,
        IoPoolRegistry? registry = null)
    {
        _hub = hub;
        _acr = acr;
        _updater = updater;
        _options = options;
        _logger = logger;
        // The ACR list + the k8s PATCH are outbound HTTP → the Http resource class. Falls back to the
        // stateless unbounded pool when no registry is wired (tests).
        _http = registry?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation(
            "[SelfUpdate] starting; version={Version}, registry={Registry}/{Repo}, canPatch={CanPatch}, interval={Interval}.",
            ShippedReleaseSeed.InstalledPlatformVersion, _options.Registry, _options.PortalRepository,
            _updater.CanPatch, _options.PollInterval);

        _subscription = CreatePolicySource()
            // The policy re-drives the poller via Switch. With DistinctUntilChanged the timer is only
            // re-subscribed when the admin ACTUALLY changes the policy (human-rare) — not a storm.
            .Select(content => content.Policy == UpdatePolicyKind.None
                ? Observable.Empty<Unit>()                            // None => never poll
                : Observable.Interval(_options.PollInterval).StartWith(-1L)
                    .SelectMany(_ => RunOnce(content)
                        .Catch((Exception ex) =>
                        {
                            // wedges-to-zero: a tick error (ACR outage, k8s 403) logs and the poller
                            // keeps ticking. No outer .Retry (that would be a resubscribe storm).
                            _logger?.LogWarning(ex, "[SelfUpdate] check failed (policy={Policy}).", content.Policy);
                            return Observable.Empty<Unit>();
                        })))
            .Switch()
            .SubscribeOn(TaskPoolScheduler.Default)
            .Subscribe(
                _ => { },
                ex => _logger?.LogError(ex, "[SelfUpdate] poller terminated unexpectedly."));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The policy source driving the poller. Two fault-isolated stages, each with its own silent
    /// resubscribe (🔁 wedges-to-zero: the 2026-07-23 prod hub-cache SubscribeRequest to
    /// <c>Admin/UpdatePolicy</c> timed out while the pod was degraded, OnError'd through
    /// <c>.Switch()</c> into the terminal Subscribe, and KILLED the poller for the life of the pod —
    /// exactly when the update it polls for is what would have recovered it):
    /// <list type="number">
    /// <item>Seed <c>Admin/UpdatePolicy</c> if absent (storm-safe, via a query — never a point-read
    /// of a maybe-absent node); a seeding fault retries at the polling cadence.</item>
    /// <item>The live node stream. The default policy is prepended exactly ONCE — after the seed
    /// (so the first tick can never point-write a not-yet-existing node), before the first live
    /// emission. A stream fault retries INSIDE the StartWith, so a retry re-establishes the read
    /// SILENTLY: it never re-emits the default, and therefore can never flip a Stable/None install
    /// back to default-policy polling (Copilot review on #611).</item>
    /// </list>
    /// Retries are delayed, Rx-composed resubscribes at the polling cadence — not a hot retry loop,
    /// not a watchdog. <c>DistinctUntilChanged</c> sits outermost so only a REAL policy change (or
    /// the initial value) re-drives the Switch.
    /// </summary>
    private IObservable<UpdatePolicyContent> CreatePolicySource()
    {
        var accessService = _hub.ServiceProvider.GetService<AccessService>();
        return Observable
            .Defer(() => UpdatePolicyNodeType.EnsureExists(_hub, accessService, _options.DefaultPolicy, _logger))
            .RetryWhen(ResubscribeAfterPollInterval("policy-node seeding"))
            .Take(1)
            .SelectMany(_ => Observable
                .Defer(ReadPolicyStream)
                .RetryWhen(ResubscribeAfterPollInterval("policy stream"))
                .StartWith(new UpdatePolicyContent { Policy = _options.DefaultPolicy }))
            .DistinctUntilChanged(c => (c.Policy, c.RequireCiGreen)); // <-- re-switch only on a REAL policy change
    }

    /// <summary>
    /// The live <c>Admin/UpdatePolicy</c> read: node stream → parsed content. Virtual: the resilience
    /// test overrides this to inject faults at the exact seam the prod hub-cache SubscribeRequest
    /// timeout surfaced through. Only ever subscribed AFTER the seed stage, so the path exists.
    /// </summary>
    protected virtual IObservable<UpdatePolicyContent> ReadPolicyStream()
    {
        var workspace = _hub.GetWorkspace();
        var jsonOptions = _hub.JsonSerializerOptions;
        return workspace.GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
            .Select(node => UpdatePolicyNodeType.Parse(node, jsonOptions));
    }

    /// <summary>Retry signal for <c>RetryWhen</c>: log the fault and resubscribe after one poll
    /// interval (delayed, Rx-composed — no hot loop).</summary>
    private Func<IObservable<Exception>, IObservable<long>> ResubscribeAfterPollInterval(string stage) =>
        faults => faults.SelectMany(ex =>
        {
            _logger?.LogWarning(ex,
                "[SelfUpdate] {Stage} faulted; re-establishing in {Interval}.", stage, _options.PollInterval);
            return Observable.Timer(_options.PollInterval);
        });

    /// <summary>One evaluation: list tags → pick target per policy → gate target &gt; current →
    /// record availability → (if armed) patch the workloads.</summary>
    private IObservable<Unit> RunOnce(UpdatePolicyContent policy) =>
        _http.Invoke(ct => _acr.ListTagsAsync(_options.PortalRepository, ct))
            .Select(tags => VersionSelect.PickTarget(tags, policy.Policy, policy.RequireCiGreen))
            .Where(target => !string.IsNullOrEmpty(target)
                          && VersionSelect.IsNewer(target!, ShippedReleaseSeed.InstalledPlatformVersion))
            .SelectMany(target => RecordAvailable(target!)
                // 🚨 BOOKKEEPING, NOT A GATE (#1020). RecordAvailable writes two cosmetic fields
                // (LatestAvailableTag / CheckedAt) that drive the admin tab; the ROLL-FORWARD does not
                // depend on them. Chaining Apply after it with .SelectMany made a failed status write
                // abort the update: on atioz the Admin/UpdatePolicy node hub was unreachable (its
                // SubscribeRequest never answered — the silo's routing was wedged), every 6 h tick
                // timed out after 30 s in this write, and the portal sat 37 h on a stale image while
                // the poller kept ticking and the ACR check kept succeeding. The update it would not
                // apply is exactly what recovers a degraded pod, so the write must never gate it:
                // surface the fault (never swallow it — the warning names the tag and the node) and
                // carry on to Apply, which touches only ACR + the k8s API and works while the mesh
                // is degraded. Concat, not Merge — the record still lands FIRST when it succeeds.
                .Catch((Exception ex) =>
                {
                    _logger?.LogWarning(ex,
                        "[SelfUpdate] could not record available tag {Tag} on {Node}; applying the update anyway.",
                        target, UpdatePolicyNodeType.NodePath);
                    return Observable.Return(Unit.Default);
                })
                .Concat(Apply(target!)));

    /// <summary>
    /// Applies the picked target: patch the workloads where this install is armed, else record-only
    /// (detect-and-notify). The intent is announced BEFORE the attempt, so a failing apply is
    /// diagnosable: the tick's error sink logs a generic "check failed", and until #1020 that was the
    /// ONLY trace a stalled install left — it read like a registry problem while the registry was
    /// fine. Deferred so the announcement runs per subscription (per tick), not at composition.
    /// </summary>
    private IObservable<Unit> Apply(string target) =>
        Observable.Defer(() =>
        {
            if (!_updater.CanPatch)
            {
                _logger?.LogInformation(
                    "[SelfUpdate] update available: {Tag} (detect-and-notify — this install does not self-patch).",
                    target);
                return Observable.Return(Unit.Default);
            }

            _logger?.LogInformation(
                "[SelfUpdate] applying update {Tag} (was {Current}).",
                target, ShippedReleaseSeed.InstalledPlatformVersion);
            return _http.Invoke(ct => _updater.PatchToVersionAsync(target, ct));
        });

    /// <summary>Record the newest available tag on the policy node (as System). Drives the admin tab
    /// and the detect-and-notify path. Touches only the bookkeeping fields; preserves Policy.
    /// Virtual: the second fault-injection seam for the resilience test (alongside
    /// <see cref="ReadPolicyStream"/>) — it is where the #1020 prod stall surfaced.</summary>
    protected virtual IObservable<Unit> RecordAvailable(string tag)
    {
        var accessService = _hub.ServiceProvider.GetService<AccessService>();
        var jsonOptions = _hub.JsonSerializerOptions;
        return Observable.Using(
            () => AccessContextScope.AsSystem(accessService),
            _ => _hub.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                .Update(node =>
                {
                    var cur = UpdatePolicyNodeType.ParseContent(node.Content, jsonOptions);
                    return node with
                    {
                        Content = cur with { LatestAvailableTag = tag, CheckedAt = DateTimeOffset.UtcNow },
                    };
                })
                .Select(_ => Unit.Default));
    }
}
