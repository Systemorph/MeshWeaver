using System.Text.Json;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Middleware that redirects authenticated users without an Active user node
/// to the onboarding page. Runs after UserContextMiddleware.
///
/// Flow:
/// - No user node (or Transient) → redirect /onboarding
/// - Active node → update AccessContext with username, pass through
/// </summary>
public class OnboardingMiddleware(RequestDelegate next, ILogger<OnboardingMiddleware> logger)
{
    private static readonly HashSet<string> ExcludedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/onboarding",
        "/welcome",
        "/login",
        "/auth/",
        "/dev/",
        "/admin/",
        "/_framework",
        "/_content",
        "/_blazor",
        "/static/",
        "/favicon.ico",
        "/mcp",
        "/signin-",
    };

    public async Task InvokeAsync(HttpContext context)
    {
        // Only check authenticated, non-virtual users
        if (context.User?.Identity?.IsAuthenticated == true && !IsExcludedPath(context.Request.Path))
        {
            var portalApp = context.RequestServices.GetService<PortalApplication>();
            if (portalApp != null)
            {
                var accessService = portalApp.Hub.ServiceProvider.GetRequiredService<AccessService>();
                var userContext = accessService.Context ?? accessService.CircuitContext;

                // Skip virtual users — they don't need onboarding
                if (userContext is { IsVirtual: false } && !string.IsNullOrEmpty(userContext.ObjectId))
                {
                    var email = userContext.Email ?? userContext.ObjectId;

                    // If the context's ObjectId was already resolved to a username
                    // (different from the email), this user was onboarded in the current
                    // session. Skip the query — it may not find newly created nodes
                    // immediately due to routing/caching in the mesh query layer.
                    if (!string.IsNullOrEmpty(email) &&
                        !string.IsNullOrEmpty(userContext.ObjectId) &&
                        userContext.ObjectId != email)
                    {
                        await next(context);
                        return;
                    }

                    var meshQuery = portalApp.Hub.ServiceProvider.GetService<IMeshService>();
                    if (meshQuery != null)
                    {
                        try
                        {
                            // Look up User node by email stored in content.
                            // Use ImpersonateAsHub scope because user context may not have
                            // sufficient permissions yet at this point in the pipeline.
                            MeshNode? node;
                            using (accessService.ImpersonateAsHub(portalApp.Hub))
                            {
                                node = await meshQuery.QueryAsync<MeshNode>(
                                    $"nodeType:User namespace:User content.email:{email} limit:1").FirstOrDefaultAsync();
                            }

                            if (node == null || node.State == MeshNodeState.Transient)
                            {
                                // No user node or incomplete onboarding — redirect
                                logger.LogInformation(
                                    "OnboardingMiddleware: Redirecting to onboarding for {Email}",
                                    email);
                                context.Response.Redirect("/onboarding");
                                return;
                            }

                            // Active user — update AccessContext with username (node ID)
                            var username = node.Id;

                            // Query global AccessAssignment to populate roles
                            var roles = await LoadUserRolesAsync(
                                meshQuery, accessService, portalApp.Hub, username);

                            var updatedContext = userContext with
                            {
                                ObjectId = username,
                                Name = node.Name ?? username,
                                Roles = roles
                            };
                            // Set per-request context only. CircuitAccessHandler handles
                            // per-circuit persistence via CreateInboundActivityHandler.
                            accessService.SetContext(updatedContext);
                        }
                        catch (Exception ex)
                        {
                            // Non-critical — don't block the request on onboarding check failure
                            logger.LogWarning(ex,
                                "OnboardingMiddleware: Failed to check user node for {UserId}",
                                userContext.ObjectId);
                        }
                    }
                }
            }
        }

        await next(context);
    }

    /// <summary>
    /// Loads the user's role names from AccessAssignment nodes across all scopes.
    /// Used to populate AccessContext.Roles so permission checks work in Blazor components.
    /// </summary>
    private static async Task<IReadOnlyCollection<string>> LoadUserRolesAsync(
        IMeshService meshQuery, AccessService accessService, IMessageHub hub, string username)
    {
        try
        {
            var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (accessService.ImpersonateAsHub(hub))
            {
                await foreach (var accessNode in meshQuery.QueryAsync<MeshNode>(
                    $"nodeType:AccessAssignment content.accessObject:\"{username}\" scope:subtree limit:10"))
                {
                    if (accessNode.Content == null)
                        continue;

                    AccessAssignment? assignment = accessNode.Content switch
                    {
                        AccessAssignment aa => aa,
                        JsonElement je => JsonSerializer.Deserialize<AccessAssignment>(
                            je.GetRawText(), hub.JsonSerializerOptions),
                        _ => null
                    };

                    if (assignment == null)
                        continue;

                    foreach (var r in assignment.Roles.Where(r => !r.Denied && !string.IsNullOrEmpty(r.Role)))
                        roles.Add(r.Role);
                }
            }

            return roles.ToList();
        }
        catch
        {
            // Non-critical — return empty roles on failure
            return [];
        }
    }

    private static bool IsExcludedPath(PathString path)
    {
        var pathValue = path.Value ?? "";
        foreach (var prefix in ExcludedPrefixes)
        {
            if (pathValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
