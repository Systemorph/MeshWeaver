using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Messaging;
using MeshWeaver.ContentCollections;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Represents a tenant container — a company, team, or organizational unit that owns
/// its own partition. Each Space gets a dedicated Postgres schema (provisioned eagerly
/// on create) and a root <c>AccessAssignment</c> granting the creator Admin.
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
/// container — the root of a per-tenant partition. Creating one is a fool-proof
/// server-side invariant (driven entirely from <c>OwnsPartitionProvisioningValidator</c>
/// + <see cref="SpacePostCreationHandler"/>, so it holds for MCP <c>create</c> and
/// every other caller):
///
/// <list type="number">
///   <item><b>Top-level only.</b> A Space's path is just its id (empty namespace).
///     <c>OwnsPartitionProvisioningValidator</c> (generic, reads
///     <c>NodeTypeDefinition.OwnsPartition</c>) rejects any create with a non-empty
///     namespace.</item>
///   <item><b>Eagerly provisioned partition.</b> The validator runs BEFORE the root
///     write and, under <c>AccessService.ImpersonateAsSystem</c>, calls every
///     <see cref="IPartitionStorageProvider"/>'s
///     <c>EnsurePartitionProvisionedAsync</c> — which routes to the
///     <c>public.ensure_partition_schema</c> Postgres stored procedure
///     (<c>PostgreSqlPartitionStorageProvider.EnsureSchemaAsync</c>). The per-Space
///     schema (<c>{id}.mesh_nodes</c> + every satellite table) therefore exists before
///     the Space root write or any child touch — no more <c>42P01 relation does not
///     exist</c>. Idempotent.</item>
///   <item><b>Routing primed.</b> <see cref="SpacePostCreationHandler.GetAdditionalNodes"/>
///     emits an <c>Admin/Partition/{id}</c> <see cref="PartitionDefinition"/>. Persisting
///     it primes <c>PgPartitionCache</c> (positive) across the mesh and drives the
///     <c>partition_changes</c> pg_notify provisioning so every silo/mirror agrees the
///     partition exists.</item>
///   <item><b>Creator gets Admin.</b> The handler persists an <c>AccessAssignment</c> at
///     <c>{id}/_Access</c> under <c>AccessService.ImpersonateAsSystem</c>; the write is
///     <b>awaited</b>, so a failed grant faults the create response rather than being
///     silently dropped.</item>
/// </list>
///
/// Access rules: Read/Create/Update/Delete controlled by partition-level
/// permissions via <c>SecurityService</c>. Space instances live in
/// their own partition and mirror to <c>auth.mesh_nodes</c> via the V27 mirror
/// trigger (extended to include <c>Space</c> in V28) so a single-schema query
/// over <c>auth</c> can list every Space in the mesh.
/// </summary>
public static class SpaceNodeType
{
    public const string NodeType = "Space";

    /// <summary>
    /// Marker registered once per builder so <see cref="AddSpaceType{TBuilder}"/> is
    /// idempotent: the test base registers Space by default AND individual tests call
    /// <c>AddSpaceType()</c>, so the second call must be a no-op. Registering the
    /// access rule / post-creation handler / validator twice would run the
    /// creator-admin grant twice and stack two access rules per Space.
    /// </summary>
    private sealed class SpaceTypeMarker;

    /// <summary>
    /// Default welcome body rendered for a Space when the node has no
    /// PreRenderedHtml of its own. Plain markdown — no pseudo-HTML.
    /// Per-Space overrides live in each Space's own <c>index.md</c>
    /// (set on <see cref="MeshNode.PreRenderedHtml"/>).
    /// <para>The mesh catalog (the namespace-tree Children layout area) is NOT part
    /// of this markdown — <c>SpaceLayoutAreas.BuildSpaceView</c> renders it directly
    /// below the body for every Space, authored or default. A space owner who wants
    /// the catalog elsewhere in their page can still embed
    /// <c>@@("area:Children")</c> in their own body.</para>
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

        ## Replace this page

        This welcome text is a placeholder shown until the Space has its own overview.
        To make it your own, edit this Space's **Body**: open the Space's menu
        (top-right **⋯**) → **Edit**, then write your overview in the **Body** field
        (plain markdown — headings, links, tables, and `@@`-embeds all work). Or simply
        ask the assistant in the chat below to draft it — it writes to the same Body field.

        ## In this space

        @@("area:Children")
        """;

    public static TBuilder AddSpaceType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        // Idempotent guard: the marker check runs against the LIVE IServiceCollection
        // inside ConfigureServices, so it is correct regardless of whether the host
        // invokes the service-config lambda synchronously (MeshHostApplicationBuilder,
        // test base) or deferred during host build (MeshHostBuilder). The very first
        // call registers the marker + node/type + the three singletons; every later
        // call short-circuits. The builder mutations (AddMeshNodes / WithMeshType) are
        // co-located inside the guarded lambda so they, too, run exactly once.
        builder.ConfigureServices(services =>
        {
            if (services.Any(d => d.ServiceType == typeof(SpaceTypeMarker)))
                return services;
            services.AddSingleton<SpaceTypeMarker>();

            builder.AddMeshNodes(CreateMeshNode());
            builder.WithMeshType<Space>();

            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new SpaceAccessRule(sp.GetRequiredService<IMessageHub>()));
            // The top-level invariant + eager schema provisioning is handled generically
            // by OwnsPartitionProvisioningValidator (it reads NodeTypeDefinition.OwnsPartition,
            // which CreateMeshNode sets true) — no Space-specific validator needed.
            services.AddSingleton<INodePostCreationHandler>(sp =>
                new SpacePostCreationHandler(
                    sp.GetRequiredService<IMeshService>(),
                    sp.GetRequiredService<AccessService>(),
                    sp.GetService<ILoggerFactory>()?.CreateLogger<SpacePostCreationHandler>()));
            // A Space must always retain at least one admin: block deleting / denying
            // the last non-denied Admin AccessAssignment on any partition's _Access.
            services.AddSingleton<INodeValidator>(sp =>
                new SpaceAdminInvariantValidator(
                    sp.GetRequiredService<IMessageHub>(),
                    sp.GetService<ILoggerFactory>()?.CreateLogger<SpaceAdminInvariantValidator>()));
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
        Content = new NodeTypeDefinition { DefaultNamespace = "", RestrictedToNamespaces = [""], OwnsPartition = true },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<Space>())
            .AddContentCollections()
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout
                .WithView(MeshNodeLayoutAreas.OverviewArea, SpaceLayoutAreas.Overview))
    };

    /// <summary>
    /// Post-creation handler that makes a Space create a fool-proof partition root.
    /// Triggered implicitly by <c>RunPostCreationHandlersObs</c> when a Space is
    /// created via a normal <c>CreateNodeRequest</c> (MCP <c>create</c> and every
    /// other caller funnel through there).
    ///
    /// <para>The per-Space schema is already provisioned eagerly by
    /// <c>OwnsPartitionProvisioningValidator</c> before the root write; this handler
    /// (1) emits the <c>Admin/Partition/{id}</c> <see cref="PartitionDefinition"/>
    /// that primes <c>PgPartitionCache</c> + the <c>partition_changes</c> notify pump,
    /// and (2) grants the creator Admin.</para>
    ///
    /// <para><b>The creator-admin grant</b> runs under
    /// <c>AccessService.ImpersonateAsSystem</c> (the new user / brand-new partition
    /// root means the caller can't already hold Create on it — the canonical
    /// infrastructure-write case) and is <b>awaited</b>, not fire-and-forget: a
    /// failed grant faults <see cref="Handle"/>, which
    /// <c>RunPostCreationHandlersObs</c> surfaces as a failed create response
    /// instead of silently dropping it.</para>
    /// </summary>
    private class SpacePostCreationHandler(
        IMeshService meshService,
        AccessService accessService,
        ILogger<SpacePostCreationHandler>? logger) : INodePostCreationHandler
    {
        public string NodeType => SpaceNodeType.NodeType;

        public IObservable<System.Reactive.Unit> Handle(MeshNode createdNode, string? createdBy)
        {
            if (string.IsNullOrEmpty(createdBy))
            {
                logger?.LogWarning("Cannot assign Admin role: no creator identity for Space at {Path}", createdNode.Path);
                return Observable.Empty<System.Reactive.Unit>();
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
            // Grant under System impersonation so the write authorises (creator can't already
            // hold Create on a brand-new partition root). Return the observable directly — the
            // caller subscribes; a failure propagates through OnError so RunPostCreationHandlers
            // reports it. Pure reactive, no Task, no ToTask bridge.
            return Observable.Using(
                    () => accessService.ImpersonateAsSystem(),
                    _ => meshService.CreateNode(assignmentNode))
                .Do(_ => logger?.LogInformation(
                    "Granted Admin to {User} on Space {Path} at {GrantPath}",
                    createdBy, createdNode.Path, assignmentNode.Path))
                .Select(_ => System.Reactive.Unit.Default);
        }

        /// <summary>
        /// Emits the per-Space <c>Admin/Partition/{id}</c> <see cref="PartitionDefinition"/>.
        /// Persisting this primes the partition cache + notify-driven schema
        /// provisioning so the partition is consistently routable across the mesh.
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
                    TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings(),
                    Description = $"Partition for space {createdNode.Name ?? createdNode.Id}"
                }
            };
        }
    }

    /// <summary>
    /// DI-registered access rule for Space nodes.
    /// Read: requires partition Read permission. Create/Update/Delete: requires
    /// appropriate permission (via <c>SecurityService</c>).
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
                // Anyone authenticated may create a top-level Space — they become its
                // Admin via SpacePostCreationHandler. A Space is ALWAYS top-level
                // (SpaceTopLevelValidator rejects non-empty namespaces), and a brand-new
                // top-level partition has no parent to hold Create on, so requiring
                // CheckPermission(parentPath, Create) here locked everyone except global
                // admins out of creating spaces (the logic lost in the Organization→Space
                // migration). Gate only on a present identity (userId != null, already
                // checked above). Nested creates still require parent Create — though the
                // validator rejects them first.
                if (string.IsNullOrEmpty(context.Node.Namespace))
                    return Observable.Return(true);
                var parentPath = context.Node.GetParentPath() ?? context.Node.Path;
                return hub.CheckPermission(parentPath, userId, Permission.Create);
            }

            if (context.Operation is NodeOperation.Update or NodeOperation.Delete)
                return hub.CheckPermission(context.Node.Path, userId, Permission.Update);

            return Observable.Return(false);
        }
    }
}
