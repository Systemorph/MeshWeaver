using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Session-scoped satellite hub for MCP callers. One hub per authenticated
/// <c>(caller-identity, mcp-session-id)</c> pair, materialised on first access
/// and auto-disposed after idle timeout.
///
/// <para>
/// <b>Why a hub per MCP session?</b> When an MCP tool (e.g. <c>ExecuteScript</c>)
/// posts a message, the message must originate from a hub that lives *outside* the
/// server's Orleans grain scope, otherwise routing rules like
/// <c>RouteAddressToHostedHub("kernel", ...)</c> never fire — Orleans tries to
/// activate a grain for the target address first, fails to find a MeshNode, and
/// the call times out with "Cannot activate grain ... node not found."
/// The Blazor client hub pattern solves this for browser users; this MCP satellite
/// gives MCP callers the same shape.
/// </para>
///
/// Mirrors <see cref="KernelNodeType"/>: a <c>RouteAddressToHostedHub</c> rule
/// registered on the root mesh hub creates the MCP session hub on demand the
/// moment the first message with target <c>mcp/{sessionId}</c> arrives.
/// </summary>
public static class McpNodeType
{
    public const string NodeType = "mcp";

    public static TBuilder AddMcp<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder
            .ConfigureHub(config => config
                .WithRoutes(routes => routes.RouteAddressToHostedHub(
                    AddressExtensions.McpType,
                    c => c)))
            .ConfigureServices(services =>
            {
                services.AddSingleton<INodeTypeAccessRule>(sp =>
                    new SatelliteAccessRule(NodeType, sp.GetService<ISecurityService>() ?? new NullSecurityService()));
                return services;
            });
        return builder;
    }

    /// <summary>
    /// Registers the satellite type definition — matches KernelNodeType's shape
    /// so the mesh treats <c>mcp/{id}</c> addresses as ephemeral session nodes.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "MCP Session",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(McpNodeType).Assembly.Location,
        HubConfiguration = config => config
    };
}
