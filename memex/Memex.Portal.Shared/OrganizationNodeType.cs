using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
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

    /// <summary>
    /// Long-form markdown body shown on the organization's Overview. Leave empty
    /// to fall back to the default welcome message; fill it to author the page
    /// yourself (mission statement, team intros, curated links, etc.).
    /// </summary>
    public string? Body { get; init; }

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
/// Access rules: Read/Create/Update/Delete controlled by partition-level permissions via ISecurityService.
/// Excluded from search to prevent cross-partition data leakage.
/// </summary>
public static class OrganizationNodeType
{
    public const string NodeType = "Organization";

    /// <summary>
    /// Default welcome body rendered for an Organization when the node has no PreRenderedHtml of its own.
    /// Plain markdown — no pseudo-HTML. Per-organization overrides live in each organization's
    /// own <c>index.md</c> (set on <see cref="MeshNode.PreRenderedHtml"/>), e.g. the Systemorph
    /// organization ships its own bespoke landing page that replaces this text.
    /// </summary>
    public const string WelcomeMarkdown = """
        # Welcome

        This is your organization's home page.

        Start by structuring the content you want to share here — a short introduction,
        a mission statement, links to the teams and projects that matter to you.

        ## Tips to get started

        - **Create some content.** Use the menu above to add pages, demos, or documents.
          You can always come back and ask the assistant to summarize what's inside.
        - **Bring in existing files.** Drop markdown, images, or documents into the
          content collection; they show up automatically.
        - **Chat with your organization.** Use the chat input below to ask questions,
          kick off an agent, or draft content together.

        Once you're ready, replace this text with whatever fits your organization best.
        """;

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
                    sp.GetRequiredService<IMeshService>(),
                    sp.GetService<ILoggerFactory>()?.CreateLogger<OrganizationPostCreationHandler>()));
            return services;
        });
        // Organization instances are NOT publicly readable — partition access controls visibility.
        // The type definition itself remains visible; instances are filtered by user permissions.
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
        NodeType = "NodeType",
        Icon = "/static/NodeTypeIcons/building.svg",
        AssemblyLocation = typeof(OrganizationNodeType).Assembly.Location,
        Content = new NodeTypeDefinition { DefaultNamespace = "" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<Organization>())
            .AddContentCollections()
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout
                .WithView(MeshNodeLayoutAreas.OverviewArea, OrganizationLayoutAreas.Overview))
    };

    /// <summary>
    /// Post-creation handler: creates partition, grants admin role, and creates markdown page.
    /// Triggered implicitly by RunPostCreationHandlersAsync when an Organization is created
    /// via normal CreateNodeRequest.
    /// </summary>
    private class OrganizationPostCreationHandler(
        IMeshService meshService,
        ILogger<OrganizationPostCreationHandler>? logger) : INodePostCreationHandler
    {
        public string NodeType => OrganizationNodeType.NodeType;

        public Task HandleAsync(MeshNode createdNode, string? createdBy, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(createdBy))
            {
                logger?.LogWarning("Cannot assign Admin role: no creator identity for Organization at {Path}", createdNode.Path);
                return Task.CompletedTask;
            }

            logger?.LogInformation("Granting Admin role to {User} on Organization {Path}", createdBy, createdNode.Path);
            // Replaces the obsolete ISecurityService.AddUserRoleAsync — write the
            // AccessAssignment node directly via IMeshService.CreateNode (the only
            // entry point that boots the per-node hub). Fire-and-forget Subscribe;
            // failures surface in the data-layer error path.
            var assignmentNode = new MeshNode($"{createdBy}_Access", $"{createdNode.Id}/_Access")
            {
                NodeType = "AccessAssignment",
                Name = $"{createdBy} Access",
                MainNode = createdNode.Id,
                Content = new AccessAssignment
                {
                    AccessObject = createdBy,
                    DisplayName = createdBy,
                    Roles = [new RoleAssignment { Role = Role.Admin.Id, Denied = false }]
                }
            };
            meshService.CreateNode(assignmentNode).Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "Failed to grant Admin role to {User} on Organization {Path}", createdBy, createdNode.Path));
            return Task.CompletedTask;
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

        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
        {
            if (string.IsNullOrEmpty(userId))
                return Observable.Return(false);

            if (context.Operation == NodeOperation.Read)
                return securityService.HasPermission(context.Node.Path, userId, Permission.Read);

            if (context.Operation == NodeOperation.Create)
            {
                var parentPath = context.Node.GetParentPath() ?? context.Node.Path;
                return securityService.HasPermission(parentPath, userId, Permission.Create);
            }

            if (context.Operation is NodeOperation.Update or NodeOperation.Delete)
                return securityService.HasPermission(context.Node.Path, userId, Permission.Update);

            return Observable.Return(false);
        }
    }
}
