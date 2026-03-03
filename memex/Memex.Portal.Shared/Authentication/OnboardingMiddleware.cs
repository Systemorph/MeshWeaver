using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Mesh;
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
/// - No user node → create Transient node with claims data → redirect /onboarding
/// - Transient node → redirect /onboarding (resume)
/// - Active node → pass through
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
                var userContext = accessService.Context;

                // Skip virtual users — they don't need onboarding
                if (userContext is { IsVirtual: false } && !string.IsNullOrEmpty(userContext.ObjectId))
                {
                    var meshQuery = portalApp.Hub.ServiceProvider.GetService<IMeshQuery>();
                    if (meshQuery != null)
                    {
                        try
                        {
                            // Use IMeshQuery to bypass security — user is not yet fully authenticated
                            var node = await meshQuery.QueryAsync<MeshNode>(
                                $"path:User/{userContext.ObjectId} scope:self").FirstOrDefaultAsync();

                            if (node == null)
                            {
                                // No node yet — create a Transient node with claims data
                                logger.LogInformation(
                                    "OnboardingMiddleware: Creating transient user node for {UserId}",
                                    userContext.ObjectId);

                                var persistence = portalApp.Hub.ServiceProvider.GetService<IPersistenceService>();
                                if (persistence != null)
                                {
                                    var transientNode = new MeshNode(userContext.ObjectId, "User")
                                    {
                                        Name = userContext.Name ?? userContext.ObjectId,
                                        NodeType = "User",
                                        State = MeshNodeState.Transient,
                                        Content = new Dictionary<string, object?>
                                        {
                                            ["name"] = userContext.Name,
                                            ["email"] = userContext.Email,
                                        }
                                    };

                                    await persistence.SaveNodeAsync(transientNode);
                                }
                                context.Response.Redirect("/onboarding");
                                return;
                            }

                            if (node.State == MeshNodeState.Transient)
                            {
                                // Incomplete onboarding — redirect back
                                logger.LogDebug(
                                    "OnboardingMiddleware: Resuming onboarding for {UserId}",
                                    userContext.ObjectId);
                                context.Response.Redirect("/onboarding");
                                return;
                            }

                            // Active (or other state) — pass through
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
