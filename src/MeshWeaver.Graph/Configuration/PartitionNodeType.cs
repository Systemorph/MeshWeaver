using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Partition nodes in the graph.
/// Partitions live at Admin/Partition/{Name} and describe which base paths they serve.
/// </summary>
public static class PartitionNodeType
{
    public const string NodeType = "Partition";
    public const string Namespace = "Admin/Partition";

    public static TBuilder AddPartitionType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStaticNodeProvider, PartitionNodeProvider>();
            services.AddSingleton<IStaticNodeProvider, DefaultPartitionProvider>();
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new PartitionAccessRule(sp.GetService<ISecurityService>() ?? new NullSecurityService()));
            return services;
        });
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        return builder;
    }

    private class PartitionNodeProvider : IStaticNodeProvider
    {
        public IEnumerable<MeshNode> GetStaticNodes()
        {
            yield return CreateMeshNode();
        }
    }

    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Partition",
        NodeType = NodeType,
        Icon = "/static/NodeTypeIcons/database.svg",
        AssemblyLocation = typeof(PartitionNodeType).Assembly.Location,
        ExcludeFromContext = new HashSet<string> { "create", "search" },
        Content = new NodeTypeDefinition { DefaultNamespace = Namespace },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<PartitionDefinition>())
            .AddDefaultLayoutAreas()
            .AddGlobalSettingsMenuItems(
                new GlobalSettingsMenuItemDefinition(
                    Id: "Partitions",
                    Label: "Partitions",
                    ContentBuilder: PartitionSettingsTab.BuildPartitionsTab,
                    Icon: FluentIcons.Database(),
                    Order: 10))
            .AddLayout(layout => layout
                .WithDefaultArea(MeshNodeLayoutAreas.SearchArea))
    };

    /// <summary>
    /// Access rule: Read for all authenticated users, Create/Update/Delete for Admin only.
    /// </summary>
    private class PartitionAccessRule(ISecurityService securityService) : INodeTypeAccessRule
    {
        public string NodeType => PartitionNodeType.NodeType;

        public IReadOnlyCollection<NodeOperation> SupportedOperations =>
            [NodeOperation.Read, NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

        public async Task<bool> HasAccessAsync(NodeValidationContext context, string? userId, CancellationToken ct = default)
        {
            if (context.Operation == NodeOperation.Read)
                return !string.IsNullOrEmpty(userId);

            if (string.IsNullOrEmpty(userId))
                return false;

            // Only admins can create/update/delete partitions
            return await securityService.HasPermissionAsync(
                context.Node.Path, userId, Permission.Update, ct);
        }
    }
}
