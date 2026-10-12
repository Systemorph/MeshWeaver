using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.ServiceDefaults;

/// <summary>
/// The <c>/health</c> entry over the build principals this deployment DECLARES in configuration
/// (<see cref="DeclaredBuildPrincipals"/>).
///
/// <para>🚨 <b>A declared trust must be visible.</b> A build principal used to exist only as a node,
/// so <c>search nodeType:BuildPrincipal</c> was the complete list of repositories an instance
/// trusts. A declared principal is in no node, so this entry names it — repository, both immutable
/// ids, each event with its ref allow-list, and the scopes — to anyone who can read <c>/health</c>,
/// which is public. It names what the DECLARATION says; nodes are not listed here.</para>
///
/// <para>Census-tagged, so it prints whatever its status: "nothing is declared" and "one is
/// declared" are both facts a reader needs. Degraded — never Unhealthy, and no probe tag — when a
/// declaration was refused (it is trusted for nothing, and the entry names the rule it broke) or
/// when principals are declared but no audience is, which leaves the whole leg off. Neither is a
/// reason to pull a pod: the cost is a build that is told 401.</para>
/// </summary>
public sealed class BuildPrincipalHealthCheck(IConfiguration configuration) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Evaluate(configuration));

    /// <summary>The entry's verdict for a configuration. Pure.</summary>
    /// <param name="configuration">The deployment's configuration.</param>
    /// <returns>Degraded on a refused declaration or a declared principal with no audience;
    /// Healthy with the reading otherwise.</returns>
    public static HealthCheckResult Evaluate(IConfiguration configuration)
    {
        var (degraded, description) = DeclaredBuildPrincipals.Describe(
            DeclaredBuildPrincipals.Read(configuration),
            DeclaredBuildPrincipals.Audiences(configuration));
        return degraded
            ? HealthCheckResult.Degraded(description)
            : HealthCheckResult.Healthy(description);
    }
}
