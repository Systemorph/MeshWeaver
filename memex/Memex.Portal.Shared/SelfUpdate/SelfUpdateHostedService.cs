using Microsoft.Extensions.Configuration;
using System.Reactive.Disposables;
using System.Threading;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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

    /// <summary>
    /// How many build-completion events this process has observed. The ONLY evidence that the event
    /// channel is alive, and it exists solely to make the dead-channel report in
    /// <see cref="ReportCheck"/> possible — nothing else can distinguish "nothing published" from
    /// "something published and nothing told us". Mutable instance state, read with
    /// <see cref="Volatile"/> because the watch and the check run on different pool threads.
    /// </summary>
    private int _buildEventsSeen;

    /// <summary>
    /// 1 once the live policy read has produced a value — i.e. once the gate every trigger waits on
    /// has opened. Chooses the retry cadence for a faulted watch (see
    /// <see cref="SelfUpdateOptions.PolicyRetryDelay"/>): before it, the poller is inert and a fault
    /// must be retried quickly; after it, the long interval paces re-establishment. An instance
    /// field — its lifetime is this service's — never static.
    /// </summary>
    private int _policyEstablished;

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
            "[SelfUpdate] starting (event-driven, with a {SafetyNet} safety net); version={Version}, "
            + "registry={Registry}/{Repo}, canPatch={CanPatch}, retryInterval={Interval}.",
            _options.SafetyNetCheckInterval > TimeSpan.Zero
                ? _options.SafetyNetCheckInterval.ToString()
                : "disabled",
            ShippedReleaseSeed.InstalledPlatformVersion, _options.Registry, _options.PortalRepository,
            _updater.CanPatch, _options.RetryInterval);

        // 🚨 EVENT-DRIVEN WITH A SAFETY NET, and the event source is deliberately OUTSIDE the
        // policy stream.
        //
        // The fast path is unchanged and is still the design: one pass at startup (to catch
        // publications missed while this install was down), then a check per build completion of
        // the platform OR of any module the environment deploys. Nothing beats an event for
        // latency, and a timer that re-asks a question nothing has answered is a band-aid.
        //
        // 🚨 What the event-ONLY shape got wrong is its failure mode, and it cost a week of prod
        // (#2494/#2553). Every event reaches this install across a chain of configuration that
        // nothing re-verifies — a GitHub webhook registration, a `WebhookInbox` allowlist slot, an
        // HMAC secret that must be byte-identical on both ends — and every joint of it fails
        // SILENTLY: an unlisted inbox target answers 404 exactly like a wrong URL, a mismatched
        // secret still answers 2xx and drops the delivery. An install whose event channel is dead
        // therefore looks EXACTLY like an install that is up to date: healthy pods, no errors, and
        // a check that simply never runs. memex sat three builds behind for 7 h in that state with
        // nothing in the product able to say so.
        //
        // So the safety net is not the removed poll returning. A poll DRIVES the update; this
        // BOUNDS how long a broken driver can hide, and it cannot change the roll cadence at all —
        // a safety-net check passes through MinRollInterval like any other. See
        // SelfUpdateOptions.SafetyNetCheckInterval.
        //
        // 🚨 The policy is STATE, not a driver — WithLatestFrom, never Switch. Under Switch every
        // policy emission re-subscribed the watch, which RE-BASELINED it: a publication landing in
        // that window was read as "current state" rather than as a new build and silently swallowed.
        // One long-lived watch, the latest policy read at decision time, is what closes it.
        // (CreatePolicySource emits the default exactly once before the first live read, so the
        // startup pass always has a policy to run under.)
        // The policy is shared STATE with a replayed latest value, connected for the lifetime of the
        // service. Replay(1)+Connect rather than RefCount: a RefCount would drop to zero between the
        // Take(1) below and the re-subscribe, re-running the seed and re-baselining everything
        // downstream — the very class of bug this restructure exists to remove.
        var policy = CreatePolicySource().Replay(1);

        // 🚨 The first check waits for a policy to exist. CreatePolicySource emits the default only
        // AFTER its async seed, so a startup pass composed with WithLatestFrom alone fires before
        // any policy is available and is silently dropped — no first check at all. Take(1) gates the
        // trigger stream on that first emission; every LATER policy change is picked up without
        // re-subscribing the watch.
        // Four trigger sources:
        //   • the one startup pass,
        //   • a build completion from the platform or ANY module the environment deploys,
        //   • an admin CHANGING the policy — enabling updates must not wait for the next
        //     publication, which could be weeks away. DistinctUntilChanged so a re-emission of the
        //     same policy is not a trigger, and Skip(1) so the replayed CURRENT policy is not one
        //     either (the startup pass already covers it),
        //   • the safety net, so a dead event channel is bounded rather than permanent.
        var checks = policy
            .Take(1)
            .SelectMany(_ => Observable.Merge(
                Observable.Return(SelfUpdateTrigger.Startup),
                BuildCompletionTicks().Do(_ => Interlocked.Increment(ref _buildEventsSeen)),
                SafetyNetTicks(),
                policy.DistinctUntilChanged(content => content.Policy).Skip(1)
                    .Select(_ => SelfUpdateTrigger.PolicyChange)))
            // 🚨 Read the CURRENT policy at decision time — never WithLatestFrom. That operator only
            // pairs once its secondary has produced, and the startup trigger fires synchronously on
            // subscribe, so whether the first check survives came down to Rx's internal subscribe
            // ordering: it silently dropped the startup pass. policy is Replay(1), so Take(1) yields
            // the latest value immediately and deterministically.
            .SelectMany(trigger => policy.Take(1).Select(content => (trigger, content)))
            .SelectMany(check => RunOnce(check.content)
                // wedges-to-zero: a check error (ACR outage, k8s 403) is a VERDICT, not a silence,
                // and the watch stays live. No outer .Retry — that would be a resubscribe storm.
                .Catch((Exception ex) => Observable.Return(SelfUpdateVerdict.CheckFailed(ex)))
                // 🚨 THE POSTCONDITION, asserted rather than hoped for. Every branch of RunOnce
                // names its outcome, so this should be unreachable — and that is exactly why it is
                // here. The defect this whole restructure fixes was two bare `Where` clauses that
                // dropped a check on the floor, and the only thing that stops a third one being
                // added is making "produced nothing" itself an outcome that gets reported.
                .DefaultIfEmpty(SelfUpdateVerdict.NoOutcome())
                .Take(1)
                .SelectMany(verdict => ReportCheck(check.trigger, verdict)))
            .SubscribeOn(TaskPoolScheduler.Default)
            .Subscribe(
                _ => _checksReported.OnNext(Unit.Default),
                ex => _logger?.LogError(ex, "[SelfUpdate] update watch terminated unexpectedly."));

        _subscription = new CompositeDisposable(checks, policy.Connect());
        return Task.CompletedTask;
    }

    /// <summary>
    /// One emission per CHECK that has been evaluated AND reported (#2777). An instance field —
    /// its lifetime is this service's — never static.
    ///
    /// <para>🚨 It exists because the alternative is a test bounding a POLL. This service is
    /// event-driven but its first check is still work that has to happen, so a test that waits
    /// "up to 30 s for the held state to appear" is really waiting for the first check to run:
    /// on a loaded shard it may simply not have, and the test then reports the absence of a
    /// verdict as a wrong verdict. That is the same false-RED shape as #2793, and the answer is
    /// the same — publish the condition, do not infer it from a bound. Production never
    /// subscribes; nothing here changes what a check does or when it runs.</para>
    /// </summary>
    private readonly Subject<Unit> _checksReported = new();

    /// <summary>
    /// Test seam (<c>InternalsVisibleTo</c>): fires once per check whose verdict has been written.
    /// Await the FIRST emission to know the service has evaluated at least once, then assert on the
    /// state it produced — never race a timer against it.
    /// </summary>
    protected internal IObservable<Unit> ChecksReported => _checksReported;

    /// <summary>
    /// The safety net (#2494): a bounded liveness floor on the CHECK, never on the roll.
    ///
    /// <para>Off entirely when <see cref="SelfUpdateOptions.SafetyNetCheckInterval"/> is zero or
    /// negative, which is the shape a deployment that genuinely wants event-only can opt into
    /// — deliberately, in configuration, where it is visible.</para>
    ///
    /// <para>Virtual for the same reason the other three seams are: a test must be able to drive
    /// the safety-net path without waiting out an hour.</para>
    /// </summary>
    protected virtual IObservable<SelfUpdateTrigger> SafetyNetTicks() =>
        _options.SafetyNetCheckInterval <= TimeSpan.Zero
            ? Observable.Never<SelfUpdateTrigger>()
            : Observable.Interval(_options.SafetyNetCheckInterval)
                .Select(_ => SelfUpdateTrigger.SafetyNet);

    /// <summary>
    /// 🚨 Reports the outcome of ONE check — the single reporting site, and the thing whose absence
    /// #2553 measured as "ZERO SelfUpdate log lines in 6.7 h while three builds behind".
    ///
    /// <para>It reports TWICE, to two audiences with different failure modes:</para>
    /// <list type="number">
    /// <item>a LOG line, one per check, naming the trigger and the verdict; and</item>
    /// <item>a durable stamp on <c>Admin/UpdatePolicy</c>
    /// (<see cref="UpdatePolicyContent.LastCheckedAt"/> / <c>LastCheckVerdict</c> /
    /// <c>LastCheckTrigger</c>).</item>
    /// </list>
    ///
    /// <para>Both, because the log alone was not enough and the reason is instructive: every line
    /// this service had to say was <c>LogInformation</c>, the portal image caps its whole logger
    /// prefix at <c>Warning</c>, and no deployment had added the category. So the mechanism was
    /// working exactly as designed and reporting into a void — the one thing an operator could
    /// read, the Updates tab, showed "No newer version detected yet" because that is what an
    /// install that has never checked and an install that checked and found nothing both look
    /// like. A node write does not depend on a log level anyone remembered to set.</para>
    ///
    /// <para>🚨 THE DEAD-EVENT-CHANNEL REPORT is the one line that goes to Warning: a check woken
    /// by the SAFETY NET, on an install that has seen no build-completion event at all, that
    /// nevertheless FOUND a newer release. Each clause matters. "No events yet" alone is not
    /// alarming — an install whose modules rarely build legitimately sees none for days — and
    /// warning on it would train people to ignore the line. "A release was waiting and nothing
    /// told us" is the #2494 symptom exactly, and it is only ever observable from here.</para>
    ///
    /// <para>Bookkeeping, never a gate (#1020): the stamp is best-effort and a failed write is a
    /// warning that names itself, never something that can abort a check or hold a roll.</para>
    /// </summary>
    private IObservable<Unit> ReportCheck(SelfUpdateTrigger trigger, SelfUpdateVerdict verdict) =>
        Observable.Defer(() =>
        {
            var eventChannelSilent = trigger == SelfUpdateTrigger.SafetyNet
                && Volatile.Read(ref _buildEventsSeen) == 0
                && verdict.FoundNewerRelease;

            if (eventChannelSilent)
                _logger?.LogWarning(
                    "[SelfUpdate] check ({Trigger}): {Verdict} 🚨 This install has received NO "
                    + "build-completion event since it started, yet a newer release was waiting — "
                    + "only the safety net found it. The event channel (GitHub webhook → "
                    + "WebhookInbox target → Admin/_Build) is not reaching this instance; every "
                    + "joint of it fails silently, so check them rather than this service.",
                    trigger, verdict.Message);
            else if (verdict.Outcome is SelfUpdateOutcome.NoOutcome
                     or SelfUpdateOutcome.CheckFailed)
                _logger?.LogWarning("[SelfUpdate] check ({Trigger}): {Verdict}", trigger, verdict.Message);
            else
                _logger?.LogInformation("[SelfUpdate] check ({Trigger}): {Verdict}", trigger, verdict.Message);

            return RecordCheck(trigger, verdict)
                .Catch((Exception ex) =>
                {
                    _logger?.LogWarning(ex,
                        "[SelfUpdate] could not record the check verdict on {Node}; the check itself "
                        + "still ran and its verdict is above.", UpdatePolicyNodeType.NodePath);
                    return Observable.Return(Unit.Default);
                });
        });

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        // Complete the seam so a subscriber awaiting a check that will now never come is
        // released rather than left hanging on a stopped service.
        _checksReported.OnCompleted();
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
    /// <item>The live node stream, and NOTHING ELSE. 🚨 The configured default is never emitted
    /// here (MeshWeaver#2731/#2797): it used to be prepended once, before the first live emission,
    /// and that one synthetic value drove a full <see cref="RunOnce"/> — ACR listing, candidate
    /// selection and the Deployment PATCH — under a policy the install had never set. On a pinned
    /// (<c>None</c>) install, EVERY pod (re)start therefore rolled the portal to the newest tag,
    /// with the persisted <c>None</c> arriving a second later as a <c>PolicyChange</c> that
    /// dutifully logged "updates are disabled" AFTER the roll had been issued. Stage 1 guarantees
    /// the node EXISTS, so the live read is the first emission on its own — the poller now waits
    /// for a policy it has actually read instead of guessing one. A stream fault retries inside
    /// the RetryWhen, so a retry re-establishes the read silently (Copilot review on #611).</item>
    /// </list>
    /// Retries are delayed, Rx-composed resubscribes at the polling cadence — not a hot retry loop,
    /// not a watchdog.
    ///
    /// <para>🚨 <b>Nothing de-duplicates the CONTENT here, and that is deliberate.</b> This source
    /// used to end in <c>DistinctUntilChanged(c =&gt; (c.Policy, c.RequireCiGreen))</c> — a leftover
    /// from the <c>Switch</c>-based shape, whose comment still said "re-switch only on a REAL policy
    /// change" long after the <c>Switch</c> was gone. With the watch moved OUT of the policy stream
    /// there is nothing left to re-drive, so the operator no longer prevented a resubscribe; it
    /// FILTERED the content, and this stream is also what <c>StartAsync</c> reads AT DECISION TIME
    /// (<c>policy.Take(1)</c> off the <c>Replay(1)</c>). Every field other than those two was
    /// therefore pinned to the first emission for the life of the pod: a combo verdict landed by
    /// <c>mw-combo-verify</c> after startup was invisible to the gate that exists to honour it, and
    /// the poller rolled to an image it had been told this instance's modules cannot run. The
    /// trigger stream keeps its OWN <c>DistinctUntilChanged(content =&gt; content.Policy)</c>, so a
    /// content change that is not a policy change still triggers nothing — including this service's
    /// own <c>LastCheckedAt</c>/<c>LastCheckVerdict</c> bookkeeping writes, which is why letting
    /// them through cannot loop.</para>
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
                // The gate has opened: from here a fault costs freshness, not the feature.
                .Do(_ => Interlocked.Exchange(ref _policyEstablished, 1))
                .RetryWhen(ResubscribeAfterRetryInterval("policy stream")));
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
            // 🚨 An ABSENT node is not a policy (MeshWeaver#2731/#2797). UpdatePolicyContent.Policy
            // defaults to Continuous and UpdatePolicyKind.Continuous is enum 0, so parsing a null
            // node yields "roll to the newest tag" — the exact verdict a pinned install must never
            // reach. Stage 1 has already guaranteed the node exists, so a null here is the stream
            // not having loaded it yet: wait for the real one rather than deciding without it.
            .Where(node => node is not null)
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
    protected virtual IObservable<SelfUpdateTrigger> BuildCompletionTicks() =>
        Observable
            .Defer(() => NewBuildEventsAcross(_hub
                .GetQuery($"SelfUpdate.BuildTrigger:{_hub.Address}",
                    // 🚨 PATH-SCOPED (BuildCompletion.WatchQuery). Build records live in the ADMIN
                    // partition, which an UNSCOPED query does not reach — the bare
                    // `nodeType:BuildCompletion` this used to pass returned EMPTY however many
                    // records existed and however often they were written, so this watch never
                    // ticked and the install silently stopped self-updating. (Not a satellite rule:
                    // GitHubSyncConfig satellites in ordinary partitions ARE returned unscoped.)
                    BuildCompletion.WatchQuery)))
            .Throttle(_options.EventCoalesceWindow)
            .Select(_ => SelfUpdateTrigger.BuildCompletion)
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
            // 🚨 The cadence depends on whether a policy has EVER been read, because the two states
            // cost completely different things. Before the first read the poller is INERT — every
            // trigger, the safety net included, is gated on that first emission — so a fault there
            // takes the whole feature out; after it, the last policy is retained and checks keep
            // running, so a fault costs only freshness and the long interval is right. Pacing both
            // at RetryInterval is what left memex-cloud ~400 builds behind (see
            // SelfUpdateOptions.PolicyEstablishRetryInterval for the measurement).
            var established = Volatile.Read(ref _policyEstablished) == 1;
            var delay = _options.PolicyRetryDelay(established);
            _logger?.LogWarning(ex,
                "[SelfUpdate] {Stage} faulted; re-establishing in {Interval} ({State}).",
                stage, delay,
                established
                    ? "a policy has been read — checks continue against it meanwhile"
                    : "NO policy read yet — every check is gated on it, so this install is inert until it succeeds");
            return Observable.Timer(delay);
        });

    /// <summary>One evaluation: list tags → pick target per policy → gate target &gt; current →
    /// record availability → (if armed) patch the workloads.
    ///
    /// <para>🚨 Returns a <see cref="SelfUpdateVerdict"/>, not <c>Unit</c>, and that is the whole
    /// #2553 fix rather than a style change. As <c>IObservable&lt;Unit&gt;</c> this method had two
    /// ways to complete having said NOTHING — a policy of <c>None</c> and "no candidate is newer"
    /// were both bare Rx <c>Where</c> clauses upstream — and an empty completion is
    /// indistinguishable from a check that never ran. Every exit now names its outcome, so the
    /// caller has something to log and something to record.</para></summary>
    private IObservable<SelfUpdateVerdict> RunOnce(UpdatePolicyContent policy)
    {
        // 🚨 `None` means never update, and it used to be a `Where` in the trigger pipeline: the
        // single most silent path in the service. An install deliberately pinned by an
        // administrator and an install whose updater is broken produced byte-identical evidence
        // (none), which is the exact confusion #2553 was filed about. It is a DECISION, so it says
        // so — and it still costs no ACR call.
        if (policy.Policy == UpdatePolicyKind.None)
            return Observable.Return(SelfUpdateVerdict.UpdatesDisabled());

        return _http.Invoke(ct => _acr.ListTagsAsync(_options.PortalRepository, ct))
            // 🚨 The BEST ROLLABLE release, not merely the newest one. A target is only rollable if a
            // sealed content bake exists for the identity that exact image resolves to, and picking
            // the newest tag and stopping means one unbaked release freezes the instance FOREVER:
            // it holds, the next platform build produces another unbaked tag, the bake publishes yet
            // another identity, and the two never meet. memex sat on 3.0.0-rc6 held against
            // 3.0.0-rc7.ci.4928 while three separate bakes published three other identities — every
            // job green throughout, nothing to point at.
            //
            // So walk the candidates newest-first and take the first that the gate accepts. Still
            // never backwards (IsNewer), still never into a boot storm (unsealed candidates are
            // SKIPPED, not forced) — but a not-yet-baked head no longer blocks the releases behind
            // it, and an instance always advances to the best release that can actually serve.
            .Select(tags => (
                Listed: tags.Count,
                Candidates: VersionSelect
                    .PickTargets(tags, policy.Policy, policy.RequireCiGreen)
                    .Where(tag => VersionSelect.IsNewer(tag, ShippedReleaseSeed.InstalledPlatformVersion))
                    .ToArray()))
            .SelectMany(listing => listing.Candidates.Length == 0
                // 🚨 "We asked and the answer was no" — the OTHER formerly-silent exit, and the one
                // that made a stalled install unfalsifiable from outside. This is the normal, happy
                // outcome of most checks, and it has to be SAID: it is the only thing that
                // distinguishes a healthy up-to-date install from one whose checker is dead.
                ? Observable.Return(SelfUpdateVerdict.NoNewerRelease(
                    listing.Listed, ShippedReleaseSeed.InstalledPlatformVersion))
                : FirstRollable(policy, listing.Candidates)
                    .SelectMany(target => RecordAvailable(target!)
                        // 🚨 BOOKKEEPING, NOT A GATE (#1020). RecordAvailable writes two cosmetic fields
                        // (LatestAvailableTag / CheckedAt) that drive the admin tab; the ROLL-FORWARD does not
                        // depend on them. Chaining Apply after it with .SelectMany made a failed status write
                        // abort the update: in production the Admin/UpdatePolicy node hub was unreachable (its
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
                        // IgnoreElements keeps the record's own emissions out of the verdict
                        // stream while preserving Concat's ordering — the record still lands FIRST,
                        // and the Select below is unreachable by construction.
                        .IgnoreElements()
                        .Select(_ => SelfUpdateVerdict.NoOutcome())
                        .Concat(GateThenApply(policy, target!))));
    }

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
    /// <summary>
    /// The newest candidate the availability gate accepts — or, when it accepts none, the newest
    /// candidate anyway so the caller records the hold against the release an operator is actually
    /// waiting for.
    ///
    /// <para>Sequential and LAZY by construction (<c>Concat</c> over a deferred enumerable): the
    /// common case asks the gate exactly once, about the head. Only a held head costs a second
    /// question, and only about the release below it.</para>
    ///
    /// <para>🚨 Returning the newest on total failure is deliberate, not a fallback that rolls
    /// something unwanted: the caller re-gates whatever comes back, so an all-held list still ends
    /// in a HOLD — with the newest tag's reason, which is the one that explains why the fleet is
    /// not moving. Reporting the OLDEST candidate's reason instead would be true and useless.</para>
    /// </summary>
    private IObservable<string?> FirstRollable(UpdatePolicyContent policy, string[] candidates)
    {
        if (candidates.Length == 0)
            return Observable.Return<string?>(null);

        var gate = ResolveAvailabilityGate();
        var combo = ResolveComboGate();
        // Neither gate wired: preserve today's behaviour exactly — the head, and GateThenApply
        // reports the not-wired case itself.
        if (gate is null && combo is null)
            return Observable.Return<string?>(candidates[0]);

        return candidates
            .Select(tag => Observable.Defer(() => Rollable(gate, combo, policy, tag)))
            .Concat()
            .FirstOrDefaultAsync(x => x.rollable)
            .Select(x => x.tag ?? candidates[0])
            // A gate that faults must not kill the tick: fall back to the head and let
            // GateThenApply take the fail-safe hold it already knows how to take.
            .Catch((Exception _) => Observable.Return<string?>(candidates[0]));
    }

    /// <summary>
    /// Whether ONE candidate survives the walk: the availability gate accepts it AND the combo gate
    /// has not already CONDEMNED it.
    ///
    /// <para>🚨 The combo half reads only what is RECORDED
    /// (<see cref="ComboVerificationGate.Recorded"/>) — a pure read of the policy content this tick
    /// already holds, so the walk costs neither an extra mesh touch nor a docker run per candidate.
    /// A verdict is PRODUCED exactly once, in <see cref="GateThenApply"/>, about the candidate
    /// actually chosen; if that comes back Red the tick holds, and the now-recorded Red makes the
    /// very next walk step past it. The two halves converge in one extra check instead of paying a
    /// full gate run per tag.</para>
    ///
    /// <para>🚨 Only a REFUSAL removes a candidate. One with no verdict, or one whose verdict could
    /// not answer, is neither preferred nor condemned here — treating "unknown" as disqualifying
    /// would empty the candidate list on every instance in the fleet, which is the freeze this gate
    /// exists to avoid causing, not to cause.</para>
    /// </summary>
    private static IObservable<(string tag, bool rollable)> Rollable(
        ReleaseAvailabilityService? gate,
        ComboVerificationGate? combo,
        UpdatePolicyContent policy,
        string tag)
    {
        var notCondemned = combo is null || !combo.Recorded(policy, tag).Refuses;
        return (gate is null
                ? Observable.Return(true)
                : gate.IsUpdatable(tag).Select(verdict => verdict.IsUpdatable))
            .Select(available => (tag, rollable: available && notCondemned));
    }

    private IObservable<SelfUpdateVerdict> GateThenApply(UpdatePolicyContent policy, string target) =>
        Observable.Defer(() =>
        {
            var gate = ResolveAvailabilityGate();
            if (gate is null)
                return GateNotWired(policy, target);

            return gate.IsUpdatable(target)
                .SelectMany(verdict =>
                {
                    if (verdict.IsUpdatable)
                    {
                        if (verdict.NotEnforcedReason is { } notEnforced)
                            _logger?.LogInformation(
                                "[SelfUpdate] release-availability gate not enforced for {Tag}: {Reason}",
                                target, notEnforced);
                        // 🚨 The availability gate answered "an artifact exists". The combo gate
                        // answers the question that artifact cannot: whether the candidate's
                        // assemblies can still serve the module content this instance has landed.
                        // Both have to clear before anything is patched.
                        return ComboThenApply(policy, target);
                    }

                    return RecordHold(target, verdict).Catch(HoldWriteFailed(target))
                        .IgnoreElements()
                        .Select(_ => SelfUpdateVerdict.NoOutcome())
                        .Concat(Observable.Return(
                            SelfUpdateVerdict.Held(target, verdict.HoldReason)));
                });
        });

    /// <summary>
    /// 🚨 <b>The combo gate (#2274), the last thing between a rollable tag and a pod restart.</b>
    ///
    /// <para>"A sealed bake exists for it" was never the whole precondition. The candidate must also
    /// be able to SERVE the modules this instance has actually landed — and it can fail to while
    /// every artifact is present, because a framework-identity change invalidates the assembly
    /// cache by design and an optional parameter added to a record's primary constructor replaces
    /// the signature. memex.systemorph.com was trapped between two failing states for exactly that
    /// reason: rolling forward aborted the host with a <c>MissingMethodException</c>, and so did
    /// re-fetching the bundles, from the other side.</para>
    ///
    /// <para><b>The three verdicts are never conflated</b> (<see cref="ComboClearance"/>):</para>
    /// <list type="bullet">
    /// <item><b>Green</b> ⇒ cleared. The roll proceeds and the clearance is logged with its
    /// caveats.</item>
    /// <item><b>Red</b> ⇒ REFUSED. The roll is held, every failing module is NAMED on
    /// <c>Admin/UpdatePolicy</c> where the Updates tab reads it, and the refusal is logged at Error.
    /// Re-evaluated from scratch every tick, so a re-verified candidate clears with nothing to
    /// un-stick by hand.</item>
    /// <item><b>NotVerifiable — and its sibling, no verdict at all</b> ⇒ NEITHER. It does not clear
    /// (that would reproduce the outage, treating "we could not find out" as "all clear") and it
    /// does not refuse (that would freeze every instance the moment this shipped, since producing a
    /// verdict needs docker a pod does not have). The roll rests on the other gates, and the fact is
    /// recorded on the check verdict and logged at Warning when a patch is actually issued — an
    /// UNVERIFIED roll is a state an operator can see, never a silent one.</item>
    /// </list>
    /// </summary>
    private IObservable<SelfUpdateVerdict> ComboThenApply(UpdatePolicyContent policy, string target)
    {
        var combo = ResolveComboGate();
        return (combo is null
                // Not registered is not a pass: it folds to NotVerified, which grants nothing.
                ? Observable.Return(ComboVerificationGate.NotRegistered(target))
                : combo.Clearance(policy, target, _options.PortalImage(target)))
            .SelectMany(clearance =>
            {
                if (clearance.Refuses)
                {
                    _logger?.LogError(
                        "[SelfUpdate] HOLDING update to {Tag} (staying on {Current}) — {Reason}",
                        target, ShippedReleaseSeed.InstalledPlatformVersion, clearance.Reason);
                    return RecordHold(target, ComboHold(clearance))
                        .Catch(HoldWriteFailed(target))
                        .IgnoreElements()
                        .Select(_ => SelfUpdateVerdict.NoOutcome())
                        .Concat(Observable.Return(
                            SelfUpdateVerdict.ComboBlocked(target, clearance.Reason)));
                }

                if (clearance.IsCleared)
                    _logger?.LogInformation(
                        "[SelfUpdate] combo gate cleared {Tag}: {Reason}", target, clearance.Reason);

                // Clearing is unconditional, exactly as on the availability path: a previous hold
                // that no longer applies must disappear from the admin tab the moment it is
                // resolved.
                return RecordHold(target, null).Catch(HoldWriteFailed(target))
                    .IgnoreElements()
                    .Select(_ => SelfUpdateVerdict.NoOutcome())
                    .Concat(Apply(target).Select(verdict => Qualify(verdict, clearance)));
            });
    }

    /// <summary>
    /// Carries a non-clearing combo answer onto the check verdict — the durable half — and raises
    /// the log to Warning at the one moment the risk is actually TAKEN: a patch issued without
    /// clearance. A deferred or detect-only check took no risk, so it says so without shouting.
    /// </summary>
    private SelfUpdateVerdict Qualify(SelfUpdateVerdict verdict, ComboClearance clearance)
    {
        if (clearance.IsCleared)
            return verdict;
        if (verdict.Outcome == SelfUpdateOutcome.Applied)
            _logger?.LogWarning(
                "[SelfUpdate] rolled {Tag} WITHOUT combo clearance — {Reason}",
                clearance.CandidateTag, clearance.Reason);
        return verdict.Unverified(clearance.Reason);
    }

    /// <summary>
    /// The combo refusal expressed as the hold the admin surfaces already render: one blocker per
    /// FAILING MODULE, so <c>HeldReason</c> names them all, and NOT indeterminate — the gate looked
    /// and found an incompatibility, which is a candidate to re-verify rather than an availability
    /// incident to fix.
    /// </summary>
    private static UpdatabilityVerdict ComboHold(ComboClearance clearance) =>
        new(false,
            [
                .. (clearance.Verdict?.FailedModules ?? []).Select(module =>
                    new PackageAvailability(
                        module.ModuleId,
                        PackageAvailabilityKind.ComboVerificationFailed,
                        module.Failures.Count > 0 ? module.Failures[0] : clearance.Reason)),
            ],
            clearance.Reason);

    /// <summary>
    /// 🚨 <b>No gate is registered on this host — so the roll HOLDS.</b>
    ///
    /// <para>This branch used to log at Information and roll anyway. That is the trap this whole
    /// area keeps falling into: a gate that cannot run must never look like a gate that passed. The
    /// gate answers "does every package this environment deploys have a usable artifact for the
    /// target release"; a host with no gate registered has not answered it — it has failed to ask.
    /// An unwired check is the absence of a verdict, not a passing one, and #1754's own rule says
    /// "cannot determine" is not "clear to proceed".</para>
    ///
    /// <para>🚨 <b>But only where there is something to verify.</b> The hold is scoped by
    /// <see cref="ReleaseAvailabilityService.NotApplicableReason"/>: on a deployment that consumes
    /// no CI bakes a registered gate would itself answer
    /// <see cref="UpdatabilityVerdict.NotEnforced"/>, so its ABSENCE is not an unanswered question
    /// — it is the same answer arrived at from configuration. Failing closed on that state would
    /// freeze an environment the gate was never going to protect, and would brick a first-ever
    /// roll (the manual button honours the same verdict), which is the classic cost of a
    /// fail-closed rule drawn one state too wide.</para>
    ///
    /// <para>It is a hold with all the properties that make a hold safe rather than a freeze: it is
    /// recorded on the policy node so the Updates tab NAMES it, it is logged at Error (a wiring
    /// defect nobody sees is how an environment sits un-updated for weeks), and it is re-evaluated
    /// from scratch on every tick — registering the gate clears it with no manual un-sticking.</para>
    ///
    /// <para>The deliberate escape hatch is <see cref="SelfUpdateOptions.AllowUnverifiedRoll"/>:
    /// set it in configuration, where it is visible, and the roll proceeds while saying so at
    /// Warning on every tick. It can never waive a gate that DID run.</para>
    /// </summary>
    private IObservable<SelfUpdateVerdict> GateNotWired(UpdatePolicyContent policy, string target) =>
        Observable.Defer(() =>
        {
            // 🚨 "CANNOT VERIFY" AND "VERIFIED AS NOTHING TO VERIFY" ARE DIFFERENT STATES, and only
            // the first may hold. This branch originally held on both, which swept in a case that
            // is legitimately clear: a deployment that consumes no CI bakes already compiles its
            // content at every boot, so a REGISTERED gate would have answered NotEnforced for it.
            // The gate being absent on such a deployment tells you nothing new — holding on it
            // would freeze an environment the gate was never going to protect, and (since the
            // manual roll honours the same verdict) an install with no roll history could never
            // take its first update at all.
            //
            // The applicability rule is read from CONFIGURATION through the gate's own static, so
            // the answer here and the answer a registered gate would give cannot drift apart.
            if (ReleaseAvailabilityService.NotApplicableReason(ResolveConfiguration())
                is { } notApplicable)
            {
                _logger?.LogInformation(
                    "[SelfUpdate] release-availability gate not enforced for {Tag}: {Reason} "
                    + "(no gate is registered on this host either, which changes nothing here — "
                    + "there is nothing for it to check against).",
                    target, notApplicable);
                // 🚨 The COMBO gate still runs. "This deployment consumes no CI bakes" answers
                // the availability question and nothing else: an instance can carry landed modules
                // whose content the candidate cannot serve whatever its bundle root says, which is
                // precisely the state #2274 was filed about.
                return ComboThenApply(policy, target);
            }

            if (_options.AllowUnverifiedRoll)
            {
                _logger?.LogWarning(
                    "[SelfUpdate] rolling {Tag} UNVERIFIED — no release-availability gate is "
                    + "registered on this host and '{Key}:{Property}' is set, so nothing checked "
                    + "whether the packages this environment deploys have artifacts for it. Unset "
                    + "that key to make the missing gate a hold again.",
                    target, SelfUpdateOptions.SectionName, nameof(SelfUpdateOptions.AllowUnverifiedRoll));
                // 🚨 AllowUnverifiedRoll waives the AVAILABILITY gate that could not run. It is not
                // a waiver of a gate that DID run, and the combo gate's Red is exactly that — so it
                // still refuses here. A key that could wave away a produced refusal would be the
                // skip-trapdoor this whole area exists to keep out.
                return ComboThenApply(policy, target);
            }

            var verdict = UpdatabilityVerdict.Unavailable(
                "no release-availability gate is registered on this host, so nothing could check "
                + "whether the packages this environment deploys have usable artifacts for this "
                + $"release — that is a hold, not clearance to proceed. Register "
                + $"{nameof(ReleaseAvailabilityService)} (it is wired by AddSelfUpdate), or set "
                + $"'{SelfUpdateOptions.SectionName}:{nameof(SelfUpdateOptions.AllowUnverifiedRoll)}'"
                + " to roll unverified on purpose.");

            _logger?.LogError(
                "[SelfUpdate] HOLDING update to {Tag} (staying on {Current}) — {Reason}",
                target, ShippedReleaseSeed.InstalledPlatformVersion, verdict.HoldReason);

            return RecordHold(target, verdict).Catch(HoldWriteFailed(target))
                .IgnoreElements()
                .Select(_ => SelfUpdateVerdict.NoOutcome())
                .Concat(Observable.Return(SelfUpdateVerdict.Held(target, verdict.HoldReason)));
        });

    /// <summary>
    /// The release-availability gate, resolved from the mesh's services. Virtual: the third
    /// documented injection seam, so a test can pin what the poller DOES with a verdict without
    /// also staging an artifact store (the verdict itself is pinned against a real one elsewhere).
    /// </summary>
    protected virtual ReleaseAvailabilityService? ResolveAvailabilityGate() =>
        _hub.ServiceProvider.GetService<ReleaseAvailabilityService>();

    /// <summary>
    /// The combo gate (#2274), resolved from the mesh's services — the fourth documented injection
    /// seam, so a test can pin what the poller DOES with a combo verdict without staging docker and
    /// a module repository.
    ///
    /// <para>🚨 A null here is NOT a pass: <see cref="ComboThenApply"/> folds it into
    /// <see cref="ComboVerificationGate.NotRegistered"/>, which grants no clearance. AddSelfUpdate
    /// registers the gate unconditionally, so no host in this repo can reach the state at all.</para>
    /// </summary>
    protected virtual ComboVerificationGate? ResolveComboGate() =>
        _hub.ServiceProvider.GetService<ComboVerificationGate>();

    /// <summary>
    /// The host's configuration, resolved from the mesh's services. Virtual for the same reason
    /// <see cref="ResolveAvailabilityGate"/> is: whether the release-availability gate APPLIES to
    /// this deployment is read from configuration, so a test must be able to present a
    /// bake-consuming deployment and a bake-free one without standing up two meshes for a one-key
    /// difference.
    /// </summary>
    protected virtual IConfiguration? ResolveConfiguration() =>
        _hub.ServiceProvider.GetService<IConfiguration>();

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
        // 🚨 RunAsSystem, never `Observable.Using(AccessContextScope.AsSystem, …)` (#1444/#1790):
        // `AsSystem(x)` IS `x.ImpersonateAsSystem()`, so the helper hides the shape. The write here
        // is CROSS-HUB, which is the worst case for `Using` — Rx disposes the scope when the inner
        // observable terminates, i.e. on the Admin partition hub's response thread, while the
        // subscriber (the self-update poller's tick) keeps `system-security` latched. RunAsSystem
        // opens and closes inside one Subscribe; the Update is still ISSUED as System, which is what
        // the cross-hub patch stamps.
        return accessService.RunAsSystem(
            () => _hub.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
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
    private IObservable<SelfUpdateVerdict> Apply(string target) =>
        Observable.Defer(() =>
        {
            if (!_updater.CanPatch)
                return Observable.Return(SelfUpdateVerdict.DetectOnly(target));

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
                return _http.Invoke(ct => _updater.PatchToVersionAsync(target, ct))
                    .Select(_ => SelfUpdateVerdict.Applied(
                        target, ShippedReleaseSeed.InstalledPlatformVersion, null));

            return _http.Invoke(ct => _updater.LastRolledAtAsync(ct)).SelectMany(lastRolledAt =>
            {
                var since = lastRolledAt is null ? (TimeSpan?)null : DateTimeOffset.UtcNow - lastRolledAt.Value;
                if (since is { } elapsed && elapsed < _options.MinRollInterval)
                    return Observable.Return(
                        SelfUpdateVerdict.Deferred(target, elapsed, _options.MinRollInterval));

                return _http.Invoke(ct => _updater.PatchToVersionAsync(target, ct))
                    .Select(_ => SelfUpdateVerdict.Applied(
                        target, ShippedReleaseSeed.InstalledPlatformVersion, lastRolledAt));
            });
        });

    /// <summary>
    /// Stamps the outcome of one check on the policy node, as System — the durable half of
    /// <see cref="ReportCheck"/>, and the one that survives a deployment forgetting to set a log
    /// level. Virtual so a test can fault it and prove the check itself is unaffected.
    ///
    /// <para>🚨 It cannot feed itself. The check trigger reads
    /// <c>policy.DistinctUntilChanged(c =&gt; c.Policy)</c> and the policy source itself is
    /// <c>DistinctUntilChanged((Policy, RequireCiGreen))</c>, so a write that touches only these
    /// three bookkeeping fields emits nothing downstream and can never schedule another check —
    /// the reconcile-feeds-itself write storm (#223) is structurally excluded rather than avoided
    /// by convention.</para>
    /// </summary>
    protected virtual IObservable<Unit> RecordCheck(SelfUpdateTrigger trigger, SelfUpdateVerdict verdict)
    {
        var accessService = _hub.ServiceProvider.GetService<AccessService>();
        var jsonOptions = _hub.JsonSerializerOptions;
        // RunAsSystem, never `Observable.Using(AccessContextScope.AsSystem, …)` — see
        // RecordHold below (#1444/#1790).
        return accessService.RunAsSystem(
            () => _hub.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                .Update(node =>
                {
                    var cur = UpdatePolicyNodeType.ParseContent(node.Content, jsonOptions);
                    return node with
                    {
                        Content = cur with
                        {
                            LastCheckedAt = DateTimeOffset.UtcNow,
                            LastCheckVerdict = verdict.Message,
                            LastCheckTrigger = trigger.ToString(),
                        },
                    };
                })
                .Select(_ => Unit.Default));
    }

    /// <summary>Record the newest available tag on the policy node (as System). Drives the admin tab
    /// and the detect-and-notify path. Touches only the bookkeeping fields; preserves Policy.
    /// Virtual: the second fault-injection seam for the resilience test (alongside
    /// <see cref="ReadPolicyStream"/>) — it is where the #1020 prod stall surfaced.</summary>
    protected virtual IObservable<Unit> RecordAvailable(string tag)
    {
        var accessService = _hub.ServiceProvider.GetService<AccessService>();
        var jsonOptions = _hub.JsonSerializerOptions;
        // RunAsSystem, never `Observable.Using(AccessContextScope.AsSystem, …)` — see
        // RecordHold above (#1444/#1790).
        return accessService.RunAsSystem(
            () => _hub.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
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
