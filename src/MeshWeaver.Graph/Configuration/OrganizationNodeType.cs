using System.ComponentModel.DataAnnotations;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Represents a company, team, or organizational unit.
/// </summary>
public record Organization
{
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? Website { get; init; }

    [ContentItem]
    public string? Logo { get; init; }

    [ContentItem]
    [MeshNodeProperty(nameof(MeshNode.Icon))]
    public string Icon { get; init; } = "Building";

    public string? Location { get; init; }

    public string? Email { get; init; }

    public bool IsVerified { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Provides configuration for Organization nodes in the graph.
/// Access rules: public read. Update/Create/Delete fall through to standard RLS.
/// </summary>
public static class OrganizationNodeType
{
    public const string NodeType = "Organization";

    public static TBuilder AddOrganizationType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStaticNodeProvider, OrganizationNodeProvider>();
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new OrganizationAccessRule(sp.GetService<ISecurityService>() ?? new NullSecurityService()));
            services.AddSingleton<INodePostCreationHandler>(sp =>
                new OrganizationCreatorAdminHandler(
                    sp.GetService<ISecurityService>() ?? new NullSecurityService(),
                    sp.GetRequiredService<ILogger<OrganizationCreatorAdminHandler>>()));
            services.AddSingleton(new NodeTypePermission(NodeType, PublicRead: true));
            return services;
        });
        return builder;
    }

    private class OrganizationNodeProvider : IStaticNodeProvider
    {
        public IEnumerable<MeshNode> GetStaticNodes()
        {
            yield return CreateMeshNode();
        }
    }

    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Organization",
        NodeType = NodeType,
        Icon = "/static/NodeTypeIcons/building.svg",
        AssemblyLocation = typeof(OrganizationNodeType).Assembly.Location,
        Content = new NodeTypeDefinition { DefaultNamespace = "" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<Organization>())
            .WithPublicRead()
            .Set(new NodeTypeCatalogMode())
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout
                .WithDefaultArea(MeshNodeLayoutAreas.SearchArea))
    };

    /// <summary>
    /// DI-registered access rule for Organization nodes.
    /// Read: all authenticated users. Update: requires Admin role (via ISecurityService).
    /// </summary>
    private class OrganizationAccessRule(ISecurityService securityService) : INodeTypeAccessRule
    {
        public string NodeType => OrganizationNodeType.NodeType;

        public IReadOnlyCollection<NodeOperation> SupportedOperations =>
            [NodeOperation.Read, NodeOperation.Create, NodeOperation.Update];

        public async Task<bool> HasAccessAsync(NodeValidationContext context, string? userId, CancellationToken ct = default)
        {
            // Read: all users including anonymous
            if (context.Operation == NodeOperation.Read)
                return true;

            if (string.IsNullOrEmpty(userId))
                return false;

            if (context.Operation == NodeOperation.Create)
            {
                var parentPath = context.Node.GetParentPath() ?? context.Node.Path;
                return await securityService.HasPermissionAsync(parentPath, userId, Permission.Create, ct);
            }

            if (context.Operation == NodeOperation.Update)
                return await securityService.HasPermissionAsync(context.Node.Path, userId, Permission.Update, ct);

            return false;
        }
    }

    /// <summary>
    /// Grants the creator Admin role on the newly created Organization
    /// and creates a Partition node for the organization's storage partition.
    /// </summary>
    private class OrganizationCreatorAdminHandler(
        ISecurityService securityService,
        ILogger<OrganizationCreatorAdminHandler> logger) : INodePostCreationHandler
    {
        public string NodeType => OrganizationNodeType.NodeType;

        public async Task HandleAsync(MeshNode createdNode, string? createdBy, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(createdBy))
            {
                logger.LogWarning("Cannot assign Admin role: no creator identity for Organization at {Path}", createdNode.Path);
                return;
            }

            // Grant Admin role to creator on the organization
            logger.LogInformation("Granting Admin role to {User} on Organization {Path}", createdBy, createdNode.Path);
            await securityService.AddUserRoleAsync(createdBy, Role.Admin.Id, createdNode.Path, assignedBy: "system", ct);
        }

        /// <summary>
        /// Returns a Partition node to be created alongside the Organization.
        /// Persisted directly by RunPostCreationHandlersAsync (bypasses hub pipeline).
        /// </summary>
        public IEnumerable<MeshNode> GetAdditionalNodes(MeshNode createdNode)
        {
            yield return new MeshNode(createdNode.Id, PartitionNodeType.Namespace)
            {
                NodeType = PartitionNodeType.NodeType,
                Name = createdNode.Name ?? createdNode.Id,
                State = MeshNodeState.Active,
                Content = new PartitionDefinition
                {
                    Namespace = createdNode.Id,
                    DataSource = "default",
                    Schema = createdNode.Id.ToLowerInvariant(),
                    TableMappings = PartitionDefinition.StandardTableMappings,
                    Description = $"Partition for organization {createdNode.Name ?? createdNode.Id}"
                }
            };
        }
    }
}

