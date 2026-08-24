using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.Distributed;

/// <summary>
/// Reports whether this instance is answering package entitlement against the REGISTRY ANCHOR, or
/// falling back to cached bindings because the anchor cannot be reached (#1782 gap 2).
///
/// <para>🚨 <b>Why a surface at all.</b> Every refusal on the bundle routes is byte-identical on the
/// wire (#1777) — deliberately, because a distinguishable refusal would be an inventory oracle over
/// the whole catalogue. That is exactly right for the caller and blind for the operator: "not
/// granted", "no such package" and "I could not reach the registry to find out" leave the same
/// trace. Without a surface, an anchor that has been down for a day looks precisely like a day on
/// which nobody asked for anything they were not entitled to.</para>
///
/// <para>🚨 <b>DEGRADED, never Unhealthy.</b> A degraded answer is a CORRECT answer: a caller whose
/// entitlement was previously observed keeps being served, deliberately, because an unreachable
/// registry is not evidence of a missing purchase. Failing readiness would turn "the registry is
/// briefly unlistable" into an outage of this instance — strictly worse than the degradation, and
/// the same reasoning <see cref="BundleAdoptionHealthCheck"/> applies to a fetch miss.</para>
///
/// <para>🚨 <b>"Nothing was ever asked" is its own answer.</b> An instance that serves no bundles
/// resolves no entitlements, and reporting that as a clean sweep would make the absence of the lane
/// look like the success of it.</para>
/// </summary>
public sealed class EntitlementAnchorHealthCheck(
    PackageEntitlementLedger ledger, PackageOriginAnchor anchor) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // The anchor's own last observation is read WITHOUT triggering a read: a health probe must
        // not be the thing that goes to GitHub, and a probe that could fail on its own I/O would
        // report on itself rather than on the lane.
        var observed = anchor.LastObserved;
        var description = observed is null
            ? $"{ledger.Describe()}; the registry anchor has not been read in this process yet"
            : $"{ledger.Describe()}; {observed.Describe()}";

        var degraded = !ledger.Degraded.IsEmpty
                       || observed is { IsComplete: false, State: not AnchorState.Unconfigured };
        return Task.FromResult(degraded
            ? HealthCheckResult.Degraded(description)
            : HealthCheckResult.Healthy(description));
    }
}
