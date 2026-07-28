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
            _ => HealthCheckResult.Unhealthy($"NodeType bake in progress — {state.Detail}"),
        });
}
