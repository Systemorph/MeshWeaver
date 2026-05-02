using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Thin façade over <see cref="OnboardingMiddleware.LoadUserRoles"/> so callers
/// outside this assembly (or callers that don't want to take a dependency on
/// the internal <c>IMeshQueryCore</c> shape) can resolve a user's
/// AccessAssignment-derived roles in one call.
///
/// <para>Used by <see cref="ApiTokenAuthenticationHandler"/> to enrich Bearer
/// principals with DB-resolved roles, so MCP / API-token sessions see the
/// same role set as cookie / OAuth sessions. Without this layer, roles would
/// be limited to whatever was stamped on the API token at creation time —
/// any later AccessAssignment grant would silently not apply for Bearer
/// requests, even though the same user signing in through a browser would
/// see them.</para>
/// </summary>
internal static class UserRoleResolver
{
    /// <summary>
    /// Resolves the user's AccessAssignment-derived role names. Returns an
    /// empty list when no resolution is possible (services missing, query
    /// hub unavailable, mesh-query layer faulted) — auth flows must keep
    /// working even when role enrichment can't.
    /// </summary>
    public static async Task<IReadOnlyCollection<string>> LoadDbRolesAsync(
        IServiceProvider services, string userId)
    {
        var meshQuery = services.GetService<IMeshQueryCore>();
        var hub = services.GetService<IMessageHub>();
        if (meshQuery is null || hub is null || string.IsNullOrEmpty(userId))
            return Array.Empty<string>();

        return await OnboardingMiddleware
            .LoadUserRoles(meshQuery, hub, userId)
            .FirstAsync()
            .ToTask();
    }
}
