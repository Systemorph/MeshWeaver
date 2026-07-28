using System.Reactive;
using MeshWeaver.Data;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// The <b>single, centralized trigger for partition-schema creation.</b> Creating a
/// top-level instance of a NodeType whose <see cref="NodeTypeDefinition.OwnsPartition"/>
/// is <c>true</c> (today: <c>User</c> and <c>Space</c>) provisions that partition's backing
/// store — the Postgres schema + its tables — <i>before</i> the root write. This runs in the
/// create-validation chain (alongside <see cref="RlsNodeValidator"/> and
/// <see cref="PartitionWriteGuardValidator"/>) so it fires on EVERY create path: MCP
/// <c>create</c>, onboarding, GUI, agents.
///
/// <para>This replaces the old per-type <c>SpaceTopLevelValidator</c> and the User-onboarding
/// reliance on lazy create-on-first-write. The knowledge of "which types own a partition" is
/// centralized on the NodeType definition (<see cref="NodeTypeDefinition.OwnsPartition"/>),
/// read here via <see cref="StaticNodeProviderExtensions.FindStaticNode"/> — no registry, no
/// per-type branch. Adding a new partition-owning type is a single <c>OwnsPartition = true</c>
/// line on its definition.</para>
///
/// <para><b>Why eager is now mandatory.</b> The storage router
/// (<c>PostgreSqlPathRoutingAdapter</c>) no longer lazily <c>CREATE SCHEMA</c>s on first write —
/// a write whose partition isn't provisioned now fails loudly (42P01) instead of conjuring a
/// ghost schema for an arbitrary path segment (the atioz 45-ghost-schema corruption). So the
/// partition schema MUST exist before the root write, and this validator is the one place that
/// makes it so. See <c>Doc/Architecture/PartitionStorageRouting.md</c>.</para>
///
/// <list type="number">
///   <item><b>Top-level only.</b> A partition-owning instance IS a partition root, so its path
///     is just its id (empty namespace). A non-empty namespace is rejected up front — a nested
///     partition root would leave a half-registered split state.</item>
///   <item><b>Eagerly provisioned.</b> Every <see cref="IPartitionStorageProvider.EnsurePartitionProvisioned"/>
///     runs (the Postgres provider routes to <c>public.ensure_partition_schema</c>; the async DB
///     edge is sealed inside <c>IIoPool</c> — no <c>await</c>, no <c>Observable.FromAsync</c> here).
///     Idempotent, so retries are harmless. A provisioning failure faults the create rather than
///     letting the subsequent root write 42P01 with a confusing error.</item>
/// </list>
///
/// Non-partition-owning creates (the overwhelming majority) short-circuit to Valid immediately.
/// 100% reactive: the validation chain composes this observable with <c>.Concat()</c> — no async.
/// </summary>
public sealed class OwnsPartitionProvisioningValidator : INodeValidator
{
    private readonly IMessageHub _hub;
    private readonly ILogger<OwnsPartitionProvisioningValidator> _logger;

    /// <summary>
    /// Initializes a new instance of the owns-partition provisioning validator.
    /// </summary>
    /// <param name="hub">The message hub providing static-node lookup and partition storage providers.</param>
    /// <param name="logger">The logger used to record partition provisioning.</param>
    public OwnsPartitionProvisioningValidator(
        IMessageHub hub,
        ILogger<OwnsPartitionProvisioningValidator> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    /// <summary>Create only — provisioning a NEW partition can only happen on create.</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Create];

    /// <summary>
    /// Validates a create, eagerly provisioning the backing partition store (Postgres
    /// schema + tables) before the root write when the node's type owns its partition,
    /// and rejecting a partition-owning create that is not top-level.
    /// </summary>
    /// <param name="context">The validation context describing the node and operation.</param>
    /// <returns>An observable that emits the validation result for the operation.</returns>
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        // Read the centralized partition-ownership flag off the NodeType's definition.
        // FindStaticNode resolves the type definition node (config-time AddMeshNodes +
        // IStaticNodeProvider); its Content is the NodeTypeDefinition.
        if (string.IsNullOrEmpty(context.Node.NodeType))
            return Observable.Return(NodeValidationResult.Valid());

        var def = _hub.ServiceProvider.FindStaticNode(context.Node.NodeType)?.Content
            as NodeTypeDefinition;
        if (def is not { OwnsPartition: true })
            return Observable.Return(NodeValidationResult.Valid());

        // A partition-owning instance is a partition root → must be top-level.
        if (!string.IsNullOrEmpty(context.Node.Namespace))
            return Observable.Return(NodeValidationResult.Invalid(
                $"A '{context.Node.NodeType}' owns its partition, so it must be top-level: its " +
                $"path is just its id. Cannot create '{context.Node.Id}' under namespace " +
                $"'{context.Node.Namespace}'.",
                NodeRejectionReason.InvalidPath));

        var partitionName = context.Node.Id;
        if (string.IsNullOrEmpty(partitionName))
            return Observable.Return(NodeValidationResult.Valid());

        var providers = _hub.ServiceProvider.GetServices<IPartitionStorageProvider>().ToList();
        if (providers.Count == 0)
            return Observable.Return(NodeValidationResult.Valid());

        var nodeType = context.Node.NodeType;
        // Provision every provider's backing store BEFORE the root write — the ONE place a
        // partition schema is created. Sequential (.Concat) so concurrent DDL never races;
        // a provider failure propagates → the create faults rather than the root write
        // 42P01-ing later. No await / no FromAsync — EnsurePartitionProvisioned is reactive.
        return providers
            .Select(p => p.EnsurePartitionProvisioned(partitionName))
            .Concat()
            .TakeLast(1)
            .Do(_ => _logger.LogInformation(
                "Provisioned partition '{Partition}' for new {NodeType} across {Count} provider(s)",
                partitionName, nodeType, providers.Count))
            // 🚨 REGISTER it as well — a schema nobody can ROUTE TO is not a usable partition.
            //
            // Creating the schema is only half of owning a partition. Routing learns which
            // partitions exist from the `Admin/Partition/*` nodes (the subscription started in
            // PostgreSqlExtensions so writes can route); without one, every address in the new
            // partition answers "No node found at '{partition}'" — the per-node hub then FAULTS on
            // activation, and because the lookup can never succeed it fails on every retry, not
            // once. The symptom is not an error the user sees: reads hang for the full 60s
            // SubscribeRequest timeout and the page dies blank.
            //
            // Packages got this for free (PackageInstaller writes the definition alongside the
            // install) and configured inclusions get it from IncludedPartitionStaticProvider — so
            // Space/package partitions routed while SELF-PROVISIONED USER partitions did not.
            // Observed 2026-07-29: schema "e2e-admin" existed, "e2e-admin".mesh_nodes held the
            // User node, auth.mesh_nodes mirrored it, and Admin/Partition held eleven entries —
            // every one a package or space, not one user. The install page hung for 3.5 minutes.
            //
            // This validator is documented above as the ONE place a partition's backing store is
            // created, so it is also the one place the partition must be made routable. Doing it
            // here keeps the two halves atomic instead of leaving a schema that only some code
            // paths know about.
            .SelectMany(_ => RegisterPartitionNode(partitionName, nodeType))
            .Select(_ => NodeValidationResult.Valid());
    }

    /// <summary>
    /// Writes the <c>Admin/Partition/{name}</c> definition that makes the partition ROUTABLE.
    ///
    /// <para>Idempotent by upsert: provisioning is re-entered on every top-level create of an
    /// owning type, and a partition that already routes must not fail the create.</para>
    ///
    /// <para>Best-effort on the write itself: the schema is already provisioned at this point, so
    /// failing the whole create because the registration write raced would trade a routable
    /// partition for no partition at all. A failure is logged loudly — it means addresses in this
    /// partition will not resolve until it is registered, which is exactly the state this exists to
    /// prevent, and it must be visible rather than silent.</para>
    /// </summary>
    private IObservable<Unit> RegisterPartitionNode(string partitionName, string? nodeType)
    {
        var node = new MeshNode(partitionName, PartitionNodeType.Namespace)
        {
            NodeType = PartitionNodeType.NodeType,
            Name = partitionName,
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                Namespace = partitionName,
                DataSource = "default",
                Schema = partitionName.ToLowerInvariant(),
                Table = "mesh_nodes",
                TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
                NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings(),
                Versioned = true,
                Description = $"Partition owned by {nodeType ?? "node"} '{partitionName}'",
            },
        };

        // Under the SYSTEM identity: registering a partition is framework bookkeeping triggered by
        // the create, not an act of the user creating it — they have no rights on Admin/Partition,
        // and requiring them would fail exactly the self-provisioning case this fixes.
        var access = _hub.ServiceProvider.GetService<AccessService>();
        return Observable.Using(
                () => access?.ImpersonateAsSystem() ?? System.Reactive.Disposables.Disposable.Empty,
                _ => _hub.GetWorkspace()
                    .GetMeshNodeStream(node.Path)
                    .Update(_ => node)
                    .Take(1))
            .Select(_ => Unit.Default)
            .Do(_ => _logger.LogInformation(
                "Registered partition '{Partition}' at {Path} — it is now routable",
                partitionName, node.Path))
            .Catch<Unit, Exception>(ex =>
            {
                _logger.LogError(ex,
                    "Partition '{Partition}' was provisioned but could NOT be registered at {Path}. "
                    + "Addresses in it will not route (reads hang to the SubscribeRequest timeout) "
                    + "until a definition exists.",
                    partitionName, node.Path);
                return Observable.Return(Unit.Default);
            });
    }
}
