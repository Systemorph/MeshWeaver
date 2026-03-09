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
                    var meshQuery = portalApp.Hub.ServiceProvider.GetService<IMeshService>();
                    if (meshQuery != null)
                    {
                        try
                        {
                            var email = userContext.Email ?? userContext.ObjectId;

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
                            var updatedContext = userContext with
                            {
                                ObjectId = username,
                                Name = node.Name ?? username
                            };
                            accessService.SetContext(updatedContext);
                            accessService.SetCircuitContext(updatedContext);
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
