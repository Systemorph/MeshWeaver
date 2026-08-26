using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.AspNetCore.Portal;

/// <summary>
/// Route constraint that excludes paths for static file prefixes used by Blazor and RCLs,
/// plus infrastructure endpoints (auth, dev, mcp, OAuth callbacks). Applied to the root
/// catch-all page endpoint via <see cref="NonfileRouteConstraintExtensions.ExcludeStaticAssetPaths{TBuilder}"/>
/// so that requests under these prefixes are never swallowed by the Blazor application page
/// (a miss falls through to a proper 404 instead of an HTML shell).
///
/// 🚨 Deliberately NOT wired as an inline ":nonfile" template constraint: the Blazor Router
/// component resolves inline constraint names against its own compiled-in map
/// (int, bool, ..., file, nonfile) where "nonfile" is the built-in dot-rejecting
/// NonFileNameRouteConstraint, and that map cannot be customized. An inline ":nonfile"
/// therefore rejects every mesh path whose last segment contains a dot (all Document
/// nodes: .pdf/.docx/.txt) in the interactive circuit — the agentic-pensions#10 regression.
/// Constrain the endpoint, never the page template.
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

        if (ExcludedPrefixes.Contains(firstSegment.ToString()))
            return false;

        // 🚨 A path that IS a real file in webroot is never a page route. The prefix list above
        // only covers the KNOWN asset roots (_framework, _content, …); anything else physically
        // present — a hand-dropped wwwroot file, a published SPA bundle, an asset outside the
        // build-time manifest — would otherwise match this catch-all, and since the static-file
        // middleware runs AFTER routing (so MapStaticAssets can serve the pre-compressed copies)
        // it SKIPS a request whose endpoint is already selected. The file would then be answered
        // with the Blazor shell: HTTP 200, wrong bytes, no error anywhere.
        //
        // Deliberately an existence check and NOT a "does the last segment contain a dot" test:
        // mesh Document nodes legitimately end in .pdf/.docx/.txt, and rejecting dotted paths is
        // exactly the agentic-pensions#10 regression this class was written to fix.
        return !FileExistsInWebRoot(httpContext, path);
    }

    /// <summary>True when <paramref name="path"/> resolves to a file that physically exists in the
    /// host's web root. Absent an <see cref="IWebHostEnvironment"/> (constraint evaluated outside a
    /// request), the answer is false — the prefix rules alone then decide, exactly as before.</summary>
    private static bool FileExistsInWebRoot(HttpContext? httpContext, string path)
    {
        var webRoot = httpContext?.RequestServices?.GetService<IWebHostEnvironment>()?.WebRootFileProvider;
        if (webRoot is null || path.Length == 0)
            return false;
        try
        {
            return webRoot.GetFileInfo(path).Exists;
        }
        catch (Exception)
        {
            // A malformed or traversal-shaped path makes the provider throw; that is not a file,
            // and a routing constraint must never be the thing that 500s a request.
            return false;
        }
    }
}

/// <summary>
/// Extension methods for applying <see cref="NonfileRouteConstraint"/> to razor-component endpoints.
/// </summary>
public static class NonfileRouteConstraintExtensions
{
    /// <summary>
    /// Applies <see cref="NonfileRouteConstraint"/> to the root catch-all page endpoint
    /// (ApplicationPage's <c>/{**Path}</c>) so static-asset and infrastructure prefixes
    /// (<c>_framework</c>, <c>_content</c>, <c>favicon.ico</c>, <c>auth</c>, <c>mcp</c>, ...)
    /// are not swallowed by the Blazor application page: a request under those prefixes that
    /// no static file or explicit endpoint handles falls through to 404 instead of rendering
    /// the HTML shell. Chain on the builder returned by <c>MapRazorComponents</c>.
    ///
    /// This is an endpoint convention, NOT an inline template constraint, because only the
    /// ASP.NET endpoint layer honors custom constraints — the Blazor Router's inline-constraint
    /// map is compiled-in and would interpret ":nonfile" as the built-in dot-rejecting
    /// constraint, breaking every mesh path that ends in a file extension.
    /// </summary>
    public static TBuilder ExcludeStaticAssetPaths<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            if (endpointBuilder is not RouteEndpointBuilder routeEndpointBuilder)
                return;

            // Target only the root catch-all page ("/{**Path}"): excluded prefixes are
            // root-anchored, so sub-path catch-alls (/area, /article, /content, ...) can
            // never collide with static assets and stay unconstrained.
            var pattern = routeEndpointBuilder.RoutePattern;
            if (pattern.RawText is not { } rawText
                || pattern.PathSegments.Count != 1
                || pattern.PathSegments[0].Parts.Count != 1
                || pattern.PathSegments[0].Parts[0] is not RoutePatternParameterPart { IsCatchAll: true } parameter)
                return;

            routeEndpointBuilder.RoutePattern = RoutePatternFactory.Parse(
                rawText,
                defaults: null,
                parameterPolicies: new RouteValueDictionary { [parameter.Name] = new NonfileRouteConstraint() });
        });
        return builder;
    }
}
