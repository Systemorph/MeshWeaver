using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MeshWeaver.Mesh;

/// <summary>One root forwarder no lease can resolve (<see cref="ModuleContexts.UnresolvableForwarders"/>).</summary>
/// <param name="Module">The module's entry-assembly name.</param>
/// <param name="Location">The entry DLL of the generation that is current — the one every lease asks.</param>
/// <param name="Reason">Which forwarder, which service, and why it cannot be resolved there.</param>
public sealed record ModuleForwarderGap(string Module, string Location, string Reason);

/// <summary>
/// 🚦 <b>Readiness = every root forwarder of every module resolves in that module's CURRENT
/// generation</b> (MeshWeaver#6847).
///
/// <para><b>The incident.</b> On memex.systemorph.com, 2026-10-16 00:15Z → ~07:00Z, a live swap of
/// WhatsApp 1.3.2 → 1.4.2 moved the index of a forwarded registration. Every hub activation that leased
/// the module's services through the root then faulted with
/// <c>'mw-module-service:10 (MeshWeaver.WhatsApp.IWhatsAppRecentChats)' has not been registered</c>,
/// sign-in included. <c>/health</c> said Degraded — with the exception quoted in two unrelated entries —
/// for six and a half hours while every pod stayed READY, so nothing took the pods out of rotation,
/// nothing held a roll and nothing paged. The swap itself is now refused (<c>ModuleServices.Shape</c>
/// carries the index); this check is what makes the CLASS loud if any other path ever reaches it.</para>
///
/// <para><b>What it reads, and why one reading is enough.</b> <see cref="ModuleContexts.UnresolvableForwarders"/>
/// asks each current generation, for every forwarder the root was laid out with at boot, whether that
/// generation can answer it — the same registration index, the same forwarded service, a registration under
/// the scope key in its container. A built container never changes, so a forwarder that cannot be resolved
/// now fails EVERY lease for as long as that generation is current: the reading is sustained by
/// construction, not by a count of failures over a window, and it clears exactly when a different
/// generation becomes current or the process restarts. A transient lease failure — a retiring generation,
/// a disposed scope, a slow constructor — is not this shape and is never read here, so one cannot flap
/// readiness. It resolves nothing: no module code runs and no module container's lock is taken, which
/// keeps <c>/ready</c> light.</para>
///
/// <para><b>Why readiness.</b> A replica in this state fails deterministically for every request that
/// needs the module; its siblings that BOOTED on the new generation (or never swapped) serve it. So it
/// leaves the Service, a roll whose new pods are in this state stalls with the previous ones serving, and
/// the fleet watch names it. Not liveness: a restart is the remedy, but the decision to take it belongs to
/// the restart path, not to a probe that would kill every replica of a fleet that swapped together.</para>
///
/// <para>Registered by <c>Memex.Portal.ServiceDefaults</c> on the readiness and census tags, so its clean
/// reading — how many forwarders over how many modules — prints on <c>/health</c> too.</para>
/// </summary>
public sealed class ModuleForwarderHealthCheck(IServiceProvider services) : IHealthCheck
{
    /// <summary>The health check's registration name.</summary>
    public const string HealthCheckName = "module_forwarders";

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Evaluate(services.GetService<ModuleContexts>()));

    /// <summary>The verdict over <paramref name="contexts"/> — null when the host holds no module registry.</summary>
    /// <param name="contexts">The mesh's module registry, or null.</param>
    public static HealthCheckResult Evaluate(ModuleContexts? contexts)
    {
        if (contexts is null)
            return HealthCheckResult.Healthy("this host holds no module registry — no module forwards a service to the root");
        var gaps = contexts.UnresolvableForwarders();
        if (!gaps.IsEmpty)
            return HealthCheckResult.Unhealthy(
                $"{gaps.Count} root forwarder(s) cannot be resolved in their module's current generation, so every "
                + "hub activation and request that leases them fails until the generation changes or this replica "
                + "restarts (a fresh boot lays the forwarders out for the generation it loads): "
                + string.Join("; ", gaps.Select(g => $"{g.Module} ({g.Location}): {g.Reason}")));
        var held = contexts.Generations.Where(g => g.Services is not null).ToArray();
        return HealthCheckResult.Healthy(
            $"{held.Sum(g => g.Services!.ForwarderLayout.Count)} root forwarder(s) over {held.Length} module(s) "
            + "each resolve in their module's current generation");
    }
}
