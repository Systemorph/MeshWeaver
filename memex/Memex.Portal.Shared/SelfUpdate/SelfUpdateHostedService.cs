using System.Reactive.Disposables;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MeshWeaver.Hosting.SelfUpdate;

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
            "[SelfUpdate] starting (event-driven; one startup pass, then a check per build-completion event); version={Version}, registry={Registry}/{Repo}, canPatch={CanPatch}, retryInterval={Interval}.",
            ShippedReleaseSeed.InstalledPlatformVersion, _options.Registry, _options.PortalRepository,
            _updater.CanPatch, _options.RetryInterval);

        // 🚨 EVENT-DRIVEN, and the event source is deliberately OUTSIDE the policy stream.
        //
        // Exactly ONE pass at startup — to catch publications missed while this install was down —
        // and after that a check per build completion, of the platform OR of any module the
        // environment deploys. There is no recurring interval: a timer that re-asks a question
        // nothing has answered is the shape this codebase treats as a band-aid, and the
        // availability gate already defers a roll whose artifacts are not ready. What makes
        // deferral safe without a timer is that the NEXT publication is itself an event, so a
        // deferred roll is re-decided the moment its missing artifact appears.
        //
        // 🚨 The policy is STATE, not a driver — WithLatestFrom, never Switch. Under Switch every
        // policy emission re-subscribed the watch, which RE-BASELINED it: a publication landing in
        // that window was read as "current state" rather than as a new build and silently swallowed.
        // The recurring interval used to cover that; with the interval gone it would be a lost
        // update with nothing to recover it. One long-lived watch, the latest policy read at
        // decision time, is what closes it. (CreatePolicySource emits the default exactly once
        // before the first live read, so the startup pass always has a policy to run under.)
        // The policy is shared STATE with a replayed latest value, connected for the lifetime of the
        // service. Replay(1)+Connect rather than RefCount: a RefCount would drop to zero between the
        // Take(1) below and the WithLatestFrom re-subscribe, re-running the seed and re-baselining
        // everything downstream — the very class of bug this restructure exists to remove.
        var policy = CreatePolicySource().Replay(1);

        // 🚨 The first check waits for a policy to exist. CreatePolicySource emits the default only
        // AFTER its async seed, so a startup pass composed with WithLatestFrom alone fires before
        // any policy is available and is silently dropped — no first check at all. Take(1) gates the
        // trigger stream on that first emission; every LATER policy change is picked up by
        // WithLatestFrom without re-subscribing the watch.
        // Three trigger sources, no timer among them:
        //   • the one startup pass,
        //   • a build completion from the platform or ANY module the environment deploys,
        //   • an admin CHANGING the policy — enabling updates must not wait for the next
        //     publication, which could be weeks away. DistinctUntilChanged so a re-emission of the
        //     same policy is not a trigger, and Skip(1) so the replayed CURRENT policy is not one
        //     either (the startup pass already covers it).
        var checks = policy
            .Take(1)
            .SelectMany(_ => Observable.Merge(
                Observable.Return(-1L),
                BuildCompletionTicks(),
                policy.DistinctUntilChanged(content => content.Policy).Skip(1).Select(_ => -3L)))
            // 🚨 Read the CURRENT policy at decision time — never WithLatestFrom. That operator only
            // pairs once its secondary has produced, and the startup trigger fires synchronously on
            // subscribe, so whether the first check survives came down to Rx's internal subscribe
            // ordering: it silently dropped the startup pass. policy is Replay(1), so Take(1) yields
            // the latest value immediately and deterministically.
            .SelectMany(_ => policy.Take(1))
            .Where(content => content.Policy != UpdatePolicyKind.None)   // None => never update
            .SelectMany(content => RunOnce(content)
                .Catch((Exception ex) =>
                {
                    // wedges-to-zero: a check error (ACR outage, k8s 403) logs and the watch stays
                    // live. No outer .Retry — that would be a resubscribe storm.
                    _logger?.LogWarning(ex, "[SelfUpdate] check failed (policy={Policy}).", content.Policy);
                    return Observable.Empty<Unit>();
                }))
            .SubscribeOn(TaskPoolScheduler.Default)
            .Subscribe(
                _ => { },
                ex => _logger?.LogError(ex, "[SelfUpdate] update watch terminated unexpectedly."));

        _subscription = new CompositeDisposable(checks, policy.Connect());
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
            .RetryWhen(ResubscribeAfterRetryInterval("policy-node seeding"))
            .Take(1)
            .SelectMany(_ => Observable
                .Defer(ReadPolicyStream)
                .RetryWhen(ResubscribeAfterRetryInterval("policy stream"))
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

    /// <summary>
    /// Ticks once per NEW green build of ANY repository that publishes — the platform and every
    /// satellite alike —
    /// the event-driven complement to the interval. The <c>BuildCompletion</c> node is a FACT the
    /// GitHub webhook writes; on an install without the webhook the query never yields and the
    /// interval still drives everything. Throttled so a burst of workflow completions coalesces
    /// into one check. This is a THIRD mesh touch, and the standing invariant applies: it may never
    /// gate or kill the poller — a fault re-establishes the watch silently at the polling cadence,
    /// and <see cref="RunOnce"/> still lists ACR itself, so a spurious tick costs one cheap tag
    /// list and can never cause a wrong roll.
    /// </summary>
    protected virtual IObservable<long> BuildCompletionTicks() =>
        Observable
            .Defer(() => NewBuildEventsAcross(_hub
                .GetQuery($"SelfUpdate.BuildTrigger:{_hub.Address}",
                    $"nodeType:{BuildCompletion.NodeType}")))
            .Throttle(_options.EventCoalesceWindow)
            .Select(_ => -2L)
            .RetryWhen(ResubscribeAfterRetryInterval("build-completion watch"));

    /// <summary>
    /// Turns a live read of one node into "a NEW write happened while we were watching": the
    /// replayed current state (or its absence) is baseline, never an event — a pod start must not
    /// look like a fresh build — and every subsequent version change is one event. Pure and static,
    /// so the distinction is pinned by a unit test rather than re-derived from Rx operator lore.
    /// </summary>
    public static IObservable<Unit> NewBuildEvents(IObservable<MeshNode?> node) =>
        node.Scan(
                (Baselined: false, Version: 0L, IsEvent: false),
                (state, current) => current is null
                    ? (true, state.Version, false)                 // absent baseline (or deleted)
                    : !state.Baselined
                        ? (true, current.Version, false)           // replayed current state
                        : current.Version != state.Version
                            ? (true, current.Version, true)        // a NEW green build
                            : (true, current.Version, false))
            .Where(s => s.IsEvent)
            .Select(_ => Unit.Default);

    /// <summary>
    /// The collection-wide form: "a NEW build completed for ANY repository we care about".
    ///
    /// <para>Every repository that publishes writes its own <c>BuildCompletion</c> record under
    /// <c>Admin/_Build</c> — the platform and every satellite alike — so watching the whole
    /// collection is what makes "listen for updates of any module or platform" one subscription
    /// rather than a per-repo fan-out that would have to be rebuilt whenever the installed set
    /// changes.</para>
    ///
    /// <para>Same baseline rule as the single-node form and for the same reason: the replayed
    /// current state is baseline, never an event, so a pod start cannot look like a fresh build.
    /// After that, a record whose version moved OR a record that appeared is one event. A record
    /// that DISAPPEARS is deliberately not an event — there is nothing to update toward.</para>
    ///
    /// <para>Pure and static, so the distinction is pinned by unit tests instead of re-derived
    /// from Rx operator lore.</para>
    /// </summary>
    public static IObservable<Unit> NewBuildEventsAcross(IObservable<IEnumerable<MeshNode>?> nodes) =>
        nodes.Scan(
                (Baselined: false, Versions: ImmutableDictionary<string, long>.Empty, IsEvent: false),
                (state, current) =>
                {
                    var snapshot = (current ?? [])
                        .GroupBy(n => n.Path)
                        .ToImmutableDictionary(g => g.Key, g => g.Max(n => n.Version));
                    if (!state.Baselined)
                        return (true, snapshot, false);
                    var advanced = snapshot.Any(kv =>
                        !state.Versions.TryGetValue(kv.Key, out var seen) || kv.Value != seen);
                    return (true, snapshot, advanced);
                })
            .Where(s => s.IsEvent)
            .Select(_ => Unit.Default);

    /// <summary>Retry signal for <c>RetryWhen</c>: log the fault and resubscribe after the retry
    /// interval (delayed, Rx-composed — no hot loop).</summary>
    private Func<IObservable<Exception>, IObservable<long>> ResubscribeAfterRetryInterval(string stage) =>
        faults => faults.SelectMany(ex =>
        {
            _logger?.LogWarning(ex,
                "[SelfUpdate] {Stage} faulted; re-establishing in {Interval}.", stage, _options.RetryInterval);
            return Observable.Timer(_options.RetryInterval);
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
                .Concat(GateThenApply(target!)));

    /// <summary>
    /// 🚨 <b>The release-availability gate (#1754), the last thing between a newer tag and a roll.</b>
    ///
    /// <para>"Newer" was never the right precondition. Every package this environment deploys must
    /// also have a usable artifact FOR the target release — a sealed content bake under its
    /// framework identity, a module floor it satisfies — or the roll trades a working portal for
    /// one that Roslyn-compiles its whole content set at boot, parking a hub for the full
    /// activation budget per type that fails.</para>
    ///
    /// <para>It fails SAFE: a verdict that cannot be determined is a HOLD, not a pass. And it fails
    /// LOUD in both directions — the refusal is logged at Information AND written to the policy node
    /// (<see cref="UpdatePolicyContent.HeldTag"/>/<see cref="UpdatePolicyContent.HeldReason"/>), so
    /// the Updates tab reports it. A silent freeze is the outage this gate must never become.</para>
    ///
    /// <para>The hold is re-evaluated on EVERY tick and never persists a decision: the moment the
    /// missing bake is published, the next poll (or the next green-build event) clears the hold and
    /// rolls. Nothing has to be un-stuck by hand — which is what makes the refusal safe to make.</para>
    ///
    /// <para>Like <see cref="RecordAvailable"/>, the bookkeeping write can never gate the roll
    /// (#1020): recording the hold is best-effort, but the DECISION is taken from the verdict
    /// itself, and a gate that could not run at all resolves to a hold with its own reason rather
    /// than to an exception that kills the tick.</para>
    /// </summary>
    private IObservable<Unit> GateThenApply(string target) =>
        Observable.Defer(() =>
        {
            var gate = ResolveAvailabilityGate();
            if (gate is null)
                // No gate registered at all (a host that wires the poller without it). Say so once
                // per tick rather than silently rolling as if it had passed.
                return Observable.Defer(() =>
                {
                    _logger?.LogInformation(
                        "[SelfUpdate] no release-availability gate is registered — rolling {Tag} "
                        + "without checking whether its packages are available.", target);
                    return Apply(target);
                });

            return gate.IsUpdatable(target)
                .SelectMany(verdict =>
                {
                    if (verdict.IsUpdatable)
                    {
                        if (verdict.NotEnforcedReason is { } notEnforced)
                            _logger?.LogInformation(
                                "[SelfUpdate] release-availability gate not enforced for {Tag}: {Reason}",
                                target, notEnforced);
                        // Clearing is unconditional: a previous hold that no longer applies must
                        // disappear from the admin tab the moment it is resolved.
                        return RecordHold(target, null).Catch(HoldWriteFailed(target))
                            .Concat(Apply(target));
                    }

                    _logger?.LogInformation(
                        "[SelfUpdate] HOLDING update to {Tag} (staying on {Current}) — {Reason}",
                        target, ShippedReleaseSeed.InstalledPlatformVersion, verdict.HoldReason);
                    return RecordHold(target, verdict).Catch(HoldWriteFailed(target));
                });
        });

    /// <summary>
    /// The release-availability gate, resolved from the mesh's services. Virtual: the third
    /// documented injection seam, so a test can pin what the poller DOES with a verdict without
    /// also staging an artifact store (the verdict itself is pinned against a real one elsewhere).
    /// </summary>
    protected virtual ReleaseAvailabilityService? ResolveAvailabilityGate() =>
        _hub.ServiceProvider.GetService<ReleaseAvailabilityService>();

    private Func<Exception, IObservable<Unit>> HoldWriteFailed(string target) =>
        ex =>
        {
            _logger?.LogWarning(ex,
                "[SelfUpdate] could not record the availability hold for {Tag} on {Node}; the "
                + "verdict itself still stands.", target, UpdatePolicyNodeType.NodePath);
            return Observable.Return(Unit.Default);
        };

    /// <summary>
    /// Writes (or clears, on null) the availability hold on the policy node, as System — the same
    /// shape and the same reasons as <see cref="RecordAvailable"/>. Virtual so a test can fault it
    /// and prove the hold DECISION survives a failed hold WRITE.
    /// </summary>
    protected virtual IObservable<Unit> RecordHold(string tag, UpdatabilityVerdict? verdict)
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
                        Content = verdict is null
                            ? cur with
                            {
                                HeldTag = null, HeldReason = null,
                                HeldIndeterminate = false, HeldAt = null,
                            }
                            : cur with
                            {
                                HeldTag = tag,
                                HeldReason = verdict.HoldReason,
                                HeldIndeterminate = verdict.IsIndeterminate,
                                HeldAt = DateTimeOffset.UtcNow,
                            },
                    };
                })
                .Select(_ => Unit.Default));
    }

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

            // 🚨 The pacing floor. A roll is a POD RESTART, so without it publication frequency is
            // restart frequency and every restart drops the live circuits of everyone using the
            // portal. The floor bounds the AUTOMATIC cadence only: `kubectl rollout restart` still
            // takes the newest image immediately (the startup pass), and a main-cd dispatch still
            // bypasses the batch window, so nothing here delays an urgent fix.
            //
            // Deferring is safe WITHOUT a timer for the same reason an availability hold is: the
            // next publication event re-decides it. The floor never schedules anything.
            // A disabled floor must not pay for the stamp read: LastRolledAtAsync is a Kubernetes GET
            // in the AKS implementation, and its answer cannot change the decision when the floor is
            // zero. Skipping it removes an API call and a failure point from the happy path.
            if (_options.MinRollInterval <= TimeSpan.Zero)
            {
                _logger?.LogInformation(
                    "[SelfUpdate] applying update {Tag} (was {Current}; no roll floor configured).",
                    target, ShippedReleaseSeed.InstalledPlatformVersion);
                return _http.Invoke(ct => _updater.PatchToVersionAsync(target, ct));
            }

            return _http.Invoke(ct => _updater.LastRolledAtAsync(ct)).SelectMany(lastRolledAt =>
            {
                var since = lastRolledAt is null ? (TimeSpan?)null : DateTimeOffset.UtcNow - lastRolledAt.Value;
                if (since is { } elapsed && elapsed < _options.MinRollInterval)
                {
                    _logger?.LogInformation(
                        "[SelfUpdate] {Tag} is available but this install rolled {Elapsed} ago, inside the "
                        + "{Floor} floor — deferring. The next publication re-decides it; "
                        + "`kubectl rollout restart` applies it now.",
                        target, elapsed, _options.MinRollInterval);
                    return Observable.Return(Unit.Default);
                }

                _logger?.LogInformation(
                    "[SelfUpdate] applying update {Tag} (was {Current}; last rolled {Last}).",
                    target, ShippedReleaseSeed.InstalledPlatformVersion,
                    lastRolledAt?.ToString("O") ?? "never");
                return _http.Invoke(ct => _updater.PatchToVersionAsync(target, ct));
            });
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
