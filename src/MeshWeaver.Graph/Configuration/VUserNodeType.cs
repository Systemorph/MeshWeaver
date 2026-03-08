using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for VUser (virtual/anonymous user) nodes in the graph.
/// VUser nodes represent unauthenticated visitors identified by a browser cookie.
/// They are kept separate from real User nodes so they never appear in user pickers,
/// login screens, or access assignment dialogs.
/// </summary>
public static class VUserNodeType
{
    /// <summary>
    /// The NodeType value used to identify virtual user nodes.
    /// </summary>
    public const string NodeType = "VUser";

    /// <summary>
    /// The portal namespace prefix. Hubs in this namespace can create/read/edit VUser nodes.
    /// </summary>
    public const string PortalNamespace = "portal";

    /// <summary>
    /// Registers the built-in "VUser" MeshNode on the mesh builder,
    /// including a validator that grants portal namespace hubs create/read/edit on VUser nodes.
    /// </summary>
    public static TBuilder AddVUserType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeTypeAccessRule, VUserAccessRule>();
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the VUser node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Virtual User",
        Icon = "/static/NodeTypeIcons/person.svg",
        NodeType = NodeType,
        AssemblyLocation = typeof(VUserNodeType).Assembly.Location,
        Content = new NodeTypeDefinition { DefaultNamespace = "VUser", RestrictedToNamespaces = ["VUser"] },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<AccessObject>())
            .AddDefaultLayoutAreas()
    };

    /// <summary>
    /// Custom access rule for VUser nodes. Replaces the standard RLS check inside RlsNodeValidator.
    /// Allows any identity in the portal namespace to create/read/edit VUser nodes.
    /// Other identities are denied (the standard AccessAssignment check is NOT performed).
    /// </summary>
    private class VUserAccessRule : INodeTypeAccessRule
    {
        public string NodeType => VUserNodeType.NodeType;

        public IReadOnlyCollection<NodeOperation> SupportedOperations =>
            [NodeOperation.Create, NodeOperation.Read, NodeOperation.Update];

        public Task<bool> HasAccessAsync(NodeValidationContext context, string? userId, CancellationToken ct = default)
        {
            // Allow if the identity is in the portal namespace
            if (!string.IsNullOrEmpty(userId) &&
                userId.StartsWith(PortalNamespace + "/", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(true);

            // Deny all others
            return Task.FromResult(false);
        }
    }
}
