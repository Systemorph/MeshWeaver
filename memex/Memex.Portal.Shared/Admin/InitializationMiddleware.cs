using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Admin;

/// <summary>
/// Middleware that redirects to the setup wizard when the application
/// is not yet initialized (no Admin/Initialization node exists).
/// Must run after authentication middleware but before OnboardingMiddleware.
/// </summary>
public class InitializationMiddleware(RequestDelegate next, ILogger<InitializationMiddleware> logger)
{
    private static readonly HashSet<string> ExcludedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/admin/setup",
        "/login",
        "/auth/",
        "/dev/",
        "/_framework",
        "/_content",
        "/_blazor",
        "/static/",
        "/favicon.ico",
        "/mcp",
    };

    private volatile bool _isInitialized;

    public async Task InvokeAsync(HttpContext context)
    {
        // Fast path: once initialized, never check again until restart
        if (_isInitialized)
        {
            await next(context);
            return;
        }

        if (IsExcludedPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        var portalApp = context.RequestServices.GetService<PortalApplication>();
        if (portalApp != null)
        {
            var persistence = portalApp.Hub.ServiceProvider.GetService<IPersistenceService>();
            if (persistence != null)
            {
                try
                {
                    var adminService = new AdminService(persistence);
                    if (await adminService.IsInitializedAsync())
                    {
                        _isInitialized = true;
                        await next(context);
                        return;
                    }

                    // Not initialized — redirect to setup
                    logger.LogInformation("App not initialized, redirecting to setup wizard");
                    context.Response.Redirect("/admin/setup");
                    return;
                }
                catch (Exception ex)
                {
                    // Non-critical — don't block the request on initialization check failure
                    logger.LogWarning(ex, "InitializationMiddleware: Failed to check initialization state");
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
