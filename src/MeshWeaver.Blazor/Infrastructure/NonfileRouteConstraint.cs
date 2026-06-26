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
    private static readonly HashSet<string> ExcludedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "_framework", "_content", "_blazor", "favicon.ico",
        "auth", "dev", "mcp",
        "signin-microsoft", "signin-google", "signin-linkedin", "signin-apple"
    };

    /// <summary>
    /// Returns false when the route value for <paramref name="routeKey"/> begins with an excluded prefix;
    /// returns true otherwise, allowing the route to match.
    /// </summary>
    /// <param name="httpContext">The current HTTP context (may be null during constraint evaluation).</param>
    /// <param name="route">The router that defined this constraint.</param>
    /// <param name="routeKey">The name of the route value to inspect.</param>
    /// <param name="values">The route values for the current request.</param>
    /// <param name="routeDirection">Indicates whether the constraint is evaluated for incoming or outgoing routes.</param>
    /// <returns>True when the path does not start with an excluded static-file prefix; false otherwise.</returns>
    public bool Match(
        HttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        if (!values.TryGetValue(routeKey, out var value) || value is not string path)
            return true;

        var firstSegment = path.AsSpan();
        var slashIndex = path.IndexOf('/');
        if (slashIndex >= 0)
            firstSegment = firstSegment[..slashIndex];

        return !ExcludedPrefixes.Contains(firstSegment.ToString());
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
