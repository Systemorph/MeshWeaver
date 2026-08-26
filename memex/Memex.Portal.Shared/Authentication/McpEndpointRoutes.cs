using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// The portal's MCP endpoint path contract: <c>/api/mcp</c> is the PRIMARY published
/// URL, <c>/mcp</c> is the permanent compatibility alias every existing client config points at
/// (Claude Code harnesses, plugin MCP servers, <c>IMcpBackConnection</c> consumers,
/// <c>McpRemoteMeshClient</c> mirrors). Both serve the identical endpoint.
///
/// <para><b>How the alias works.</b> The MeshWeaver.Mcp module maps its streamable-HTTP
/// transport at <c>/mcp</c> (via <c>MapMeshModuleEndpoints</c>). <see cref="Middleware"/> —
/// self-wired through an <see cref="IStartupFilter"/> registered by
/// <see cref="McpAuthenticationExtensions.AddMcpAuthentication"/>, so it runs at the very FRONT
/// of the pipeline — rewrites <c>/api/mcp[/…]</c> to <c>/mcp[/…]</c> before routing. Everything
/// downstream (the MCP auth policy, the middleware exclusion lists, the module's route) then
/// treats <c>/api/mcp</c> traffic byte-for-byte like <c>/mcp</c> traffic. This deliberately
/// does NOT depend on the module's own mapping moving: the alias works against every module
/// generation, and there is no cross-repo ordering window where <c>/api/mcp</c> maps to
/// nothing. If the module's pattern ever moves to <c>/api/mcp</c> natively, INVERT this rewrite
/// in the same change (<c>/mcp</c> → <c>/api/mcp</c>) — never map both without removing it, or
/// the module-endpoint collision check refuses startup.</para>
///
/// <para><b>OAuth discovery (RFC 9728).</b> A strict MCP client derives the path-inserted
/// protected-resource-metadata URL from the endpoint it connects to:
/// <c>/.well-known/oauth-protected-resource/api/mcp</c> for the primary,
/// <c>…/mcp</c> for the alias. The MCP SDK serves ONE metadata document at the bare
/// <c>/.well-known/oauth-protected-resource</c>, so the middleware rewrites both path-inserted
/// forms onto the bare path and stashes the resource path in
/// <see cref="ResourcePathItem"/>; <c>OnResourceMetadataRequest</c> (which runs AFTER
/// <c>UseForwardedHeaders</c>, so its origin is the public one) reads the stash and answers
/// with the matching <c>resource</c> value. The bare document keeps answering
/// <c>{origin}/mcp</c> — the value every already-connected client validated against.</para>
/// </summary>
public static class McpEndpointRoutes
{
    /// <summary>The primary MCP endpoint path. New client configs point here.</summary>
    public const string PrimaryEndpoint = "/api/mcp";

    /// <summary>The compatibility alias — the path the MCP module actually maps, and the one
    /// every pre-#2378 client config uses. Permanent: breaking it breaks every existing
    /// harness/plugin/back-connection config in the field.</summary>
    public const string CompatibilityEndpoint = "/mcp";

    /// <summary>The bare RFC 9728 protected-resource-metadata path the MCP SDK serves.</summary>
    public const string ResourceMetadataPath = "/.well-known/oauth-protected-resource";

    /// <summary><see cref="HttpContext.Items"/> key carrying the resource path
    /// (<see cref="PrimaryEndpoint"/> or <see cref="CompatibilityEndpoint"/>) a path-inserted
    /// metadata request was addressed to.</summary>
    public const string ResourcePathItem = "Memex.Mcp.ResourcePath";

    /// <summary>
    /// The front-of-pipeline rewrite described in the type remarks. Pure path logic — no
    /// response is written and no origin is composed here, precisely because this runs before
    /// <c>UseForwardedHeaders</c>.
    /// </summary>
    public static IApplicationBuilder UseMcpEndpointAlias(this IApplicationBuilder app)
        => app.Use(Middleware);

    private static Task Middleware(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path;

        // /api/mcp[/…] → /mcp[/…] — segment-exact (never /api/mcpx), case-insensitive
        // (route matching is; a client typing /API/MCP gets the same endpoint).
        if (path.StartsWithSegments(PrimaryEndpoint, StringComparison.OrdinalIgnoreCase, out var remaining))
        {
            context.Request.Path = new PathString(CompatibilityEndpoint).Add(remaining);
            return next(context);
        }

        // Path-inserted RFC 9728 discovery for both resource names → the SDK's bare document,
        // with the requested resource path stashed for OnResourceMetadataRequest.
        if (path.StartsWithSegments(ResourceMetadataPath, StringComparison.OrdinalIgnoreCase, out var resource)
            && resource.HasValue)
        {
            if (resource.Equals(new PathString(PrimaryEndpoint), StringComparison.OrdinalIgnoreCase))
            {
                context.Items[ResourcePathItem] = PrimaryEndpoint;
                context.Request.Path = ResourceMetadataPath;
            }
            else if (resource.Equals(new PathString(CompatibilityEndpoint), StringComparison.OrdinalIgnoreCase))
            {
                context.Items[ResourcePathItem] = CompatibilityEndpoint;
                context.Request.Path = ResourceMetadataPath;
            }
        }

        return next(context);
    }

    /// <summary>Self-wires <see cref="Middleware"/> at the front of every host that registers
    /// MCP authentication — the composition (which lives in the plugins repo) needs no extra
    /// call, so there is no cross-repo window where the primary path is unserved.</summary>
    internal sealed class StartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.UseMcpEndpointAlias();
                next(app);
            };
    }
}
