using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Separate auth wiring for the MCP endpoint.
///
/// The Blazor portal uses cookie-based auth with a redirect-to-login challenge, which is
/// correct for browser users but fatal for MCP clients: Claude Desktop / Claude.ai follow
/// a 302 to an HTML login page and fail with "couldn't reach the server" instead of doing
/// OAuth discovery.
///
/// MCP auth must be strictly Bearer-only:
///   * token validation goes to <see cref="ApiTokenAuthenticationHandler"/>
///   * unauthed requests get 401 + <c>WWW-Authenticate: Bearer resource_metadata="..."</c>
///     emitted by the MCP SDK's own scheme, so clients can run OAuth discovery
///   * no leakage to cookie — no redirects, ever
/// </summary>
public static class McpAuthenticationExtensions
{
    public const string PolicyName = "McpAuth";

    /// <summary>
    /// Authorization policy for the READ-ONLY <c>/api/mesh</c> verbs: an
    /// <c>Authorization: Bearer mw_…</c> token OR the portal's own session cookie.
    ///
    /// <para>
    /// 🚨 Why a browser session must be enough here: a server-side renderer that already
    /// holds the user's session cookie (portal-next SSR) otherwise has to MINT an API token
    /// per page render just to read a snapshot back — and every mint writes TWO permanent
    /// mesh nodes (<c>{userId}/ApiToken/{hash}</c> + the global <c>ApiToken/{hash}</c> index),
    /// so ordinary page traffic grew a user's partition without bound (issue #1477). The
    /// credential the caller already has must be sufficient to READ.
    /// </para>
    ///
    /// <para>
    /// 🚨 READ-ONLY on purpose. Every MUTATING verb stays on <see cref="PolicyName"/>
    /// (Bearer-only), so a cookie can never drive a write. That keeps the CSRF surface at
    /// zero by construction rather than by argument: the worst a forged cross-site request
    /// could do against these endpoints is cause a read whose response the attacker cannot
    /// see (no CORS grant), and the session cookie is <c>SameSite=Lax</c> so it is not even
    /// sent on a cross-site POST. Adding a verb here is a security decision — only pure
    /// reads belong.
    /// </para>
    /// </summary>
    public const string ReadPolicyName = "MeshApiRead";

    /// <summary>
    /// The composite authentication scheme behind <see cref="ReadPolicyName"/>: a policy
    /// scheme that forwards AUTHENTICATION to the Bearer/ApiToken scheme when the request
    /// carries an <c>Authorization: Bearer</c> header and to the cookie scheme otherwise —
    /// but forwards every CHALLENGE to the MCP scheme unconditionally.
    ///
    /// <para>
    /// The challenge split is the load-bearing part. Letting the cookie scheme challenge
    /// would answer an unauthenticated API call with <c>302 → /login</c> and an HTML page,
    /// which is exactly the failure this file exists to prevent (see the type remarks): a
    /// REST/MCP client sees a 200 full of markup instead of a 401 it can act on. Forwarding
    /// the challenge keeps the API-shaped <c>401 + WWW-Authenticate: Bearer</c> for every
    /// anonymous caller, cookie-capable or not.
    /// </para>
    /// </summary>
    public const string CookieOrBearerScheme = "MeshApiCookieOrBearer";

    /// <summary>
    /// Registers the ApiToken + MCP authentication schemes and the <c>McpAuth</c>
    /// authorization policy. Call after the primary (cookie / OIDC) auth has been
    /// registered — this adds to the existing authentication builder without
    /// touching its defaults.
    /// </summary>
    public static IServiceCollection AddMcpAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
                ApiTokenAuthenticationHandler.SchemeName, _ => { })
            .AddMcp(ConfigureMcpAuth)
            .AddPolicyScheme(CookieOrBearerScheme, CookieOrBearerScheme, ConfigureCookieOrBearer);

        services.AddAuthorization(options =>
        {
            options.AddPolicy(PolicyName, policy =>
            {
                policy.AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            });

            options.AddPolicy(ReadPolicyName, policy =>
            {
                policy.AddAuthenticationSchemes(CookieOrBearerScheme);
                policy.RequireAuthenticatedUser();
            });
        });

        return services;
    }

    /// <summary>
    /// Wires <see cref="CookieOrBearerScheme"/>: pick the scheme by what the request
    /// actually presents, and never let the cookie scheme write the challenge.
    /// </summary>
    private static void ConfigureCookieOrBearer(PolicySchemeOptions options)
    {
        options.ForwardDefaultSelector = context =>
            context.Request.Headers.Authorization.ToString()
                .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? McpAuthenticationDefaults.AuthenticationScheme
                : CookieAuthenticationDefaults.AuthenticationScheme;

        // 🚨 NEVER the cookie challenge — see CookieOrBearerScheme's remarks. Both auth
        // modes the portal composes (unified cookie, and MicrosoftIdentity's OIDC + cookie
        // sign-in) register CookieAuthenticationDefaults.AuthenticationScheme, so the
        // selector above always names a scheme that exists; the challenge, however, has to
        // stay API-shaped regardless of which one authenticated.
        options.ForwardChallenge = McpAuthenticationDefaults.AuthenticationScheme;
        options.ForwardForbid = McpAuthenticationDefaults.AuthenticationScheme;
    }

    private static void ConfigureMcpAuth(McpAuthenticationOptions options)
    {
        // Bearer token validation → ApiToken handler. The MCP SDK constructor hardcodes
        // ForwardAuthenticate = "Bearer" (a scheme that doesn't exist here); point it at
        // the real scheme so token validation actually runs.
        options.ForwardAuthenticate = ApiTokenAuthenticationHandler.SchemeName;

        // Leave Challenge on the MCP scheme itself so it emits
        // 401 + WWW-Authenticate: Bearer resource_metadata="..." — that's what lets
        // MCP clients discover the auth server. NEVER forward to cookie: that would
        // produce a 302 to /login which MCP clients can't follow.
        options.ForwardChallenge = null;
        options.ForwardForbid = null;
        options.ForwardDefaultSelector = null;

        options.ResourceMetadata = new ProtectedResourceMetadata
        {
            BearerMethodsSupported = { "header" },
            ScopesSupported = { "mcp" },
        };

        options.Events = new McpAuthenticationEvents
        {
            OnResourceMetadataRequest = ctx =>
            {
                var req = ctx.HttpContext.Request;
                var origin = $"{req.Scheme}://{req.Host}";
                ctx.ResourceMetadata = new ProtectedResourceMetadata
                {
                    Resource = $"{origin}/mcp",
                    BearerMethodsSupported = { "header" },
                    ScopesSupported = { "mcp" },
                    AuthorizationServers = { origin },
                };
                return Task.CompletedTask;
            },
        };
    }
}
