using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace MeshWeaver.Mcp;

/// <summary>
/// Extension methods for registering MCP (Model Context Protocol) services and endpoints.
/// </summary>
public static class McpExtensions
{
    /// <summary>
    /// Adds MCP support to the mesh: registers ApiToken node type (with ValidateTokenRequest handler)
    /// so that API tokens can authenticate users via the message hub.
    /// </summary>
    public static TBuilder AddMcp<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddApiTokenType();
        return builder;
    }

    /// <summary>
    /// Adds MCP server services to the service collection.
    /// Registers tools from McpMeshPlugin and resources from McpResources.
    /// Binds <see cref="McpConfiguration"/> to the <c>Mcp</c> configuration
    /// section (Aspire AppHost wires the portal's own external endpoint
    /// into <c>Mcp__BaseUrl</c> at deployment time, no per-environment
    /// source patches needed).
    /// </summary>
    /// <summary>
    /// Connect-time guidance the MCP client (Claude Code / Copilot) receives in the <c>instructions</c>
    /// field of the initialize response — so NOTHING is synced to disk: the mesh is the workspace, search
    /// is vector-indexed, and skills are mesh nodes found + read on demand. The full tool + query reference
    /// is the <c>tools-reference</c> resource (the same embedded ToolsReference the in-portal agents use).
    /// </summary>
    public const string ServerInstructions =
        "You are connected to the MeshWeaver mesh through this MCP server — the mesh IS your workspace " +
        "(not a local file tree). Use these tools to read and modify content.\n\n" +
        "Everything in the mesh is vector-indexed: retrieve anything with `search` (free-text routes to the " +
        "semantic index) — you do not need exact paths.\n\n" +
        "Skills are reusable capabilities stored as `nodeType:Skill` nodes. When a request matches a specific " +
        "operation, find the relevant skill with `search nodeType:Skill`, then read it with `get` to follow " +
        "its instructions. Read each skill only once.\n\n" +
        "Read the `tools-reference` resource for the full tool + query-syntax reference.";

    /// <summary>
    /// Registers the MCP server with HTTP transport, exposing the tools from
    /// <c>McpMeshPlugin</c> and the resources from <c>McpResources</c>, sets the
    /// connect-time <see cref="ServerInstructions"/>, and binds
    /// <see cref="McpConfiguration"/> to the <c>Mcp</c> configuration section.
    /// </summary>
    /// <param name="services">The service collection to add MCP services to.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddMeshMcp(this IServiceCollection services)
    {
        services.AddMcpServer(options => options.ServerInstructions = ServerInstructions)
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(McpMeshPlugin).Assembly)
            .WithResourcesFromAssembly(typeof(McpResources).Assembly);

        // BindConfiguration resolves IConfiguration from DI at options
        // construction — works wherever the standard ASP.NET host is running,
        // no caller-side IConfiguration parameter needed.
        services.AddOptions<McpConfiguration>().BindConfiguration("Mcp");

        return services;
    }

    /// <summary>
    /// Maps the MCP endpoint for HTTP transport.
    /// </summary>
    /// <param name="app">The endpoint route builder (usually WebApplication)</param>
    /// <param name="pattern">The URL pattern for the MCP endpoint. Defaults to "/mcp".</param>
    public static IEndpointRouteBuilder MapMeshMcp(this IEndpointRouteBuilder app, string pattern = "/mcp")
    {
        app.MapMcp(pattern).RequireAuthorization("McpAuth");
        return app;
    }
}
