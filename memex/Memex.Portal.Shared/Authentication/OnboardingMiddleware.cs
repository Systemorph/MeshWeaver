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
                    var meshQuery = portalApp.Hub.ServiceProvider.GetService<IMeshService>();
                    if (meshQuery != null)
                    {
                        try
                        {
                            var email = userContext.Email ?? userContext.ObjectId;

                            // Look up User node by email stored in content
                            var node = await meshQuery.QueryAsync<MeshNode>(
                                $"namespace:User nodeType:User content.email:{email}").FirstOrDefaultAsync();

                            if (node == null)
                            {
                                // No user node yet — create a Transient node to hold claims data
                                logger.LogInformation(
                                    "OnboardingMiddleware: Creating transient user node for {Email}",
                                    email);

                                var nodeFactory = portalApp.Hub.ServiceProvider.GetRequiredService<IMeshService>();
                                var transientNode = new MeshNode(email, "User")
                                {
                                    Name = userContext.Name ?? email,
                                    NodeType = "User",
                                    State = MeshNodeState.Transient,
                                    Content = new Dictionary<string, object?>
                                    {
                                        ["email"] = email,
                                    }
                                };

                                await nodeFactory.CreateTransientAsync(transientNode);
                                context.Response.Redirect("/onboarding");
                                return;
                            }

                            if (node.State == MeshNodeState.Transient)
                            {
                                // Incomplete onboarding — redirect back
                                logger.LogDebug(
                                    "OnboardingMiddleware: Resuming onboarding for {Email}",
                                    email);
                                context.Response.Redirect("/onboarding");
                                return;
                            }

                            // Active user — update AccessContext with username (node ID)
                            var username = node.Id;
                            var updatedContext = userContext with
                            {
                                ObjectId = username,
                                Name = username
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
