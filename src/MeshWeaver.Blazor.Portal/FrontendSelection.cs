using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Portal;

/// <summary>
/// Frontend selection — choose the portal frontend per deployment and per user:
/// <list type="bullet">
/// <item><c>Portal:Frontend</c> — the deployment default: <c>"Blazor"</c> (default) or <c>"React"</c>.</item>
/// <item><c>Portal:ReactAppUrl</c> — where the React app is served (e.g. <c>/app/</c> or an absolute
/// URL). The whole feature is inert until this is configured, so existing deployments are unaffected.</item>
/// <item>Per-user override — the <c>mw-frontend</c> cookie, set via the <c>GET /frontend/{target}</c>
/// endpoint (<c>react</c> | <c>blazor</c> | <c>clear</c>). Theme and similar user preferences are
/// client-side (localStorage via <c>FluentDesignTheme</c>); there is no server-side per-user
/// preference store, so the override deliberately follows the same client-side pattern: a plain
/// (non-HttpOnly) cookie the shell reads at the HTTP layer and the React app can read/clear too.</item>
/// </list>
/// The shell checks the effective choice in <see cref="UseFrontendSelection"/> (registered by the
/// portal host before static files/routing): interactive HTML GET navigations are redirected to the
/// React app when the effective frontend is React. Everything non-navigational (Blazor circuit,
/// static assets, MCP/API/SignalR, auth flows, the /frontend endpoints themselves) passes through.
/// The toggle is fully reversible from both sides: the Blazor user menu links to
/// <c>/frontend/react</c> ("Try the new frontend"), the React shell links to <c>/frontend/blazor</c>
/// ("Back to classic").
/// </summary>
public static class FrontendSelection
{
    /// <summary>The per-user override cookie. Values: <see cref="React"/> or <see cref="Blazor"/>.</summary>
    public const string CookieName = "mw-frontend";

    /// <summary>The React frontend selector value.</summary>
    public const string React = "React";

    /// <summary>The classic Blazor frontend selector value (the default).</summary>
    public const string Blazor = "Blazor";

    /// <summary>Configuration key for the deployment-default frontend ("Blazor" | "React").</summary>
    public const string FrontendKey = "Portal:Frontend";

    /// <summary>Configuration key for the React app URL. Empty/unset disables the feature.</summary>
    public const string ReactAppUrlKey = "Portal:ReactAppUrl";

    /// <summary>Route prefix of the selection endpoint (<c>/frontend/{target}</c>).</summary>
    public const string EndpointPrefix = "/frontend";

    // Never bounce anything that is not an interactive page navigation: framework/static assets,
    // the Blazor circuit, mesh transports (MCP/REST/SignalR/gRPC), health probes, auth flows, and
    // the selection endpoint itself.
    private static readonly string[] ExcludedPrefixes =
    [
        "/_framework", "/_content", "/_blazor", "/static/", "/favicon.ico", "/mcp", "/bootstrap",
        "/healthz", "/api", "/signalr", "/connect", "/frontend", "/signin", "/signout", "/login",
        "/logout", "/MicrosoftIdentity", "/authentication",
    ];

    /// <summary>Whether frontend selection is configured for this deployment (a React app URL exists).</summary>
    /// <param name="configuration">The application configuration.</param>
    public static bool IsEnabled(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration[ReactAppUrlKey]);

    /// <summary>
    /// The effective frontend for the request: the user's cookie override when present, else the
    /// deployment default (<c>Portal:Frontend</c>), else <see cref="Blazor"/>.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="configuration">The application configuration.</param>
    public static string EffectiveFrontend(HttpContext context, IConfiguration configuration)
    {
        var choice = context.Request.Cookies[CookieName];
        if (string.IsNullOrWhiteSpace(choice))
            choice = configuration[FrontendKey];
        return string.Equals(choice, React, StringComparison.OrdinalIgnoreCase) ? React : Blazor;
    }

    /// <summary>
    /// Whether this request is an interactive HTML page navigation that should be redirected to the
    /// React app (feature enabled + effective frontend React + not an excluded/asset/React path).
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="configuration">The application configuration.</param>
    public static bool ShouldRedirectToReact(HttpContext context, IConfiguration configuration)
    {
        if (!IsEnabled(configuration))
            return false;
        if (!HttpMethods.IsGet(context.Request.Method))
            return false;

        var path = context.Request.Path.Value ?? "/";
        if (ExcludedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return false;
        // Static assets carry an extension (".css", ".js", ".png", ...) — only bounce extensionless
        // page routes.
        if (Path.HasExtension(path))
            return false;

        // Never bounce a request already inside the React app (when co-hosted under a relative base).
        var reactUrl = configuration[ReactAppUrlKey]!;
        if (reactUrl.StartsWith('/')
            && path.StartsWith(reactUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return false;

        // Only interactive navigations (the browser asks for text/html); XHR/fetch pass through.
        if (!context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
            return false;

        return EffectiveFrontend(context, configuration) == React;
    }

    /// <summary>
    /// Middleware: redirect interactive page navigations to the React app when the effective
    /// frontend (deployment default + per-user cookie override) is React. Register before static
    /// files/routing so the check runs on every navigation; it is a no-op unless
    /// <c>Portal:ReactAppUrl</c> is configured.
    /// </summary>
    /// <param name="app">The application pipeline builder.</param>
    public static IApplicationBuilder UseFrontendSelection(this IApplicationBuilder app)
        => app.Use((context, next) =>
        {
            var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
            if (ShouldRedirectToReact(context, configuration))
            {
                context.Response.Redirect(configuration[ReactAppUrlKey]!);
                return Task.CompletedTask;
            }
            return next();
        });

    /// <summary>
    /// Maps <c>GET /frontend/{target}</c> — the reversible toggle both shells link to:
    /// <c>react</c> sets the override cookie and redirects to the React app, <c>blazor</c> sets it
    /// back and redirects to the classic shell, <c>clear</c> drops the override (deployment default
    /// applies again).
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    public static IEndpointRouteBuilder MapFrontendSelection(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(EndpointPrefix + "/{target}", (HttpContext context, string target) =>
        {
            var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
            // Deliberately NOT HttpOnly: the React app reads/clears the same preference client-side
            // (the mirror of the theme's localStorage pattern).
            var options = new CookieOptions
            {
                Path = "/",
                MaxAge = TimeSpan.FromDays(365),
                SameSite = SameSiteMode.Lax,
                HttpOnly = false,
                Secure = context.Request.IsHttps,
            };
            switch (target.ToLowerInvariant())
            {
                case "react":
                    context.Response.Cookies.Append(CookieName, React, options);
                    return Results.Redirect(configuration[ReactAppUrlKey] ?? "/");
                case "blazor":
                    context.Response.Cookies.Append(CookieName, Blazor, options);
                    return Results.Redirect("/");
                case "clear":
                    context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
                    return Results.Redirect("/");
                default:
                    return Results.BadRequest($"Unknown frontend '{target}' — use react, blazor, or clear.");
            }
        });
        return endpoints;
    }
}
