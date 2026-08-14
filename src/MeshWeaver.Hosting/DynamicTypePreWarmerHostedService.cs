using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Mesh.Diagnostics;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Kicks <see cref="DynamicTypePreWarmer.WarmDynamicTypes"/> once, in the background, after
/// the host has fully started (so the Orleans silo is up and grains can activate).
///
/// <para>Registered via <see cref="PreWarmServiceCollectionExtensions.AddDynamicTypePreWarming"/>
/// — opt-in, portal-only. It is NOT wired into the shared <see cref="MeshHostApplicationBuilder"/>
/// so tests and non-portal hosts never pay for a startup warm-up they don't want.</para>
///
/// <para><b>Never blocks host startup or readiness.</b> <see cref="StartAsync"/> returns
/// immediately; the warm-up is launched from the <c>ApplicationStarted</c> callback and runs
/// on a background Rx subscription. The subscription is torn down on shutdown.</para>
///
/// <para><b>OFF by default</b> (<c>PreWarm:DynamicTypes</c> config, default <c>false</c>): the
/// warm-up enumerates EVERY NodeType across EVERY partition and activates + compiles each dynamic
/// one — on a mesh with many partitions that is a boot-time subscribe/compile storm that starves
/// the hubs (and, multi-silo, the Orleans membership probes — the "I have been told I am dead"
/// restart loop on memex, 2026-07-22). Correctness never needs it: first access compiles lazily
/// via <c>NodeTypeEnrichmentHelpers.WaitForCompileSettled</c>. Opt in only where the
/// first-visitor compile latency actually hurts and the mesh is small enough to warm quietly.</para>
/// </summary>
public sealed class DynamicTypePreWarmerHostedService(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<DynamicTypePreWarmerHostedService> logger) : IHostedService, IDisposable
{
    /// <summary>Config key that opts a deployment into the startup warm-up (default: off).</summary>
    public const string EnabledConfigKey = "PreWarm:DynamicTypes";

    /// <summary>
    /// How many of the costliest NodeTypes the warm-up summary NAMES (issue #1439). Small on
    /// purpose: the point is to identify the compile units worth looking at, not to log a table —
    /// and the per-type lines already carry every type's own cost for anyone who wants the full
    /// distribution. Not configurable; a knob here would be one more thing to get wrong for a
    /// diagnostic whose whole value is that it is always on.
    /// </summary>
    private const int SlowestReported = 5;

    /// <summary>
    /// Config key overriding the per-type warm budget, as a <see cref="TimeSpan"/> string
    /// (e.g. <c>"00:00:30"</c>). Default: <see cref="DynamicTypePreWarmer.DefaultPerTypeBudget"/>.
    /// The budget only bites on types that never settle — a healthy compile is seconds — so this
    /// knob bounds how long a WEDGED type can hold the sweep, per type, without a code change:
    /// on 2026-08-11 (memex-cloud) every remaining compile timed out cross-silo at the full
    /// default 5 minutes and 67 pending types priced the sweep at ~5.5 hours, with no way to
    /// shorten it from the outside.
    /// </summary>
    public const string PerTypeBudgetConfigKey = "PreWarm:PerTypeBudget";

    /// <summary>
    /// Config key overriding the pause between types, as a <see cref="TimeSpan"/> string. When the
    /// key is absent the pacing DIFFERENTIATES by what the pod is doing (the initial-bake vs
    /// usual-compile distinction): a pod whose bake GATES readiness serves nobody while it bakes,
    /// so it sweeps at full speed (<see cref="TimeSpan.Zero"/>); an ungated pod is warming in the
    /// background of live traffic, so it keeps the trickle
    /// (<see cref="DynamicTypePreWarmer.BetweenTypes"/>). Measured 2026-08-11: the 2s trickle was
    /// ~5.4 of a 7-minute sweep whose compiles summed to 51s — on a gated pod that pacing was pure
    /// added rollout time.
    /// </summary>
    public const string BetweenTypesConfigKey = "PreWarm:BetweenTypes";

    /// <summary>
    /// Config key overriding whether the sweep runs as ONE LINKED BATCH BUILD (issue #1207):
    /// batched source discovery + direct compiler drive, no per-type hub activations, no
    /// compile-watcher settles, no cross-silo hops.
    ///
    /// <para>Absent, the mode follows the same initial-bake discriminator as
    /// <see cref="BetweenTypesConfigKey"/>: a pod whose bake GATES readiness batches; an ungated
    /// background warm keeps the activation-driven path. This is the ESCAPE HATCH — set
    /// <c>false</c> to force the activation sweep on a gated pod (measured 18m55s for a 237-type
    /// fleet where the batch takes ~60s), or <c>true</c> to batch an ungated warm-up. See the note
    /// in <c>KickWarmup</c> for why the default was walked back once and what had to be proven
    /// before restoring it.</para>
    /// </summary>
    public const string BatchBakeConfigKey = "PreWarm:BatchBake";

    private IDisposable? _warmSubscription;
    private IDisposable? _startedRegistration;

    /// <summary>
    /// One recovery watch per RECORDED REGRESSION (#1214) — each observes its type until it
    /// reaches a usable build on this image and then retracts the regression. They outlive the
    /// sweep by design (the content that tore the compile converges after it), so they are owned
    /// here and disposed with the service; a discarded subscription would root the hub.
    /// Empty on a healthy bake, which is the overwhelmingly common case.
    /// </summary>
    private readonly CompositeDisposable _recoveryWatches = new();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // ApplicationStarted fires only after EVERY hosted service (incl. the Orleans silo)
        // has started — so grains can activate. Registering the kick here, rather than doing
        // work in StartAsync, guarantees the silo is up without any ordering assumptions.
        _startedRegistration = lifetime.ApplicationStarted.Register(KickWarmup);
        return Task.CompletedTask;
    }

    private void KickWarmup()
    {
        // The bake barrier consumers sequence on (#1114) — settled at EVERY terminal of this
        // method, including the early no-bake returns, so a waiter never waits on a bake that is
        // not coming. Resolved (not required) for resilience; AddDynamicTypePreWarming registers it.
        var bake = services.GetService<PreWarmCompletion>();

        // Config-gated, DEFAULT OFF — see the class doc. Read as a raw string (no Binder
        // dependency); anything but an explicit true stays lazy.
        var enabled = services.GetService<IConfiguration>()?[EnabledConfigKey];
        if (!bool.TryParse(enabled, out var isEnabled) || !isEnabled)
        {
            logger.LogInformation(
                "DynamicTypePreWarmer: disabled ({Key} != true) — dynamic NodeTypes compile lazily on first access",
                EnabledConfigKey);
            bake?.MarkSettled(PreWarmSettlement.NotApplicable);
            return;
        }

        var mesh = services.GetService<IMessageHub>();
        if (mesh is null)
        {
            logger.LogDebug("DynamicTypePreWarmer: no mesh hub resolved — skipping startup warm-up");
            bake?.MarkSettled(PreWarmSettlement.NotApplicable);
            return;
        }

        // Optional per-type budget override — raw string + TryParse like EnabledConfigKey, so a
        // malformed value degrades to the default rather than faulting the warm-up. Zero/negative
        // is refused for the same reason: a budget that can never elapse into a verdict would turn
        // one wedged type into an eternal sweep.
        TimeSpan? perTypeBudget = null;
        var budgetRaw = services.GetService<IConfiguration>()?[PerTypeBudgetConfigKey];
        if (!string.IsNullOrWhiteSpace(budgetRaw))
        {
            if (TimeSpan.TryParse(budgetRaw, out var parsed) && parsed > TimeSpan.Zero)
                perTypeBudget = parsed;
            else
                logger.LogWarning(
                    "DynamicTypePreWarmer: ignoring invalid {Key}='{Value}' — using the default "
                    + "per-type budget {Default}",
                    PerTypeBudgetConfigKey, budgetRaw, DynamicTypePreWarmer.DefaultPerTypeBudget);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var compiled = 0;
        var alreadyBaked = 0;
        var errored = 0;
        var timedOut = 0;
        var skipped = 0;
        var faulted = 0;
        var contentBroken = 0;

        // 🚨 WHERE THE SWEEP'S TIME WENT, named — the answer #1439 asks for and nothing could give.
        // A sweep reported only its TOTAL, so a bootstrap that ran out of budget said nothing about
        // WHICH compile units were expensive, and the only available response to a type brushing the
        // deadline was to widen it. Keeping the whole per-type list would be an unbounded allocation
        // on a 300-type mesh for a line nobody reads in full, so the summary keeps just the costliest
        // few. Instance-local to this sweep (never static — NoStaticState.md), and taken under the
        // same lock the ranking uses, because outcomes arrive on the warm subscription's thread.
        var slowestLock = new object();
        var slowest = new List<PreWarmOutcome>(SlowestReported + 1);

        // The readiness gate reads this. Resolved (not required) so a host that never called
        // AddNodeTypeBakeGate simply warms without gating — the warmer stays a latency optimisation
        // unless a deployment explicitly opts into making it a rollout gate.
        var gate = services.GetService<NodeTypeBakeGateState>();
        gate?.MarkRunning("enumerating dynamic NodeTypes");

        // Pacing: explicit config wins; otherwise a readiness-GATED pod sweeps at full speed (it
        // serves nobody while it bakes — the initial-bake case) and an ungated pod keeps the
        // serving-friendly trickle. See BetweenTypesConfigKey for the measurement behind this.
        TimeSpan? betweenTypes = gate is { GatesReadiness: true } ? TimeSpan.Zero : null;
        var pacingRaw = services.GetService<IConfiguration>()?[BetweenTypesConfigKey];
        if (!string.IsNullOrWhiteSpace(pacingRaw))
        {
            if (TimeSpan.TryParse(pacingRaw, out var parsedPacing) && parsedPacing >= TimeSpan.Zero)
                betweenTypes = parsedPacing;
            else
                logger.LogWarning(
                    "DynamicTypePreWarmer: ignoring invalid {Key}='{Value}' — using the "
                    + "{Mode} pacing default",
                    BetweenTypesConfigKey, pacingRaw,
                    gate is { GatesReadiness: true } ? "gated (zero)" : "serving (trickle)");
        }

        // Batch mode (issue #1207) follows the SAME initial-bake discriminator as the pacing above:
        // a pod whose bake GATES readiness serves nobody while it bakes, so it runs the sweep as one
        // linked batch build (no per-type hub activations); an ungated pod warming in the background
        // of live traffic keeps the activation-driven path.
        //
        // History, because this default was walked back once and the reason it is safe to restore
        // matters more than the default itself. It first shipped ON and was WRONG AT PRODUCTION
        // SCALE (memex-cloud, ci.2947): 19s sweep, `compiled=4 compileErrors=59 contentBroken=169`
        // of 237, because batched discovery resolved no sources for most types. Two real defects,
        // both now fixed and both pinned by tests: a default query limit truncated the global fetch
        // and `nodeType:Code` could not see Postgres-stored Code nodes at all (#1216/#1220), and
        // `scope:subtree` was silently dropped for a WILDCARD namespace so nested sources were
        // missed (#1232). Discovery also now REFUSES to answer rather than guess — a type that
        // declares sources but resolves none fails the whole batch to the activation path, so a
        // discovery bug can no longer masquerade as the non-gating NoSources content verdict.
        //
        // Verified in production on all three portals (2026-08-11, ci.2982/ci.2990):
        //   memex-cloud  237/237 in 62s (the same fleet took 18m55s on the activation path)
        //   memex        83 already baked + 1 pending in 6.9s
        //   atioz        39/39 in 38s; the follower pod finished in 1.1s off the shared store
        // all with compileErrors=0, timedOut=0, contentBroken=0.
        //
        // The default is what every deployment gets, so it must be the verified path — carrying it
        // as three live `kubectl set env` patches meant a helm upgrade, a new namespace or a fresh
        // deployment silently fell back to the slow path. `PreWarm:BatchBake` returns to being the
        // ESCAPE HATCH (set it false to force the activation sweep), not the enabler.
        var batchBake = gate is { GatesReadiness: true };
        var batchRaw = services.GetService<IConfiguration>()?[BatchBakeConfigKey];
        if (!string.IsNullOrWhiteSpace(batchRaw))
        {
            if (bool.TryParse(batchRaw, out var parsedBatch))
                batchBake = parsedBatch;
            else
                logger.LogWarning(
                    "DynamicTypePreWarmer: ignoring invalid {Key}='{Value}' — using the "
                    + "{Mode} default",
                    BatchBakeConfigKey, batchRaw,
                    batchBake ? "gated (batch)" : "serving (activation-driven)");
        }

        // Build-protocol coordination (Doc/Architecture/BuildCoordination): the Admin/Build claim
        // decides who bakes, chunk nodes record what each part produced, and everyone else
        // completes on the per-fingerprint GO. DEFAULT ON — this is the only coordination there
        // is (the file lease it replaced is deleted), and it was verified on memex-cloud's gated
        // roll 2026-08-13 (claim → 37 chunks → GO → gate green). Execution is the same sweep
        // either way; the key is the ESCAPE HATCH (set false to bake solo, uncoordinated).
        var buildProtocol = true;
        var protocolRaw = services.GetService<IConfiguration>()?[BuildProtocolDriver.EnabledConfigKey];
        if (!string.IsNullOrWhiteSpace(protocolRaw))
        {
            if (bool.TryParse(protocolRaw, out var parsedProtocol))
                buildProtocol = parsedProtocol;
            else
                logger.LogWarning(
                    "DynamicTypePreWarmer: ignoring invalid {Key}='{Value}' — build-protocol "
                    + "coordination stays on (the default)",
                    BuildProtocolDriver.EnabledConfigKey, protocolRaw);
        }

        // 🚨 SEQUENCE THE SWEEP BEHIND THE STATIC REPO IMPORT — it is content this bake must see.
        //
        // The import is kicked fire-and-forget from its own StartAsync (it has to be: subscribing on
        // the startup thread re-enters the hub schedulers and deadlocks), and ApplicationStarted —
        // where this method runs — fires as soon as every StartAsync has RETURNED. So without this
        // wait the sweep's ONE enumeration snapshot (Query(...).Take(1)) can be taken while the
        // import is still landing content, and every NodeType missing from that snapshot is never
        // baked: it compiles on the activation path when a user first opens it.
        //
        // Observed resolving BOTH ways on memex-cloud 2026-08-12 — the 23:02 pod enumerated 237
        // types with the whole statically-served MeshWeaver/… tree absent, its 05:52 successor 279
        // on the same content. Those 42 unbaked types are the "waiting for code" stall.
        //
        // Absent (no static import registered — every test host, non-portal hosts) this is
        // immediate, so the shape is unchanged there. The signal also settles when the import FAILS,
        // so a faulted import delays the bake rather than cancelling it.
        var importSettled = services.GetService<StaticRepoImportSettled>();
        if (importSettled is { IsSettled: false })
            logger.LogInformation(
                "DynamicTypePreWarmer: waiting for the static repo import to settle before "
                + "enumerating — types it has not written yet would be missed by the bake");

        // What the whole sweep COSTS, appended to the summary line that already exists — no new log
        // volume. This is the measurement that turned "the bake must be leaking" into a fact on
        // memex-cloud: the pod sat at 2.5 GB minutes after baking all 279 types, so one linked build
        // for the fleet is cheap and the growth is elsewhere. Keep it, so nobody has to re-derive
        // that from a container gauge. See MemoryDelta.
        var sweepMemory = MemoryDelta.Start();

        logger.LogInformation("DynamicTypePreWarmer: starting background warm-up of dynamic NodeType hubs");
        _warmSubscription = (importSettled?.Settled ?? Observable.Return(Unit.Default))
            .SelectMany(_ => DynamicTypePreWarmer
                .WarmDynamicTypes(mesh, logger, perTypeBudget, betweenTypes, batchBake, buildProtocol))
            .Subscribe(
                outcome =>
                {
                    // A recorded regression stalls the rollout — so from this moment the gate
                    // WATCHES the type it just condemned. The platform recompiles a failed type by
                    // itself once its sources change (the park registry's source-change auto-retry),
                    // and a type that then builds on this image is no longer evidence against the
                    // image: the regression is retracted and the pod may go Ready without a human.
                    // #1214: a bake that compiled a half-applied plugin update recorded four false
                    // regressions and hung the rollout although the content converged seconds later.
                    //
                    // MarkOutcome returns true only for a DIRECTLY-MEASURED regression. A derived
                    // one (a dependent skipped because its upstream failed) is recorded and still
                    // gates, but is deliberately NOT watched: the sweep skipped it to avoid
                    // activating its hub at all, and one broken upstream would otherwise activate
                    // its whole fan-out and hold it for the pod's lifetime. Those are retracted
                    // through their blocker instead (NodeTypeBakeGateState.RetractRegression).
                    if (gate?.MarkOutcome(outcome) == true)
                    {
                        if (outcome.SourcesMovedDuringCompile)
                            logger.LogWarning(
                                "DynamicTypePreWarmer: {TypePath} regressed, but its SOURCES MOVED "
                                + "at or after the compile started — this verdict may describe a "
                                + "half-applied content update (a plugin auto-update or git sync "
                                + "landing mid-bake), not this image. It STANDS until the type is "
                                + "seen to build here, and is retracted automatically when it does.",
                                outcome.TypePath);
                        _recoveryWatches.Add(
                            DynamicTypePreWarmer.WatchForRecovery(mesh, gate, outcome.TypePath, logger));
                    }
                    // Rank by cost before classifying — a type that TIMED OUT is exactly the one
                    // whose duration the summary must name, so this deliberately does not filter on
                    // success.
                    if (outcome.Duration > TimeSpan.Zero)
                        lock (slowestLock)
                        {
                            slowest.Add(outcome);
                            slowest.Sort((a, b) => b.Duration.CompareTo(a.Duration));
                            if (slowest.Count > SlowestReported)
                                slowest.RemoveAt(slowest.Count - 1);
                        }
                    switch (outcome.Status)
                    {
                        case PreWarmStatus.Compiled: Interlocked.Increment(ref compiled); break;
                        case PreWarmStatus.AlreadyBaked: Interlocked.Increment(ref alreadyBaked); break;
                        case PreWarmStatus.CompileError: Interlocked.Increment(ref errored); break;
                        case PreWarmStatus.TimedOut: Interlocked.Increment(ref timedOut); break;
                        // A type skipped because its upstream failed — or because its upstream was
                        // never evaluated — is not a FAULT; it is a deliberate, reported outcome.
                        // Counting it as one made the summary read like the warmer had crashed N
                        // times when one dependency was broken. (Which of the two it was is not
                        // lost: the gate files UpstreamUnevaluated under "not evaluated" and names
                        // it in the health payload, while UpstreamFailed gates.)
                        case PreWarmStatus.UpstreamFailed:
                        case PreWarmStatus.UpstreamUnevaluated: Interlocked.Increment(ref skipped); break;
                        // Broken by deleted CONTENT, not by this image — the gate files these
                        // under ContentBroken (never gating); the count keeps them visible here.
                        case PreWarmStatus.NoSources:
                        case PreWarmStatus.UpstreamContentBroken: Interlocked.Increment(ref contentBroken); break;
                        default: Interlocked.Increment(ref faulted); break;
                    }
                },
                ex =>
                {
                    // 🚨 A FAULTED SWEEP PROVED NOTHING — and must not be recorded as a bake that
                    // passed. This handler used to call MarkComplete, so a pod that could not
                    // enumerate a single NodeType went Complete → Healthy and took traffic having
                    // verified nothing: the one way a pod could clear a gate that otherwise works.
                    //
                    // The rationale for releasing it read "an un-provable bake must not become an
                    // outage", which is the documented fail-OPEN policy — but that policy is about
                    // the pre-warm being DISABLED (BakePhase.NotStarted: "a configuration mistake
                    // must never black-hole a pod forever"), not about it ERRORING. The same health
                    // check already reports BakePhase.Running — a pod that has verified strictly
                    // MORE than this one — as Unhealthy, so treating a fault as passing was never
                    // the policy; it was an inconsistency inside it. Nothing here changes what
                    // happens when the sweep is switched off.
                    //
                    // Correctness is unaffected either way: lazy compilation still builds every
                    // type on first access. What changes is only whether this pod may CLAIM to have
                    // verified its NodeTypes on this image.
                    gate?.MarkFaulted("the warm-up stream faulted before the sweep could finish");
                    if (gate is { GatesReadiness: true, Phase: BakePhase.Faulted })
                        logger.LogCritical(ex,
                            "DynamicTypePreWarmer: REFUSING READINESS — the warm-up stream FAULTED, "
                            + "so this pod has NOT verified that its NodeTypes build on this image. "
                            + "A sweep that errored is not a sweep that passed. The rollout will "
                            + "stall with the previous image still serving; a restart re-runs the "
                            + "sweep, so a transient cause clears itself. Lazy compilation still "
                            + "works, so if this deployment must roll forward regardless, set "
                            + "'{Key}'=true — that serves on an UNPROVEN bake and says so in the "
                            + "health payload.",
                            NodeTypeBakeGateExtensions.AllowUnprovenBakeConfigKey);
                    else
                        logger.LogError(ex,
                            "DynamicTypePreWarmer: warm-up stream faulted — this pod verified "
                            + "NOTHING about its NodeTypes (lazy compile still works, so it stays "
                            + "correct). Nothing gates on this state here; the bake is recorded as "
                            + "unproven rather than complete.");
                    // A fault is a terminal too: the sweep is over, the compile queue is no longer
                    // saturated, and whoever sequenced on the bake may proceed (#1114) — now able
                    // to see THAT it was a fault they proceeded past.
                    bake?.MarkSettled(PreWarmSettlement.Faulted);
                },
                () =>
                {
                    var elapsed = DateTimeOffset.UtcNow - startedAt;
                    string costliest;
                    lock (slowestLock)
                        costliest = slowest.Count == 0
                            ? "(nothing was compiled)"
                            : string.Join(", ", slowest.Select(o => $"{o.TypePath} {o.DescribeCost()}"));
                    logger.LogInformation(
                        "DynamicTypePreWarmer: warm-up complete in {Elapsed} — compiled={Compiled} alreadyBaked={AlreadyBaked} "
                        + "compileErrors={Errored} timedOut={TimedOut} skipped={Skipped} contentBroken={ContentBroken} faulted={Faulted} "
                        + "— costliest: {Costliest} — {Memory}",
                        elapsed,
                        Volatile.Read(ref compiled), Volatile.Read(ref alreadyBaked), Volatile.Read(ref errored),
                        Volatile.Read(ref timedOut), Volatile.Read(ref skipped), Volatile.Read(ref contentBroken),
                        Volatile.Read(ref faulted), costliest, sweepMemory);

                    // MarkComplete keeps a recorded regression red — completion is not absolution.
                    gate?.MarkComplete(
                        $"baked in {elapsed:hh\\:mm\\:ss} — compiled={Volatile.Read(ref compiled)} "
                        + $"alreadyBaked={Volatile.Read(ref alreadyBaked)}");
                    // 🚨 Say ONLY what is actually enforced. The gate STATE is registered
                    // unconditionally, so this branch runs whether or not a readiness probe consumes
                    // it. Claiming a stall that nothing enforces is worse than saying nothing: it was
                    // read as proof the portal was protected while the pod went Ready and served
                    // traffic, and a production outage was diagnosed against it for hours.
                    if (gate is { Phase: BakePhase.Regressed })
                    {
                        var regressions = string.Join(" | ", gate.Regressions.Select(r => $"{r.Key} → {r.Value}"));
                        if (gate.GatesReadiness)
                            logger.LogCritical(
                                "DynamicTypePreWarmer: REFUSING READINESS — {Detail}. The rollout will stall "
                                + "with the previous image still serving. Each of these types is now being "
                                + "WATCHED: if it reaches a usable build on this image (the platform "
                                + "recompiles a failed type once its sources change), its regression is "
                                + "retracted and readiness follows — so a verdict formed against a "
                                + "half-applied content update clears itself. Regressions: {Regressions}",
                                gate.Detail, regressions);
                        else
                            logger.LogCritical(
                                "DynamicTypePreWarmer: GATE NOT ARMED — {Count} NodeType(s) regressed on this "
                                + "image and NOTHING CONSUMES THIS STATE, so nothing is blocked. Startup "
                                + "continues and instances of these types will fail. To make it enforce, run a "
                                + "host that registers the bake readiness check with '{Key}'=true, and give the "
                                + "startupProbe a full cold-bake budget. {Detail}. Regressions: {Regressions}",
                                gate.Regressions.Count, NodeTypeBakeGateExtensions.EnabledConfigKey,
                                gate.Detail, regressions);
                    }

                    // The sweep ran to its end — regressed or not, the compile queue has drained
                    // and per-node hub activations no longer park behind it. Release the boot
                    // flows sequenced on the bake (#1114). On a Regressed+armed pod readiness
                    // stays refused regardless; the default install proceeding is deliberate —
                    // installs repair content, and the broken type is already terminal.
                    bake?.MarkSettled(PreWarmSettlement.Completed);
                });
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _startedRegistration?.Dispose();
        _startedRegistration = null;
        _warmSubscription?.Dispose();
        _warmSubscription = null;
        _recoveryWatches.Dispose();
    }
}

/// <summary>Opt-in registration for the dynamic-NodeType startup pre-warm (portal hosts).</summary>
public static class PreWarmServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DynamicTypePreWarmerHostedService"/> so the pod CAN front-load its
    /// dynamic NodeType compiles at startup (Part 1 of the fresh-pod compile-race hardening).
    /// The warm-up only actually runs when the deployment opts in via
    /// <see cref="DynamicTypePreWarmerHostedService.EnabledConfigKey"/> (default: off — startup
    /// does no mesh-wide enumeration; types compile lazily on first access). Best-effort and
    /// non-blocking — safe to call from any portal host; a no-op if no mesh hub is present.
    /// </summary>
    public static IServiceCollection AddDynamicTypePreWarming(this IServiceCollection services)
    {
        // The bake barrier (#1114): boot flows that write through per-node hubs — the plugin
        // default install foremost — resolve this and sequence themselves after the sweep, so
        // they never race a post-roll recompile storm. Registered HERE, with the pre-warmer,
        // because its absence is itself the signal: a host without the pre-warm has no bake to
        // wait for, and consumers proceed immediately.
        services.TryAddSingleton<PreWarmCompletion>();
        services.AddHostedService<DynamicTypePreWarmerHostedService>();
        return services;
    }
}
