using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Memex.Portal.Shared;

/// <summary>
/// The per-browser GUI-shell switch for a deployment serving BOTH shells (<see cref="GuiFeatureOptions"/>):
/// Blazor owns <c>/</c>, Next lives under <c>/next</c> on the same host. A plain browser navigation
/// lands on <see cref="GuiFeatureOptions.Default"/>; <c>?gui=next</c> / <c>?gui=blazor</c> on any
/// page switches — the choice is remembered in a cookie and applied by a REDIRECT (never a proxy:
/// the ingress owns the actual routing to the Next service, exactly as today).
///
/// <para>Only NAVIGATIONS switch: GET, an <c>Accept</c> that wants <c>text/html</c>, and a path
/// outside every non-page surface (APIs, the circuit, static assets, auth). Everything else — the
/// mesh surfaces both shells consume — is untouched, so a Next viewer's same-host REST/gRPC-web
/// calls flow exactly as before.</para>
/// </summary>
public static class GuiShellSwitch
{
    /// <summary>The preference cookie. Values: "next" | "blazor".</summary>
    public const string CookieName = "memex-gui";

    /// <summary>The query parameter that sets (and re-sets) the preference.</summary>
    public const string QueryParam = "gui";

    /// <summary>The Next shell's base path on the shared host (its build bakes <c>basePath: "/next"</c>).</summary>
    public const string NextBasePath = "/next";

    // Non-page surfaces a navigation redirect must never touch. Prefix match on the raw path.
    private static readonly string[] ExcludedPrefixes =
    [
        NextBasePath,
        "/api", "/mcp", "/signalr", "/meshweaver.v1.Mesh",
        "/_blazor", "/_framework", "/_content",
        "/static", "/content", "/images", "/favicon", "/robots.txt", "/manifest",
        "/login", "/logout", "/signin-oidc", "/signout", "/authorize", "/token", "/register",
        "/.well-known", "/healthz", "/alive", "/ready", "/webhooks",
    ];

    /// <summary>
    /// The pure decision: where should this request land, and should the cookie be (re)written?
    /// Exposed for unit tests — the middleware below is just this plus HttpContext plumbing.
    /// </summary>
    /// <returns><c>redirect</c> null = pass through; otherwise the location to 302 to.
    /// <c>setCookie</c> null = leave the cookie alone; otherwise write this value.</returns>
    public static (string? Redirect, string? SetCookie) Decide(
        string method, PathString path, string? acceptHeader, string? queryGui, string? cookieGui,
        string defaultShell)
    {
        // An explicit ?gui= always records the choice, even on excluded paths (the redirect below
        // still only applies to navigations).
        var setCookie = queryGui?.ToLowerInvariant() switch
        {
            "next" => "next",
            "blazor" => "blazor",
            _ => null,
        };

        if (!HttpMethods.IsGet(method))
            return (null, setCookie);
        if (acceptHeader is null || !acceptHeader.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            return (null, setCookie);
        var raw = path.HasValue ? path.Value! : "/";
        foreach (var prefix in ExcludedPrefixes)
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (null, setCookie);

        var effective = setCookie
            ?? cookieGui?.ToLowerInvariant()
            ?? (string.Equals(defaultShell, "Next", StringComparison.OrdinalIgnoreCase) ? "next" : "blazor");
        if (effective != "next")
            return (null, setCookie);

        // Send the navigation to the same path under /next (Next is built with that basePath).
        var target = NextBasePath + (raw == "/" ? "" : raw);
        return (target, setCookie);
    }

    /// <summary>Mounts the switch. Call only when BOTH shells are enabled.</summary>
    public static IApplicationBuilder UseGuiShellSwitch(this IApplicationBuilder app, GuiFeatureOptions gui)
        => app.Use(async (context, nextMiddleware) =>
        {
            var (redirect, setCookie) = Decide(
                context.Request.Method,
                context.Request.Path,
                context.Request.Headers.Accept,
                context.Request.Query[QueryParam],
                context.Request.Cookies[CookieName],
                gui.Default);
            if (setCookie is not null)
                context.Response.Cookies.Append(CookieName, setCookie, new CookieOptions
                {
                    Path = "/",
                    MaxAge = TimeSpan.FromDays(365),
                    HttpOnly = false,     // the shells may read it to render their switch affordance
                    SameSite = SameSiteMode.Lax,
                    // Same policy as the frontend-selection cookie: Secure whenever the request
                    // itself is HTTPS, so the preference never rides plain HTTP on a TLS site.
                    Secure = context.Request.IsHttps,
                });
            if (redirect is not null)
            {
                // Preserve the query, minus the gui parameter itself (it has done its job).
                var query = QueryString.Create(
                    context.Request.Query.Where(kv => !string.Equals(kv.Key, QueryParam, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(kv => kv.Value, (kv, v) => new KeyValuePair<string, string?>(kv.Key, v)));
                context.Response.Redirect(redirect + query, permanent: false);
                return;
            }
            await nextMiddleware();
        });
}
