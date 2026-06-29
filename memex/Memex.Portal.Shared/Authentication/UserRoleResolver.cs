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
    /// Resolves the user's AccessAssignment-derived role names. Returns an
    /// empty list when no resolution is possible (services missing, workspace
    /// unavailable, query layer faulted) — auth flows must keep working even
    /// when role enrichment can't.
    ///
    /// <para>The single Task bridge here lives at the ASP.NET
    /// <c>AuthenticationHandler.HandleAuthenticateAsync</c> boundary —
    /// callers expect a Task-returning helper, but everything below
    /// stays observable.</para>
    /// </summary>
    public static async Task<IReadOnlyCollection<string>> LoadDbRolesAsync(
        IServiceProvider services, string userId)
    {
        var hub = services.GetService<IMessageHub>();
        if (hub is null || string.IsNullOrEmpty(userId))
            return Array.Empty<string>();

        var workspace = hub.GetWorkspace();
        if (workspace is null)
            return Array.Empty<string>();

        return await OnboardingMiddleware
            .LoadUserRoles(workspace, userId)
            .FirstAsync()
            .ToTask();
    }
}
