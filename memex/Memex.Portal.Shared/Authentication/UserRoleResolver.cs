using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Thin façade over <c>OnboardingMiddleware.LoadUserRoles</c> so callers
/// outside this assembly can resolve a user's AccessAssignment-derived roles in
/// one call.
///
/// <para>Used by <see cref="ApiTokenAuthenticationHandler"/> to enrich Bearer
/// principals with DB-resolved roles, so MCP / API-token sessions see the
/// same role set as cookie / OAuth sessions. Without this layer, roles would
/// be limited to whatever was stamped on the API token at creation time —
/// any later AccessAssignment grant would silently not apply for Bearer
/// requests, even though the same user logging in through a browser would
/// see them.</para>
///
/// <para>Resolution goes through the canonical synced-query API
/// (<c>workspace.GetQuery</c>) — same path-keyed dedup + Initial gating + static
/// provider fan-out as every other live mesh-node collection consumer in the
/// codebase. Direct <c>IMeshQueryCore.Query</c> calls from auth code
/// were a pedestrian-query antipattern (replaced 2026-05).</para>
/// </summary>
internal static class UserRoleResolver
{
    /// <summary>
    /// Resolves the user's AccessAssignment-derived role names, keeping "there are no extra
    /// roles" apart from "the role store could not be read" (issue #637).
    ///
    /// <list type="bullet">
    ///   <item><description><c>Resolved(roles)</c> — the read completed; an empty set means the
    ///     user genuinely has no AccessAssignment grants.</description></item>
    ///   <item><description><c>Unavailable(reason)</c> — the read stalled or faulted. The caller
    ///     must answer retryable rather than authenticate a silently under-privileged principal,
    ///     whose every later request would be denied with a misleading "Access denied".</description></item>
    /// </list>
    ///
    /// <para>No role SOURCE at all (no hub / no workspace / no user id) is <c>Resolved(empty)</c>,
    /// not Unavailable: that is a static configuration fact — there is nothing to enrich from —
    /// not a transient outage, and it must not turn every request into a 503.</para>
    ///
    /// <para>The single Task bridge here lives at the ASP.NET
    /// <c>AuthenticationHandler.HandleAuthenticateAsync</c> boundary —
    /// callers expect a Task-returning helper, but everything below
    /// stays observable.</para>
    /// </summary>
    public static async Task<IdentityReadOutcome<IReadOnlyCollection<string>>> LoadDbRolesAsync(
        IServiceProvider services, string userId)
    {
        var hub = services.GetService<IMessageHub>();
        if (hub is null || string.IsNullOrEmpty(userId))
            return IdentityReadOutcome<IReadOnlyCollection<string>>.Resolved(Array.Empty<string>());

        var workspace = hub.GetWorkspace();
        if (workspace is null)
            return IdentityReadOutcome<IReadOnlyCollection<string>>.Resolved(Array.Empty<string>());

        // LoadUserRoles classifies its own stalls/faults — no catch needed here, and none
        // wanted: swallowing would recreate the collapse this method exists to prevent.
        return await OnboardingMiddleware
            .LoadUserRoles(workspace, userId)
            .FirstAsync()
            .ToTask();
    }
}
