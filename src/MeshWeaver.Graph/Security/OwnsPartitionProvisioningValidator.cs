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
/// ghost schema for an arbitrary path segment (the prod 45-ghost-schema corruption). So the
/// partition schema MUST exist before the root write, and this validator is the one place that
/// makes it so. See <c>Doc/Architecture/PartitionStorageRouting.md</c>.</para>
///
/// <list type="number">
///   <item><b>Top-level only.</b> A partition-owning instance IS a partition root, so its path
///     is just its id (empty namespace). A non-empty namespace is rejected up front — a nested
///     partition root would leave a half-registered split state. This holds for a type declared in
///     mesh content too, resolved from the definition's durable row so the refusal never activates
///     the type's hub (<see cref="PartitionOwningTypes.OwnsPartitionWithoutActivating"/>).</item>
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
        if (string.IsNullOrEmpty(context.Node.NodeType))
            return Observable.Return(NodeValidationResult.Valid());

        // A type registered in src/ answers from the static registry, synchronously — and for it
        // the nested-create refusal inside Provision applies to every create.
        if (_hub.ServiceProvider.FindStaticNode(context.Node.NodeType) is { } staticType)
            return staticType.ContentAs<NodeTypeDefinition>(_hub.JsonSerializerOptions) is { OwnsPartition: true }
                ? Provision(context)
                : Observable.Return(NodeValidationResult.Valid());

        // 🚨 A type declared in MESH CONTENT (Crm/Client) is invisible to FindStaticNode, and asking
        // only the static registry is why its top-level create was never provisioned — and why a
        // NESTED instance of it was never refused (#4449 item 1).
        //
        // A nested create is the ordinary content path, so it must not ask the way the top-level
        // path does: that resolver ends in GetMeshNodeStream(<type>), and for a per-node hub the read
        // IS the activation — a cold compile that can outlast the probe budget and fail closed,
        // i.e. an intermittent refusal of routine content. It reads the definition's DURABLE row
        // instead: the same store the create's existence check reads next, so the answer never
        // depends on which hubs are warm, and a store that cannot answer refuses as Unavailable
        // (retryable) exactly where the existence check would have failed the create anyway.
        // Doc/Architecture/PartitionOwnershipResolution.
        if (!string.IsNullOrEmpty(context.Node.Namespace))
            return PartitionOwningTypes.OwnsPartitionWithoutActivating(_hub, context.Node.NodeType)
                .Select(owns => owns switch
                {
                    true => PartitionOwningTypes.NestedInstanceRefused(context),
                    false => NodeValidationResult.Valid(),
                    null => PartitionOwningTypes.Undetermined(context),
                });

        return PartitionOwningTypes.OwnsPartitionOnce(_hub, context)
            .SelectMany(owns => owns switch
            {
                true => Provision(context),
                false => Observable.Return(NodeValidationResult.Valid()),
                null => Observable.Return(PartitionOwningTypes.Undetermined(context)),
            });
    }

    /// <summary>
    /// Provisions the partition of a create whose type OWNS its partition: refuses a nested
    /// instance, validates the partition id, then provisions every provider's backing store
    /// before the root write.
    /// </summary>
    private IObservable<NodeValidationResult> Provision(NodeValidationContext context)
    {
        // A partition-owning instance is a partition root → must be top-level. Same keyed refusal
        // an in-mesh owning type gets, in the caller's language.
        if (!string.IsNullOrEmpty(context.Node.Namespace))
            return Observable.Return(PartitionOwningTypes.NestedInstanceRefused(context));

        var partitionName = context.Node.Id;
        if (string.IsNullOrEmpty(partitionName))
            return Observable.Return(NodeValidationResult.Valid());

        // 🚨 Defense-in-depth (#714): the partition id becomes the backing schema name.
        // A malformed id (URL/query-string shaped — '?', '%', ':', '=', '&', whitespace;
        // >63 chars; not starting with a letter/digit) must NEVER provision a schema. The
        // storage routers refuse to ROUTE such segments (the same shared
        // PartitionDefinition.IsValidPartitionSegment rule), so a partition created with
        // one would be permanently unreachable anyway — reject the create with a speaking
        // error naming the offending id instead of provisioning junk.
        // 🚨 NO `_`-prefix exemption. A `_`-prefixed name is a SATELLITE CONTAINER segment
        // ({P}/_Access/…, {P}/_Thread/…) or a global-satellite NAMESPACE, never a partition:
        // the router resolves a `_`-prefixed first segment ONLY through a registered
        // PartitionDefinition with an EXPLICIT schema (`_Access` → `system_access`) and never
        // derives one from the name, so a schema named `_access` is unroutable by construction —
        // exactly the ghost this guard exists to prevent. A `Space` create with id `_Access`
        // is therefore a bug in the CALLER, and rejecting it loud is the point.
        if (!PartitionDefinition.IsValidPartitionSegment(partitionName))
            return Observable.Return(NodeValidationResult.Invalid(
                $"Cannot create '{context.Node.NodeType}' with id '{partitionName}': "
                + $"{PartitionDefinition.PartitionSegmentRequirement}.",
                NodeRejectionReason.InvalidPath));

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
            .Select(_ => NodeValidationResult.Valid());
    }
}
