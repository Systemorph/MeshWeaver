using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Infrastructure;

/// <summary>
/// Route constraint that excludes paths for static file prefixes used by Blazor and RCLs.
/// Use with catch-all routes like "/{*Path:nonfile}" to prevent matching _framework, _content, etc.
/// </summary>
public class NonfileRouteConstraint : IRouteConstraint
{
    // Prefixes used by Blazor and Razor Class Libraries for static content
    private static readonly string[] StaticPrefixes =
    [
        "_framework",
        "_content",
        "_blazor",
        "favicon.ico"
    ];

    public bool Match(
        HttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        if (!values.TryGetValue(routeKey, out var value) || value is not string path)
            return true; // Allow empty paths

        // Check if path starts with any static prefix
        foreach (var prefix in StaticPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"NonfileRouteConstraint: Rejecting '{path}' (matches prefix '{prefix}')");
                return false; // Don't match - let static file middleware handle it
            }
        }

        System.Diagnostics.Debug.WriteLine($"NonfileRouteConstraint: Accepting '{path}'");
        return true; // Allow the route to match
    }
}

/// <summary>
/// Extension methods for registering the nonfile route constraint.
/// </summary>
public static class NonfileRouteConstraintExtensions
{
    /// <summary>
    /// Registers the "nonfile" route constraint that excludes static file paths.
    /// </summary>
    public static IServiceCollection AddNonfileRouteConstraint(this IServiceCollection services)
    {
        services.AddRouting(options =>
        {
            options.ConstraintMap["nonfile"] = typeof(NonfileRouteConstraint);
        });
        return services;
    }
}
