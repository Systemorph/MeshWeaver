using System.ComponentModel.DataAnnotations;
using MeshWeaver.ContentCollections;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared;

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
/// Access rules: public read. Create/Update/Delete require Admin role via ISecurityService.
/// </summary>
public static class OrganizationNodeType
{
    public const string NodeType = "Organization";

    public static TBuilder AddOrganizationType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.WithMeshType<Organization>();
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStaticNodeProvider, OrganizationNodeProvider>();
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new OrganizationAccessRule(sp.GetService<ISecurityService>() ?? new NullSecurityService()));
            services.AddSingleton<INodePostCreationHandler>(sp =>
                new OrganizationPostCreationHandler(
                    sp.GetService<ISecurityService>() ?? new NullSecurityService(),
                    sp.GetService<ILoggerFactory>()?.CreateLogger<OrganizationPostCreationHandler>()));
            return services;
        });
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
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
            .AddContentCollections()
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout
                .WithView(MeshNodeLayoutAreas.OverviewArea, OrganizationLayoutAreas.Overview)
                .WithDefaultArea(MeshNodeLayoutAreas.SearchArea))
    };

    /// <summary>
    /// Post-creation handler: creates partition, grants admin role, and creates markdown page.
    /// Triggered implicitly by RunPostCreationHandlersAsync when an Organization is created
    /// via normal CreateNodeRequest.
    /// </summary>
    private class OrganizationPostCreationHandler(
        ISecurityService securityService,
        ILogger<OrganizationPostCreationHandler>? logger) : INodePostCreationHandler
    {
        public string NodeType => OrganizationNodeType.NodeType;

        public async Task HandleAsync(MeshNode createdNode, string? createdBy, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(createdBy))
            {
                logger?.LogWarning("Cannot assign Admin role: no creator identity for Organization at {Path}", createdNode.Path);
                return;
            }

            logger?.LogInformation("Granting Admin role to {User} on Organization {Path}", createdBy, createdNode.Path);
            await securityService.AddUserRoleAsync(createdBy, Role.Admin.Id, createdNode.Id, assignedBy: "system", ct);
        }

        public IEnumerable<MeshNode> GetAdditionalNodes(MeshNode createdNode)
        {
            // Partition node at Admin/Partition/{OrgId}
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

            // Markdown overview page at {OrgId}/Overview
            yield return new MeshNode("Overview", createdNode.Id)
            {
                Name = "Overview",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
                Content = new MarkdownContent
                {
                    Content = $"# {createdNode.Name ?? createdNode.Id}\n\nWelcome to **{createdNode.Name ?? createdNode.Id}**.\n"
                }
            };
        }
    }

    /// <summary>
    /// DI-registered access rule for Organization nodes.
    /// Read: all authenticated users. Create/Update/Delete: requires appropriate permission (via ISecurityService).
    /// </summary>
    private class OrganizationAccessRule(ISecurityService securityService) : INodeTypeAccessRule
    {
        public string NodeType => OrganizationNodeType.NodeType;

        public IReadOnlyCollection<NodeOperation> SupportedOperations =>
            [NodeOperation.Read, NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

        public async Task<bool> HasAccessAsync(NodeValidationContext context, string? userId, CancellationToken ct = default)
        {
            if (context.Operation == NodeOperation.Read)
                return true;

            if (string.IsNullOrEmpty(userId))
                return false;

            if (context.Operation == NodeOperation.Create)
            {
                var parentPath = context.Node.GetParentPath() ?? context.Node.Path;
                return await securityService.HasPermissionAsync(parentPath, userId, Permission.Create, ct);
            }

            if (context.Operation is NodeOperation.Update or NodeOperation.Delete)
                return await securityService.HasPermissionAsync(context.Node.Path, userId, Permission.Update, ct);

            return false;
        }
    }
}
