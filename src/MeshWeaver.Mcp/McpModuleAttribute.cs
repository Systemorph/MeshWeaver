using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

[assembly: MeshWeaver.Mcp.McpMeshModule]
[assembly: MeshWeaver.Mcp.McpEndpointModule]

namespace MeshWeaver.Mcp;

/// <summary>
/// The mesh half of the MCP module: the MCP server itself — the tool surface over
/// <see cref="McpMeshPlugin"/>, the resources of <see cref="McpResources"/>, the stateless HTTP
/// transport and the shared argument-validation filter.
///
/// <para>MCP is a protocol surface a deployment either publishes or does not. Listing the DLL is
/// now that decision, instead of every portal compiling in a server whose only switch was the
/// route. Nothing else changes for a deployment that publishes it: same <c>/mcp</c> route, same
/// <c>McpAuth</c> policy, same <c>Mcp</c> configuration section.</para>
///
/// <para>🚨 Two pieces deliberately do NOT ride this module, because surfaces that outlive it
/// depend on them:</para>
/// <list type="bullet">
/// <item>The <b>authentication scheme</b> (<c>McpAuthenticationExtensions</c>, the <c>McpAuth</c>
///   and <c>MeshApiRead</c> policies) stays in the portal composition root — the REST mirror
///   <c>/api/mesh/*</c> is gated by the same policies and is not part of this module, and an
///   auth scheme must be registered before the pipeline is built either way. This module names
///   the policy by STRING only, so there is no compiled edge back to the host.</item>
/// <item><c>SessionHubResolver</c> and <c>McpConfiguration</c> moved to
///   <c>MeshWeaver.Hosting.AspNetCore</c> for the same reason: REST callers resolve the same
///   per-caller hub, and the co-hosted CLI back-connection reads the same base URL.</item>
/// </list>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class McpMeshModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.Mcp")
        {
            Name = "MCP server",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services => services.AddMeshMcp()),
    ];
}

/// <summary>
/// The endpoint half: <c>/mcp</c>, the Model Context Protocol HTTP transport. The module's OWN
/// protocol surface, so delisting it removing the route is the right semantic — an MCP client
/// gets a 404 it can act on rather than a server that answers but has no mesh behind it.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class McpEndpointModuleAttribute : MeshEndpointProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Action<IEndpointRouteBuilder>> EndpointConfigurations =>
    [
        endpoints => endpoints.MapMeshMcp(),
    ];
}
