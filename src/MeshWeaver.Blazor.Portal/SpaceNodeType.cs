using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
/// server-side invariant (driven entirely from <see cref="SpaceTopLevelValidator"/>
/// + <see cref="SpacePostCreationHandler"/>, so it holds for MCP <c>create</c> and
/// every other caller):
///
/// <list type="number">
///   <item><b>Top-level only.</b> A Space's path is just its id (empty namespace).
///     <see cref="SpaceTopLevelValidator"/> rejects any create with a non-empty
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
            // Top-level invariant: a Space IS a partition root. Reject any create with a
            // non-empty namespace BEFORE the root write, and eagerly provision the
            // partition's schema+tables (via public.ensure_partition_schema) so the
            // root write + first child touch never race a missing relation.
            services.AddSingleton<INodeValidator>(sp =>
                new SpaceTopLevelValidator(
                    sp,
                    sp.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Blazor.Portal.SpaceTopLevelValidator")));
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
        Content = new NodeTypeDefinition { DefaultNamespace = "", OwnsPartition = true },
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
    /// <see cref="SpaceTopLevelValidator"/> before the root write; this handler
    /// (1) emits the <c>Admin/Partition/{id}</c> <see cref="PartitionDefinition"/>
    /// that primes <c>PgPartitionCache</c> + the <c>partition_changes</c> notify pump,
    /// and (2) grants the creator Admin.</para>
    ///
    /// <para><b>The creator-admin grant</b> runs under
    /// <c>AccessService.ImpersonateAsSystem</c> (the new user / brand-new partition
    /// root means the caller can't already hold Create on it — the canonical
    /// infrastructure-write case) and is <b>awaited</b>, not fire-and-forget: a
    /// failed grant faults <see cref="HandleAsync"/>, which
    /// <c>RunPostCreationHandlersObs</c> surfaces as a failed create response
    /// instead of silently dropping it.</para>
    /// </summary>
    private class SpacePostCreationHandler(
        IMeshService meshService,
        AccessService accessService,
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
            // Await the grant under System impersonation so:
            //   (1) the write authorises (creator can't already hold Create on a
            //       brand-new partition root they don't own yet);
            //   (2) a failure propagates — the awaited Task faults, which
            //       RunPostCreationHandlersObs reports as a failed create response.
            //       The old `.Subscribe(onError: log)` fire-and-forget swallowed it.
            // INodePostCreationHandler is a Task-based contract invoked via
            // Observable.FromAsync (the sanctioned reactive→Task boundary), so the
            // ToTask bridge here is allowed (see AsynchronousCalls.md).
            return Observable.Using(
                    () => accessService.ImpersonateAsSystem(),
                    _ => meshService.CreateNode(assignmentNode))
                .Do(_ => logger?.LogInformation(
                    "Granted Admin to {User} on Space {Path} at {GrantPath}",
                    createdBy, createdNode.Path, assignmentNode.Path))
                .FirstAsync()
                .ToTask(ct);
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
                    TableMappings = PartitionDefinition.StandardTableMappings,
                    Description = $"Partition for space {createdNode.Name ?? createdNode.Id}"
                }
            };
        }
    }

    /// <summary>
    /// Enforces the top-level invariant AND eagerly provisions the partition.
    ///
    /// <para>A Space IS a partition root, so its path must be just its id (empty
    /// namespace). Rejecting non-empty-namespace creates up front prevents the
    /// half-registered split state (routing/index entry with no schema or creator
    /// grant) that a nested Space would otherwise leave behind.</para>
    ///
    /// <para>For a valid top-level Space, the validator runs BEFORE the root write
    /// and provisions the partition's backing store via every
    /// <see cref="IPartitionStorageProvider"/>'s <c>EnsurePartitionProvisionedAsync</c>
    /// (the Postgres provider routes to <c>public.ensure_partition_schema</c>). It runs
    /// under <c>AccessService.ImpersonateAsSystem</c> because creating a partition schema
    /// is an infrastructure operation the creating user has no permission for yet. The
    /// provisioning is idempotent, so re-validation (retries) is harmless.</para>
    /// </summary>
    private sealed class SpaceTopLevelValidator(
        IServiceProvider serviceProvider,
        ILogger? logger) : INodeValidator
    {
        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Create];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        {
            if (!string.Equals(context.Node.NodeType, SpaceNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                return Observable.Return(NodeValidationResult.Valid());

            if (!string.IsNullOrEmpty(context.Node.Namespace))
                return Observable.Return(NodeValidationResult.Invalid(
                    $"A Space must be top-level: it is a partition root, so its path is just its id. " +
                    $"Cannot create Space '{context.Node.Id}' under namespace '{context.Node.Namespace}'.",
                    NodeRejectionReason.InvalidPath));

            // Eagerly provision the partition's schema + tables BEFORE the root write,
            // so neither the Space root write nor any follow-up child touch races a
            // missing relation. Idempotent + routed through the stored proc.
            var partitionName = context.Node.Id;
            return Observable
                .FromAsync(ct => ProvisionPartitionAsync(partitionName, ct))
                .Select(_ => NodeValidationResult.Valid());
        }

        private async Task ProvisionPartitionAsync(string partitionName, CancellationToken ct)
        {
            var accessService = serviceProvider.GetService<AccessService>();
            var providers = serviceProvider.GetServices<IPartitionStorageProvider>().ToList();

            // ImpersonateAsSystem: creating a brand-new partition schema is an
            // infrastructure write the creating user has no permission for yet (the
            // partition root doesn't exist, so no AccessAssignment grants Create on it).
            IDisposable? scope = null;
            try
            {
                scope = accessService?.ImpersonateAsSystem();
                foreach (var provider in providers)
                {
                    try
                    {
                        await provider.EnsurePartitionProvisionedAsync(partitionName, ct);
                    }
                    catch (Exception ex)
                    {
                        // Surface but don't necessarily fail the whole create: the lazy
                        // path-routing provisioning still runs on first write. Log so a
                        // genuinely broken backend is visible.
                        logger?.LogWarning(ex,
                            "Eager partition provisioning failed for Space '{Partition}' via provider {Provider}",
                            partitionName, provider.Name);
                    }
                }
                logger?.LogInformation(
                    "Eagerly provisioned partition '{Partition}' for new Space across {Count} provider(s)",
                    partitionName, providers.Count);
            }
            finally
            {
                scope?.Dispose();
            }
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
