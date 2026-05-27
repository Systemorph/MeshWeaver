using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using MeshWeaver.ContentCollections;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Portal;

/// <summary>
/// Represents a tenant container — a company, team, or organizational unit that owns
/// its own partition. Each Space gets a dedicated Postgres schema (lazy-created on
/// first write) and a root <c>AccessAssignment</c> granting the creator Admin.
/// </summary>
public record Space
{
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    /// <summary>
    /// Long-form markdown body shown on the Space's Overview. Leave empty to fall
    /// back to the default welcome message; fill it to author the page yourself
    /// (mission statement, team intros, curated links, etc.).
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
/// Provides configuration for Space nodes in the graph. A Space is a tenant
/// container — the root of a per-tenant partition. The partition schema itself
/// is created lazily on first write by the path-routing adapter; there is no
/// dedicated <c>Partition</c> MeshNode emitted for a Space.
///
/// Access rules: Read/Create/Update/Delete controlled by partition-level
/// permissions via <see cref="SecurityService"/>. Space instances live in
/// their own partition and mirror to <c>auth.mesh_nodes</c> via the V27 mirror
/// trigger (extended to include <c>Space</c> in V28) so a single-schema query
/// over <c>auth</c> can list every Space in the mesh.
/// </summary>
public static class SpaceNodeType
{
    public const string NodeType = "Space";

    /// <summary>
    /// Default welcome body rendered for a Space when the node has no
    /// PreRenderedHtml of its own. Plain markdown — no pseudo-HTML.
    /// Per-Space overrides live in each Space's own <c>index.md</c>
    /// (set on <see cref="MeshNode.PreRenderedHtml"/>).
    /// </summary>
    public const string WelcomeMarkdown = """
        # Welcome

        This is your space's home page.

        Start by structuring the content you want to share here — a short introduction,
        a mission statement, links to the teams and projects that matter to you.

        ## Tips to get started

        - **Create some content.** Use the menu above to add pages, demos, or documents.
          You can always come back and ask the assistant to summarize what's inside.
        - **Bring in existing files.** Drop markdown, images, or documents into the
          content collection; they show up automatically.
        - **Chat with your space.** Use the chat input below to ask questions,
          kick off an agent, or draft content together.

        Once you're ready, replace this text with whatever fits your space best.
        """;

    public static TBuilder AddSpaceType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.WithMeshType<Space>();
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new SpaceAccessRule(sp.GetRequiredService<IMessageHub>()));
            services.AddSingleton<INodePostCreationHandler>(sp =>
                new SpacePostCreationHandler(
                    sp.GetRequiredService<IMeshService>(),
                    sp.GetService<ILoggerFactory>()?.CreateLogger<SpacePostCreationHandler>()));
            return services;
        });
        // Space instances are NOT publicly readable — partition access controls visibility.
        // The type definition itself remains visible; instances are filtered by user permissions.
        return builder;
    }

    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Space",
        NodeType = "NodeType",
        Icon = "/static/NodeTypeIcons/organization.svg",
        Content = new NodeTypeDefinition { DefaultNamespace = "" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<Space>())
            .AddContentCollections()
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout
                .WithView(MeshNodeLayoutAreas.OverviewArea, SpaceLayoutAreas.Overview))
    };

    /// <summary>
    /// Post-creation handler: grants the creator Admin role on the new Space's
    /// scope. Triggered implicitly by <c>RunPostCreationHandlersAsync</c> when
    /// a Space is created via normal CreateNodeRequest. No longer emits a
    /// dedicated <c>Partition</c> MeshNode — the path-routing adapter creates
    /// the partition schema lazily on first write via
    /// <c>public.ensure_partition_schema</c>.
    /// </summary>
    private class SpacePostCreationHandler(
        IMeshService meshService,
        ILogger<SpacePostCreationHandler>? logger) : INodePostCreationHandler
    {
        public string NodeType => SpaceNodeType.NodeType;

        public Task HandleAsync(MeshNode createdNode, string? createdBy, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(createdBy))
            {
                logger?.LogWarning("Cannot assign Admin role: no creator identity for Space at {Path}", createdNode.Path);
                return Task.CompletedTask;
            }

            logger?.LogInformation("Granting Admin role to {User} on Space {Path}", createdBy, createdNode.Path);
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
                ex => logger?.LogWarning(ex, "Failed to grant Admin role to {User} on Space {Path}", createdBy, createdNode.Path));
            return Task.CompletedTask;
        }

        public IEnumerable<MeshNode> GetAdditionalNodes(MeshNode createdNode) => [];
    }

    /// <summary>
    /// DI-registered access rule for Space nodes.
    /// Read: requires partition Read permission. Create/Update/Delete: requires
    /// appropriate permission (via <see cref="SecurityService"/>).
    /// </summary>
    private class SpaceAccessRule(IMessageHub hub) : INodeTypeAccessRule
    {
        public string NodeType => SpaceNodeType.NodeType;

        public IReadOnlyCollection<NodeOperation> SupportedOperations =>
            [NodeOperation.Read, NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
        {
            if (string.IsNullOrEmpty(userId))
                return Observable.Return(false);

            if (context.Operation == NodeOperation.Read)
                return hub.CheckPermission(context.Node.Path, userId, Permission.Read);

            if (context.Operation == NodeOperation.Create)
            {
                var parentPath = context.Node.GetParentPath() ?? context.Node.Path;
                return hub.CheckPermission(parentPath, userId, Permission.Create);
            }

            if (context.Operation is NodeOperation.Update or NodeOperation.Delete)
                return hub.CheckPermission(context.Node.Path, userId, Permission.Update);

            return Observable.Return(false);
        }
    }
}
