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
    private readonly ConcurrentDictionary<string, string> retracted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// For each DERIVED regression (an <c>Upstream*</c> outcome), the upstream that blocked it —
    /// see <see cref="PreWarmOutcome.BlockedBy"/>. Retracting a blocker cascades to these, because
    /// their only evidence was the blocker's verdict. Guarded by <see cref="verdict"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> blockedBy = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Guards the (regressions ↔ phase ↔ detail) invariant across the four writers, so a
    /// retraction that empties the set cannot interleave with a fresh regression and leave the
    /// phase disagreeing with the dictionary.
    ///
    /// <para>Writers only. <see cref="Phase"/> and <see cref="Detail"/> stay lock-free
    /// (<see cref="Volatile"/> / <c>volatile</c>) because the readiness probe reads them
    /// synchronously from ASP.NET's health endpoint — a probe that can block on a lock is a probe
    /// that can take the pod out of rotation under load. Nothing awaits inside the lock; it is a
    /// handful of dictionary operations and a string build.</para>
    /// </summary>
    private readonly object verdict = new();

    private int phase = (int)BakePhase.NotStarted;
    private volatile string detail = "pre-warm not started";

    /// <summary>The message <see cref="MarkComplete"/> was given, or <c>null</c> while the sweep
    /// is still running. Guarded by <see cref="verdict"/>.</summary>
    private string? completedMessage;

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

    /// <summary>
    /// Regressions that were RETRACTED because the type has since been observed reaching a usable
    /// build on this image — see <see cref="RetractRegression"/>. Kept (rather than simply
    /// forgotten) so a rollout that recovered by itself still says so in the health payload: a
    /// silently-vanished regression is indistinguishable from one that never happened.
    /// </summary>
    public IReadOnlyDictionary<string, string> Retracted => retracted;

    /// <summary>The sweep has begun.</summary>
    public void MarkRunning(string message)
    {
        lock (verdict)
        {
            Interlocked.Exchange(ref phase, (int)BakePhase.Running);
            detail = message;
        }
    }

    /// <summary>
    /// Record one type's outcome. Only a type that was HEALTHY before this image can put the gate
    /// into <see cref="BakePhase.Regressed"/> — a type already sitting at Error before the deploy is
    /// broken in production right now, and blocking every future rollout on it would let one
    /// abandoned NodeType freeze the platform.
    /// </summary>
    /// <returns>
    /// <c>true</c> when this outcome recorded a regression the caller should WATCH FOR RECOVERY —
    /// a DIRECTLY-MEASURED verdict on a type the sweep actually compiled
    /// (<see cref="RetractRegression"/> is what a recovery then calls).
    ///
    /// <para><c>false</c> for every non-gating outcome AND for a DERIVED regression (one carrying
    /// <see cref="PreWarmOutcome.BlockedBy"/>), which is still recorded and still gates but must
    /// NOT be watched: the sweep skipped that type precisely to avoid activating its hub, and
    /// watching it would activate every skipped dependent of one broken upstream and hold them for
    /// the pod's lifetime. A derived regression is retracted through its blocker instead — see the
    /// cascade in <see cref="RetractRegression"/>.</para>
    /// </returns>
    public bool MarkOutcome(PreWarmOutcome outcome)
    {
        if (outcome.ReachedUsableBuild || !outcome.WasHealthyBeforeBake)
            return false;

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
            return false;
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
            return false;
        }

        lock (verdict)
        {
            regressions[outcome.TypePath] = $"{outcome.Status}: {outcome.Detail ?? "(no detail)"}";
            // A fresh verdict supersedes an earlier retraction of the SAME type: if it broke again
            // after recovering, the health payload must say "regressed", not "recovered".
            retracted.TryRemove(outcome.TypePath, out _);
            if (outcome.BlockedBy is { Length: > 0 } blocker)
                blockedBy[outcome.TypePath] = blocker;
            else
                blockedBy.TryRemove(outcome.TypePath, out _);
            Interlocked.Exchange(ref phase, (int)BakePhase.Regressed);
            detail = RegressedDetail();
        }
        return outcome.BlockedBy is null;
    }

    /// <summary>
    /// 🚨 A REGRESSION IS A MEASUREMENT, NOT A LABEL — retract it when the type it names is
    /// afterwards observed reaching a usable build ON THIS IMAGE.
    ///
    /// <para>This is NOT "completion is absolution", which <see cref="MarkComplete"/> still
    /// refuses: the sweep finishing proves nothing about a type that failed during it. This is the
    /// opposite direction — a LATER, BETTER measurement of the SAME type on the SAME image
    /// supersedes an earlier one. A type that compiles here cannot also be evidence that this image
    /// broke it, and the gate exists to stop a bad IMAGE.</para>
    ///
    /// <para><b>The incident this exists for (memex-cloud, 2026-08-11 11:04–11:12 UTC, issue
    /// #1214).</b> A gated pod refused readiness with four <c>Store/*</c> types "regressed" and
    /// CS0117 diagnostics naming <c>Localizer</c> members. Nothing was wrong with the image: the
    /// plugins repo had reverted a branch and the portal's plugin auto-update was applying that
    /// revert WHILE the bake compiled — <c>Store/Plugin/Source/Localizer</c> landed at 11:04:59
    /// (members gone) and its callers at 11:06:57. The bake, running 10:47→11:06, compiled against
    /// the gap between the two writes, so Roslyn's verdict described a source set that existed for
    /// roughly two minutes and never again.</para>
    ///
    /// <para>The platform itself already treats such a failure as provisional:
    /// <c>NodeTypeCompileParkRegistry.ShouldRetryForSourceChange</c> un-parks and recompiles a
    /// failed type the moment its source snapshot changes, so those four types went green seconds
    /// after the content converged. The ONLY thing that stayed broken was this record — latched,
    /// sticky, and never re-read — so the pod never became Ready and the rollout hung until a human
    /// noticed. Making the latch level-triggered on the truth it claims to represent is the fix;
    /// widening a timeout or ignoring CompileErrors would not be.</para>
    ///
    /// <para><b>It cannot launder a real regression.</b> The retraction is driven by the type
    /// reaching a usable build — a genuinely broken type never does, so a real compile error on a
    /// stable source set gates exactly as long as it did before: forever. No timer, no retry, no
    /// budget; the caller merely observes state the platform already publishes.</para>
    /// </summary>
    /// <param name="typePath">The NodeType whose regression is being retracted.</param>
    /// <param name="reason">Why — recorded in <see cref="Retracted"/> and the health payload.</param>
    /// <returns><c>true</c> if a regression was actually held for this type and has now been removed.</returns>
    public bool RetractRegression(string typePath, string reason)
    {
        lock (verdict)
        {
            if (!regressions.TryRemove(typePath, out var was))
                return false;

            retracted[typePath] = $"{reason} (had been {was})";

            // 🚨 CASCADE THROUGH THE DERIVED VERDICTS. A dependent skipped as UpstreamFailed was
            // never compiled — its regression's ENTIRE evidence is "my upstream failed". With that
            // verdict withdrawn there is nothing left saying this type is broken, so it must be
            // withdrawn too; leaving it would keep the pod out of rotation on a claim whose basis
            // no longer exists. This is the same principle the Upstream* statuses already encode —
            // a derived verdict is only ever as strong as the one it derives from.
            //
            // Transitive (a dependent of a dependent), and terminating: each round removes at least
            // one entry from a finite, shrinking set.
            var cascade = new Queue<string>();
            cascade.Enqueue(typePath);
            while (cascade.Count > 0)
            {
                var blocker = cascade.Dequeue();
                foreach (var dependent in blockedBy
                             .Where(kv => string.Equals(
                                 kv.Value, blocker, StringComparison.OrdinalIgnoreCase))
                             .Select(kv => kv.Key)
                             .ToList())
                {
                    blockedBy.TryRemove(dependent, out _);
                    if (!regressions.TryRemove(dependent, out var derived))
                        continue;
                    retracted[dependent] = $"its blocker {blocker} was retracted (had been {derived})";
                    cascade.Enqueue(dependent);
                }
            }

            if (!regressions.IsEmpty)
            {
                // Still red on other types — only the wording changes.
                detail = RegressedDetail();
                return true;
            }

            // That was the last one. Where the gate lands depends on whether the sweep has
            // finished: mid-sweep it goes back to Running (still measuring), afterwards to the
            // Complete verdict MarkComplete would have produced had this type never failed.
            if (completedMessage is not { } done)
            {
                Interlocked.Exchange(ref phase, (int)BakePhase.Running);
                detail = "the last recorded regression was retracted — sweep still running";
                return true;
            }

            Interlocked.Exchange(ref phase, (int)BakePhase.Complete);
            detail = CompleteDetail(done);
            return true;
        }
    }

    /// <summary>
    /// The sweep finished. A run that recorded a regression STAYS regressed — completion is not
    /// absolution. (What DOES absolve a single type is that type building on this image afterwards
    /// — see <see cref="RetractRegression"/>, which is a fresh measurement rather than an amnesty.)
    /// </summary>
    public void MarkComplete(string message)
    {
        lock (verdict)
        {
            // Remembered so a retraction arriving AFTER the sweep can land on the Complete verdict
            // this call would have produced, instead of leaving the pod stuck mid-sweep forever.
            completedMessage = message;

            if (!regressions.IsEmpty)
            {
                detail = RegressedDetail();
                return;
            }

            Interlocked.Exchange(ref phase, (int)BakePhase.Complete);
            detail = CompleteDetail(message);
        }
    }

    /// <summary>
    /// The health payload while at least one regression stands. Names any RETRACTED regression
    /// too: on a pod that is still red, "these other types failed and then rebuilt" is exactly the
    /// signal that tells an operator they are looking at a content race rather than a bad image —
    /// hiding it until the gate happens to go green would withhold it precisely when it is needed.
    /// Caller holds <see cref="verdict"/>.
    /// </summary>
    private string RegressedDetail()
    {
        var head = $"{regressions.Count} NodeType(s) regressed on this image: "
            + string.Join(", ", regressions.Keys.OrderBy(k => k, StringComparer.Ordinal));
        return retracted.IsEmpty
            ? head
            : $"{head} ({retracted.Count} further regression(s) retracted after the type rebuilt "
                + "on this image — "
                + string.Join(", ", retracted.Keys.OrderBy(k => k, StringComparer.Ordinal)) + ")";
    }

    /// <summary>
    /// The health payload for a pod that may serve. Ready, but say so honestly: a type we could not
    /// evaluate is not a type we verified, a content-broken type is broken RIGHT NOW even though it
    /// must not stall the rollout, and a retracted regression means this pod DID record damage that
    /// later healed. Surfacing all three is what keeps "non-blocking" from becoming "invisible" — a
    /// silently-swallowed bucket is how a real problem would hide behind this leniency.
    /// Caller holds <see cref="verdict"/>.
    /// </summary>
    private string CompleteDetail(string message)
    {
        var addenda = new List<string>(3);
        if (!unevaluated.IsEmpty)
            addenda.Add($"{unevaluated.Count} not evaluated — "
                + string.Join(", ", unevaluated.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        if (!contentBroken.IsEmpty)
            addenda.Add($"{contentBroken.Count} content-broken, sources missing — "
                + string.Join(", ", contentBroken.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        if (!retracted.IsEmpty)
            addenda.Add($"{retracted.Count} regression(s) retracted after the type rebuilt on this "
                + "image — "
                + string.Join(", ", retracted.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        return addenda.Count == 0
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
