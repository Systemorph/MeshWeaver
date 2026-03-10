using System.ComponentModel.DataAnnotations;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

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
            services.AddSingleton<INodeTypeAccessRule, OrganizationAccessRule>();
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
            [NodeOperation.Read, NodeOperation.Update];

        public async Task<bool> HasAccessAsync(NodeValidationContext context, string? userId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(userId))
                return false;

            if (context.Operation == NodeOperation.Read)
                return true;

            if (context.Operation == NodeOperation.Update)
                return await securityService.HasPermissionAsync(context.Node.Path, userId, Permission.Update, ct);

            return false;
        }
    }
}

