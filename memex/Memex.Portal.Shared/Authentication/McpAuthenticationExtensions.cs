using Microsoft.AspNetCore.Authentication;
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
            .AddMcp(ConfigureMcpAuth);

        services.AddAuthorization(options =>
        {
            options.AddPolicy(PolicyName, policy =>
            {
                policy.AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            });
        });

        return services;
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
                    AuthorizationServers = { $"{origin}/connect" },
                };
                return Task.CompletedTask;
            },
        };
    }
}
