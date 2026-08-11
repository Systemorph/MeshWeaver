using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting;

/// <summary>How far this pod's dynamic-NodeType bake has got.</summary>
public enum BakePhase
{
    /// <summary>The sweep has not been started — typically because pre-warming is disabled.</summary>
    NotStarted,
    /// <summary>The sweep is running; some types are not yet built against this framework.</summary>
    Running,
    /// <summary>Every type the sweep touched reached a usable build.</summary>
    Complete,
    /// <summary>
    /// At least one type that WAS working before this image failed to build against it — a
    /// regression. The pod must not take traffic.
    /// </summary>
    Regressed,
}

/// <summary>
/// This pod's live bake state, written by <see cref="DynamicTypePreWarmerHostedService"/> and read by
/// the host's readiness probe (<c>Memex.Portal.Distributed.NodeTypeBakeHealthCheck</c>).
///
/// <para>Kept as a tiny mutable singleton rather than threaded through the reactive pipeline on
/// purpose: a health check is a synchronous pull from ASP.NET's probe endpoint, and it must never
/// block on, subscribe to, or take a dependency on the mesh — a readiness probe that can hang is a
/// readiness probe that takes the pod out of rotation under load (the /health-as-readiness
/// death-spiral this deployment already fixed once).</para>
/// </summary>
public sealed class NodeTypeBakeGateState
{
    private readonly ConcurrentDictionary<string, string> regressions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> unevaluated = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> contentBroken = new(StringComparer.OrdinalIgnoreCase);
    private int phase = (int)BakePhase.NotStarted;
    private volatile string detail = "pre-warm not started";

    /// <summary>
    /// Whether a readiness probe actually CONSUMES this state — i.e. the host registered its
    /// health check. Declared by the host at registration
    /// (<see cref="NodeTypeBakeGateExtensions.AddNodeTypeBakeGate"/>), never re-derived from
    /// configuration here: the host owns the decision, and re-parsing the key would be a second
    /// source of truth that can disagree with the wiring it claims to describe.
    ///
    /// <para>🚨 This exists because the gate silently did nothing for months while the log said
    /// otherwise. The state is registered unconditionally, so a recorded regression used to print
    /// "REFUSING READINESS — the rollout will stall with the previous image still serving" on a pod
    /// that then went Ready and took traffic. An operator reading that line believed they were
    /// protected; a production outage was diagnosed for hours against a log that was describing a
    /// gate nobody had armed. A message that overstates enforcement is worse than no message.</para>
    /// </summary>
    public bool GatesReadiness { get; init; }

    /// <summary>The current phase.</summary>
    public BakePhase Phase => (BakePhase)Volatile.Read(ref phase);

    /// <summary>Human-readable status for the health payload and the logs.</summary>
    public string Detail => detail;

    /// <summary>Types that regressed on this image, with their compile error.</summary>
    public IReadOnlyDictionary<string, string> Regressions => regressions;

    /// <summary>
    /// Types the sweep could NOT evaluate on this image — a timed-out warm or an upstream that
    /// never built. Visible for diagnosis, but deliberately NOT readiness-blocking: "I don't know"
    /// is not "it broke". See <see cref="MarkOutcome"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Unevaluated => unevaluated;

    /// <summary>
    /// Types broken by CONTENT on this image — their declared source queries match zero Code nodes
    /// (the sources were deleted from the mesh), or an upstream in that state blocks them. Visible
    /// for diagnosis, deliberately NOT readiness-blocking: no image caused it and no rollout can
    /// fix it. See <see cref="PreWarmStatus.NoSources"/> for the incident that mandates this.
    /// </summary>
    public IReadOnlyDictionary<string, string> ContentBroken => contentBroken;

    /// <summary>The sweep has begun.</summary>
    public void MarkRunning(string message)
    {
        Interlocked.Exchange(ref phase, (int)BakePhase.Running);
        detail = message;
    }

    /// <summary>
    /// Record one type's outcome. Only a type that was HEALTHY before this image can put the gate
    /// into <see cref="BakePhase.Regressed"/> — a type already sitting at Error before the deploy is
    /// broken in production right now, and blocking every future rollout on it would let one
    /// abandoned NodeType freeze the platform.
    /// </summary>
    public void MarkOutcome(PreWarmOutcome outcome)
    {
        if (outcome.ReachedUsableBuild || !outcome.WasHealthyBeforeBake)
            return;

        // 🚨 A TIMEOUT IS NOT A VERDICT. The per-type budget elapsing means the sweep never got an
        // answer — it says nothing about whether the type builds. During a roll the baking pod and
        // the serving pod are two silos, and the sweep's shared-source resolution can time out
        // across that boundary (core #694), so a healthy type times out for reasons that have
        // nothing to do with it.
        //
        // Enabling the gate while timeouts counted as regressions stalled memex-cloud's rollout on
        // 2026-08-02: "7 NodeType(s) regressed on this image", with not a single CS#### diagnostic
        // in the pod log — every one was a SubscribeRequest timeout. The old image kept serving (no
        // outage), but self-update silently stopped advancing, which for an auto-updating fleet is
        // the worse failure.
        //
        // UpstreamUnevaluated is the SAME condition one hop downstream: the sweep never got an
        // answer about this type's upstream, so it has no answer about this type either. Without
        // it the leniency stopped at depth 1 — one timed-out shared source turned every
        // previously-healthy dependent into a gating UpstreamFailed and the 2026-08-02 stall came
        // straight back. "I don't know" has to propagate as "I don't know".
        //
        // Deliberately narrow: only the two no-answer statuses are reclassified. A CompileError is
        // Roslyn's verdict that the type is broken on this image, and an UpstreamFailed — a
        // dependent of a type that genuinely failed to compile — still gates; see
        // UpstreamFailedOnAPreviouslyHealthyType_AlsoGates for why that one is intentional.
        if (outcome.Status is PreWarmStatus.TimedOut or PreWarmStatus.UpstreamUnevaluated)
        {
            unevaluated[outcome.TypePath] = $"{outcome.Status}: {outcome.Detail ?? "(no detail)"}";
            return;
        }

        // A CONTENT verdict is not an IMAGE verdict. NoSources means the type's declared source
        // queries no longer match anything on the mesh — its sources were deleted out from under
        // it (2026-08-10: four KmuBasics/* types whose course was re-installed under a new id,
        // the type nodes left behind, stalled memex-cloud's self-update across two images). No
        // image can change what a mesh query matches, so no rollout may be held hostage to it —
        // the same deploy-freeze rule as WasHealthyBeforeBake, arriving through content deletion
        // instead of an abandoned Error record. UpstreamContentBroken is the same condition one
        // hop downstream (the depth-1 rule, again).
        if (outcome.Status is PreWarmStatus.NoSources or PreWarmStatus.UpstreamContentBroken)
        {
            contentBroken[outcome.TypePath] = $"{outcome.Status}: {outcome.Detail ?? "(no detail)"}";
            return;
        }

        regressions[outcome.TypePath] = $"{outcome.Status}: {outcome.Detail ?? "(no detail)"}";
        Interlocked.Exchange(ref phase, (int)BakePhase.Regressed);
        detail = $"{regressions.Count} NodeType(s) regressed on this image";
    }

    /// <summary>
    /// The sweep finished. A run that recorded a regression STAYS regressed — completion is not
    /// absolution.
    /// </summary>
    public void MarkComplete(string message)
    {
        if (!regressions.IsEmpty)
        {
            detail = $"{regressions.Count} NodeType(s) regressed on this image: "
                + string.Join(", ", regressions.Keys.OrderBy(k => k, StringComparer.Ordinal));
            return;
        }

        Interlocked.Exchange(ref phase, (int)BakePhase.Complete);
        // Ready, but say so honestly: a type we could not evaluate is not a type we verified, and
        // a content-broken type is broken RIGHT NOW even though it must not stall the rollout.
        // Surfacing both in the health payload is what keeps "non-blocking" from becoming
        // "invisible" — a silently-swallowed bucket is how a real problem would hide behind this
        // leniency.
        var addenda = new List<string>(2);
        if (!unevaluated.IsEmpty)
            addenda.Add($"{unevaluated.Count} not evaluated — "
                + string.Join(", ", unevaluated.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        if (!contentBroken.IsEmpty)
            addenda.Add($"{contentBroken.Count} content-broken, sources missing — "
                + string.Join(", ", contentBroken.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        detail = addenda.Count == 0
            ? message
            : $"{message} ({string.Join("; ", addenda)})";
    }
}

/// <summary>DI helper for the bake readiness gate.</summary>
///
/// <remarks>
/// Only the STATE lives in the framework. The <c>IHealthCheck</c> that surfaces it on <c>/health</c>
/// belongs to the host (see <c>Memex.Portal.Distributed.NodeTypeBakeHealthCheck</c>), which already
/// owns its probe wiring — that keeps the health-check package out of the framework's dependency
/// closure and lets a self-host surface the same state however it likes.
/// </remarks>
public static class NodeTypeBakeGateExtensions
{
    /// <summary>Config key a host reads to decide whether to gate readiness. Default: off.</summary>
    public const string EnabledConfigKey = "PreWarm:GateReadiness";

    /// <summary>
    /// Registers the shared bake state. Safe and cheap to call unconditionally: the pre-warm hosted
    /// service writes to it whether or not anything gates on it, so the diagnostics are always there.
    ///
    /// <para>⚠️ A host that gates readiness on this MUST raise its <c>startupProbe.failureThreshold</c>
    /// to cover a full cold bake. A cold compile is ~2.4 s per NodeType and the sweep is strictly
    /// sequential (measured 2026-08-10 across three production portals), so the largest mesh we run
    /// (~240 types) needs ~10 minutes of bake on top of a plain cold boot. Left at the usual default
    /// (60 × 5 s = 5 minutes) Kubernetes kills and restarts the pod mid-bake, forever. The chart's
    /// paired setting is 10 s × 180 = 30 minutes; see <c>deploy/helm/values.yaml</c>
    /// (<c>probes.startup</c>) for the derivation.</para>
    /// </summary>
    /// <param name="gatesReadiness">
    /// Pass <c>true</c> ONLY when the host also registers a readiness check that consumes this state.
    /// It is what makes <see cref="NodeTypeBakeGateState.GatesReadiness"/> honest, and therefore what
    /// decides whether a recorded regression is reported as an enforced stall or as an unenforced
    /// warning. Defaulting to <c>false</c> keeps "registered" and "armed" separate: the diagnostics
    /// are always collected, the enforcement is opt-in, and the log never claims the latter from the
    /// former.
    /// </param>
    public static IServiceCollection AddNodeTypeBakeGate(
        this IServiceCollection services, bool gatesReadiness = false)
    {
        services.AddSingleton(new NodeTypeBakeGateState { GatesReadiness = gatesReadiness });
        return services;
    }
}
