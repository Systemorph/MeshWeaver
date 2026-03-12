using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace MeshWeaver.Blazor.AI;

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
    /// </summary>
    public static IServiceCollection AddMeshMcp(this IServiceCollection services)
    {
        services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(McpMeshPlugin).Assembly);

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
