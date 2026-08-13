using MeshWeaver.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.Distributed;

/// <summary>
/// Readiness gate: reports Unhealthy until this pod's dynamic NodeTypes are built against the image
/// it is actually running.
///
/// <para><b>What it buys — "fail before prod, not in prod".</b> The chart already keeps the OLD pod
/// serving until the new one passes its <c>startupProbe</c> on <c>/health</c> (<c>maxSurge: 1</c>,
/// <c>maxUnavailable: 0</c>). Wiring the bake into <c>/health</c> therefore turns a NodeType that no
/// longer compiles from a production incident into a STALLED ROLLOUT: the new pod never goes ready,
/// traffic never moves, and the previous image keeps serving. That is the difference between
/// discovering a broken type from a user's error page and discovering it from a rollout that
/// declined to finish.</para>
///
/// <para><b>Fail CLOSED on a regression, fail OPEN on "not running".</b> A detected regression holds
/// the pod out of rotation — the entire point. But <see cref="BakePhase.NotStarted"/> reports
/// HEALTHY: if this check is registered while the sweep is not enabled, that is a configuration
/// mistake, and a configuration mistake must never black-hole a pod forever. The gate withholds
/// readiness only for a condition it is actively measuring.</para>
///
/// <para>🚨 <b>"Not running" means DISABLED, not ERRORED.</b> The fail-open clause above is scoped
/// to <see cref="BakePhase.NotStarted"/> — the gate is switched off, nothing is being measured.
/// <see cref="BakePhase.Faulted"/> is the opposite situation: the sweep was armed, was measuring,
/// and ERRORED, so this pod verified nothing while claiming to be verifying. That fails CLOSED.
/// The rest of this switch already implies it — <see cref="BakePhase.Running"/> is Unhealthy, and a
/// pod whose sweep errored knows strictly LESS than one still sweeping, so handing it the better
/// verdict was an inconsistency rather than a policy. It is also the rule the retired pre-run bake
/// Job enforced from outside the portal (<i>"FINDING NOTHING IS NOT PASSING"</i>, exit 3) until
/// #1357 retired the Job without porting the guard.</para>
///
/// <para><b>Cold start is deliberate, and it is the same trade the gate already makes.</b> On a
/// ROLL, <c>maxSurge:1 / maxUnavailable:0</c> means refusing readiness is free: the old pod keeps
/// serving and the rollout stalls. On a FIRST deployment there is no predecessor, so refusing
/// readiness means the install does not come up — the pod fails its <c>startupProbe</c> and
/// restarts, re-running the sweep each time, which is what clears a transient cause without anybody
/// adding a retry. That is not a new exposure: <see cref="BakePhase.Regressed"/> already black-holes
/// a first deployment in exactly the same way, and the environmental cause most likely to produce a
/// blind enumeration — an unmigrated database — is already refused earlier and more precisely by
/// <see cref="DbVersionGate"/>. An operator who must roll forward anyway sets
/// <see cref="NodeTypeBakeGateExtensions.AllowUnprovenBakeConfigKey"/>, which serves on an unproven
/// bake and keeps saying so in the payload.</para>
///
/// <para><b>Deliberately NOT tagged <c>"live"</c>.</b> It must gate <c>/health</c> (startup +
/// rollout) and never <c>/alive</c> (liveness): a long bake is not a wedged process, and failing
/// liveness would have Kubernetes restart the pod in the middle of the very work it is waiting for.
/// Same reasoning that keeps <c>/alive</c> light in <c>ServiceDefaults</c>.</para>
/// </summary>
public sealed class NodeTypeBakeHealthCheck(NodeTypeBakeGateState state) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(state.Phase switch
        {
            // Nobody is baking — never gate on an unmeasured condition.
            BakePhase.NotStarted => HealthCheckResult.Healthy($"bake not gating ({state.Detail})"),
            BakePhase.Complete => HealthCheckResult.Healthy(state.Detail),
            BakePhase.Regressed => HealthCheckResult.Unhealthy(
                "NodeType bake regressed on this image — refusing readiness so the rollout stalls "
                + $"with the previous image still serving. {state.Detail}"),
            // The sweep errored: nothing was verified. Unhealthy unless the operator has explicitly
            // accepted serving on an unproven bake — and even then the payload still says it was
            // never proven, because the override relaxes the VERDICT, never the record.
            BakePhase.Faulted => state.AllowUnprovenBake
                ? HealthCheckResult.Healthy(
                    "NodeType bake was NOT proven, serving anyway because "
                    + $"{NodeTypeBakeGateExtensions.AllowUnprovenBakeConfigKey}=true — types compile "
                    + $"lazily on first access. {state.Detail}")
                : HealthCheckResult.Unhealthy(
                    "NodeType bake FAULTED before it could verify this image — refusing readiness. "
                    + "A sweep that errored has verified nothing, and certifying an unmeasured bake "
                    + "is precisely what this gate exists to prevent. A restart re-runs the sweep; "
                    + $"to serve regardless, set {NodeTypeBakeGateExtensions.AllowUnprovenBakeConfigKey}"
                    + $"=true. {state.Detail}"),
            _ => HealthCheckResult.Unhealthy($"NodeType bake in progress — {state.Detail}"),
        });
}
