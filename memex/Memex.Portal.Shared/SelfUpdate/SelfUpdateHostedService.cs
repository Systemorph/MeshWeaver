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
/// Not sealed: <see cref="CreatePolicySource"/> is the fault-injection seam for the resilience test.
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

        _subscription = Observable
            // Defer so every (re)subscription rebuilds the whole policy source — seed + live stream.
            .Defer(CreatePolicySource)
            // 🔁 wedges-to-zero: the policy READ can fault transiently — e.g. the 2026-07-23 prod
            // hub-cache SubscribeRequest to Admin/UpdatePolicy timing out while the pod was degraded.
            // That fault used to OnError through .Switch() into the terminal Subscribe and KILL the
            // poller for the life of the pod — exactly when the update it polls for is what would
            // recover it. Log the fault and re-establish the subscription at the existing polling
            // cadence (a delayed, Rx-composed resubscribe — not a hot retry loop, not a watchdog).
            .RetryWhen(faults => faults.SelectMany(ex =>
            {
                _logger?.LogWarning(ex,
                    "[SelfUpdate] policy stream faulted; re-establishing in {Interval}.",
                    _options.PollInterval);
                return Observable.Timer(_options.PollInterval);
            }))
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
    /// The policy source: seed <c>Admin/UpdatePolicy</c> if absent (storm-safe, via a query — never a
    /// point-read of a maybe-absent node), then the live node stream, keyed to re-emit only on a REAL
    /// policy change, starting with the default so the poller runs before the first live emission.
    /// Virtual: the resilience test overrides this to inject a first-subscription fault at the exact
    /// seam the prod hub-cache SubscribeRequest timeout surfaced through.
    /// </summary>
    protected virtual IObservable<UpdatePolicyContent> CreatePolicySource()
    {
        var workspace = _hub.GetWorkspace();
        var accessService = _hub.ServiceProvider.GetService<AccessService>();
        var jsonOptions = _hub.JsonSerializerOptions;
        return UpdatePolicyNodeType
            .EnsureExists(_hub, accessService, _options.DefaultPolicy, _logger)
            .SelectMany(_ => workspace.GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                .Select(node => UpdatePolicyNodeType.Parse(node, jsonOptions))
                .DistinctUntilChanged(c => (c.Policy, c.RequireCiGreen)) // <-- re-switch only on a REAL policy change
                .StartWith(new UpdatePolicyContent { Policy = _options.DefaultPolicy }));
    }

    /// <summary>One evaluation: list tags → pick target per policy → gate target &gt; current →
    /// record availability → (if armed) patch the workloads.</summary>
    private IObservable<Unit> RunOnce(UpdatePolicyContent policy) =>
        _http.Invoke(ct => _acr.ListTagsAsync(_options.PortalRepository, ct))
            .Select(tags => VersionSelect.PickTarget(tags, policy.Policy, policy.RequireCiGreen))
            .Where(target => !string.IsNullOrEmpty(target)
                          && VersionSelect.IsNewer(target!, ShippedReleaseSeed.InstalledPlatformVersion))
            .SelectMany(target => RecordAvailable(target!)
                .SelectMany(_ => _updater.CanPatch
                    ? _http.Invoke(ct => _updater.PatchToVersionAsync(target!, ct))
                        .Do(_ => _logger?.LogInformation(
                            "[SelfUpdate] applying update {Tag} (was {Current}).",
                            target, ShippedReleaseSeed.InstalledPlatformVersion))
                    : Observable.Return(Unit.Default)
                        .Do(_ => _logger?.LogInformation(
                            "[SelfUpdate] update available: {Tag} (detect-and-notify — this install does not self-patch).",
                            target))));

    /// <summary>Record the newest available tag on the policy node (as System). Drives the admin tab
    /// and the detect-and-notify path. Touches only the bookkeeping fields; preserves Policy.</summary>
    private IObservable<Unit> RecordAvailable(string tag)
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
