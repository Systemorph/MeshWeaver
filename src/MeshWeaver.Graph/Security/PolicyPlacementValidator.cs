using System.Reactive.Linq;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Data.Validation;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Refuses a <c>PartitionAccessPolicy</c> written where the permission fold can never read it, so a
/// restriction that would not apply fails at the write instead of being stored and ignored.
///
/// <para><b>Where the fold reads policies.</b> The fold reads two places: the root node at
/// <c>_Policy</c>, and every node with id <c>_Policy</c> in the target's partition
/// (<c>path:{partition} scope:descendants id:_Policy nodeType:PartitionAccessPolicy</c>). That
/// partition read is a CONTENT read, so it covers the partition's primary table only.
/// <list type="bullet">
/// <item>A node whose path routes to a satellite table is never returned. The partition's EFFECTIVE
/// storage mappings decide the routing: <see cref="PartitionDefinition.TableMappings"/> on its
/// <c>Admin/Partition</c> record, or the standard layout when it CONFIRMABLY has none. They are
/// matched as <see cref="PartitionDefinition.ResolveTable"/> matches them, case-insensitively. That
/// covers <c>_storage</c> as well as <c>_Storage</c>, and a custom segment as well as a standard
/// one.</item>
/// <item>The fold also skips any policy whose id is not <c>_Policy</c>.</item>
/// </list>
/// So <c>{partition}/_Storage/_Policy</c> with <c>BreaksInheritance</c> was written, read back by
/// path, and never applied: the inherited grant kept granting. A statically declared node of the same
/// shape DID apply, because static policies are collected from the static node providers and never
/// pass through the query. Pinned by <c>RuntimeInheritanceBreakingPolicyTest</c>.</para>
///
/// <para><b>Why refuse rather than widen the read.</b> A satellite's access is decided through its
/// main node (<c>SatelliteAccessRule</c>) or by its type's own rule (a storage binding asks for Update
/// on the partition root). A policy belongs on that main node's scope. Making the fold read every
/// satellite table of every partition would cost on every permission check, and it would serve only
/// a placement no platform writer uses.</para>
///
/// <para><b>Every route that places a policy.</b>
/// <list type="bullet">
/// <item>A create runs it in the creation chain.</item>
/// <item><c>IMeshService.UpdateNode</c> runs it in its update chain when the update retypes a node
/// INTO a policy.</item>
/// <item>The upsert runs it through <see cref="INodeTypeTransitionValidator"/>.</item>
/// <item>A client merge patch of the node (<c>GetMeshNodeStream(path).Update</c> from another hub) and a
/// synced write-back reach the owning hub, which runs it in <see cref="PolicyPlacementGuard.AddPolicyPlacementGuard"/>,
/// against the node it holds.</item>
/// </list>
/// An update of a node that is ALREADY a policy is never judged, so a legacy policy stays editable. The
/// type name matches case-insensitively, the way NodeType resolution matches it.</para>
///
/// <para><b>An unread layout is not the standard one.</b> The partition record is read as System, with
/// an anchored query. A read that faults or never answers REFUSES the write as
/// <see cref="NodeRejectionReason.Unavailable"/>. Only a completed read with no record falls back to
/// the standard layout. Not an <see cref="IOwnerEnforcedNodeValidator"/>: this is placement
/// integrity, and the caller must see the refusal before the write is issued.</para>
/// </summary>
public sealed class PolicyPlacementValidator(IMessageHub hub) : INodeValidator, INodeTypeTransitionValidator
{
    /// <summary>The only id the permission fold reads a policy under.</summary>
    public const string PolicyId = "_Policy";

    /// <summary>Create, and Update for a write that turns a node INTO a policy.</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Create, NodeOperation.Update];

    /// <summary>Validates where a <c>PartitionAccessPolicy</c> node is placed.</summary>
    /// <param name="context">The validation context describing the node and operation.</param>
    /// <returns>An observable emitting the one validation result.</returns>
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        if (!PlacesAPolicy(context))
            return Observable.Return(NodeValidationResult.Valid());

        // The id rule needs no layout.
        if (Judge(context, null) is { IsValid: false } byId && IsIdRefusal(context))
            return Observable.Return(byId);

        var partition = PartitionOf(context.Node.Path);
        if (string.IsNullOrEmpty(partition))
            return Observable.Return(NodeValidationResult.Valid());

        return ReadLayout(partition)
            .Select(layout => layout.Established
                ? Judge(context, layout.Definition)
                : NodeValidationResult.Invalid(
                    LocalizationCatalog.Get("policy.placement.unestablished", context.AccessContext?.Locale,
                        context.Node.Path, partition),
                    NodeRejectionReason.Unavailable))
            .Catch<NodeValidationResult, Exception>(_ => Observable.Return(NodeValidationResult.Invalid(
                LocalizationCatalog.Get("policy.placement.unestablished", context.AccessContext?.Locale,
                    context.Node.Path, partition),
                NodeRejectionReason.Unavailable)));
    }

    /// <summary>
    /// The partition's record, read as System with an ANCHORED query (no listing limit to fall
    /// short of). <c>Established</c> is false when the read did not complete with an answer; a
    /// completed read with no record is established, with a null definition.
    /// </summary>
    private IObservable<(bool Established, PartitionDefinition? Definition)> ReadLayout(string partition)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        var options = hub.JsonSerializerOptions;
        return access.RunAsSystem(() => meshService.Query<MeshNode>(new MeshQueryRequest
                {
                    Query = $"namespace:{PartitionNodeType.Namespace} nodeType:{PartitionNodeType.NodeType} id:{partition}",
                    Skip = 0,
                    Limit = 10,
                }))
            .Where(change => change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1)
            .Select(change => (Established: true, Definition: (change.Items ?? [])
                .Select(n => n.ContentAs<PartitionDefinition>(options))
                .FirstOrDefault(d => d is not null
                    && string.Equals(d.Namespace, partition, StringComparison.OrdinalIgnoreCase))))
            .DefaultIfEmpty((Established: false, Definition: null));
    }

    /// <summary>
    /// True when the write places a <c>PartitionAccessPolicy</c>: a create of one, or an update that
    /// turns a node of another type into one. An update of an existing policy is not a placement.
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <returns>Whether the placement rule applies.</returns>
    public static bool PlacesAPolicy(NodeValidationContext context)
    {
        if (!IsPolicyType(context.Node.NodeType))
            return false;
        return context.Operation switch
        {
            NodeOperation.Create => true,
            NodeOperation.Update => !IsPolicyType(context.ExistingNode?.NodeType),
            _ => false,
        };
    }

    /// <summary>
    /// The verdict for a placement, against <paramref name="definition"/>: the partition's effective
    /// definition, or null for the standard layout.
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="definition">The partition's effective definition, or null for the standard layout.</param>
    /// <returns>The validation result.</returns>
    public static NodeValidationResult Judge(NodeValidationContext context, PartitionDefinition? definition)
    {
        if (!PlacesAPolicy(context))
            return NodeValidationResult.Valid();

        var node = context.Node;
        var locale = context.AccessContext?.Locale;
        if (IsIdRefusal(context))
            return NodeValidationResult.Invalid(
                LocalizationCatalog.Get("policy.placement.invalidId", locale, node.Path, PolicyId));

        var partition = PartitionOf(node.Path);
        if (string.IsNullOrEmpty(partition))
            return NodeValidationResult.Valid();

        var effective = Effective(definition, partition);
        if (string.Equals(effective.ResolveTable(node.Path), effective.Table, StringComparison.Ordinal))
            return NodeValidationResult.Valid();

        return NodeValidationResult.Invalid(
            LocalizationCatalog.Get("policy.placement.satellite", locale,
                node.Path, node.Namespace ?? string.Empty, SuggestedPath(node.Path, effective)));
    }

    /// <summary>
    /// Where the policy belongs: the scope in front of the first path segment that routes to a
    /// satellite table, matched as <see cref="PartitionDefinition.ResolveTable"/> matches it
    /// (case-insensitively), plus <see cref="PolicyId"/>.
    /// </summary>
    /// <param name="path">The refused policy's path.</param>
    /// <param name="definition">The partition's effective definition.</param>
    /// <returns>The suggested policy path.</returns>
    public static string SuggestedPath(string path, PartitionDefinition definition)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var mapped = definition.TableMappings?.Keys ?? (IEnumerable<string>)[];
        for (var i = 0; i < segments.Length; i++)
        {
            if (mapped.Any(m => string.Equals(m, segments[i], StringComparison.OrdinalIgnoreCase)))
                return i == 0 ? PolicyId : $"{string.Join('/', segments[..i])}/{PolicyId}";
        }
        return path;
    }

    /// <summary>Whether <paramref name="nodeType"/> names the policy type, matched as NodeType resolution matches it.</summary>
    /// <param name="nodeType">The NodeType.</param>
    /// <returns>True for <c>PartitionAccessPolicy</c> in any casing.</returns>
    public static bool IsPolicyType(string? nodeType)
        => string.Equals(nodeType, PartitionAccessPolicyNodeType.NodeType, StringComparison.OrdinalIgnoreCase);

    private static bool IsIdRefusal(NodeValidationContext context)
        => !string.Equals(context.Node.Id, PolicyId, StringComparison.Ordinal);

    private static PartitionDefinition Effective(PartitionDefinition? definition, string partition)
        => definition is { TableMappings: not null }
            ? definition
            : (definition ?? new PartitionDefinition { Namespace = partition }) with
            {
                TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            };

    private static string PartitionOf(string? path)
    {
        var segments = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1 ? segments[0] : string.Empty;
    }

    internal static string PartitionOfPath(string? path) => PartitionOf(path);
}

/// <summary>Installs <see cref="PolicyPlacementValidator"/> at the owning hub's write boundary.</summary>
public static class PolicyPlacementGuard
{
    /// <summary>
    /// The rule at the OWNING hub's write boundary, for the two routes that reach a node's hub
    /// without passing a validator chain: a client merge patch of the node (<see cref="PatchDataRequest"/>,
    /// which is every <c>GetMeshNodeStream(path).Update</c> from another hub), and a synced
    /// write-back (<see cref="DataChangeRequest"/>, through <see cref="PolicyPlacementDataValidator"/>).
    /// Both are judged against the node this hub HOLDS, so an update of a node that is already a
    /// policy passes, and only a retype into one is checked. A patch that is not a retype into a
    /// policy continues on the same turn. Only the rare retype waits for the layout read, the way the
    /// access gate continues after its fold.
    /// </summary>
    /// <param name="config">The node hub's configuration.</param>
    /// <returns>The configuration with the guard installed.</returns>
    public static MessageHubConfiguration AddPolicyPlacementGuard(this MessageHubConfiguration config)
        => config
            .WithServices(services => services.AddScoped<IDataValidator, PolicyPlacementDataValidator>())
            .AddDeliveryPipeline(pipeline =>
            {
                var hub = pipeline.Hub;
                return pipeline.AddPipeline((delivery, ct, next) =>
                {
                    if (RetypeIntoAPolicyByPatch(hub, delivery) is not { } context)
                        return next.Invoke(delivery, ct);

                    var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                        ?.CreateLogger("MeshWeaver.Graph.PolicyPlacement");
                    new PolicyPlacementValidator(hub).Validate(context)
                        .Take(1)
                        .DefaultIfEmpty(NodeValidationResult.Invalid(
                            LocalizationCatalog.Get("policy.placement.unestablished", context.AccessContext?.Locale,
                                context.Node.Path, PolicyPlacementValidator.PartitionOfPath(context.Node.Path)),
                            NodeRejectionReason.Unavailable))
                        .Subscribe(
                            result =>
                            {
                                if (result.IsValid)
                                {
                                    next.Invoke(delivery, ct).Subscribe(_ => { },
                                        ex => logger?.LogError(ex,
                                            "PolicyPlacement: downstream pipeline faulted for {Path}", context.Node.Path));
                                    return;
                                }
                                logger?.LogWarning("PolicyPlacement: refused a retype of {Path} from {Sender}: {Message}",
                                    context.Node.Path, delivery.Sender, result.ErrorMessage);
                                hub.Post(new DeliveryFailure(delivery)
                                {
                                    ErrorType = result.Reason == NodeRejectionReason.Unavailable
                                        ? ErrorType.Unavailable
                                        : ErrorType.Rejected,
                                    Message = result.ErrorMessage,
                                }, o => o.ResponseFor(delivery));
                            },
                            ex => logger?.LogError(ex, "PolicyPlacement: the guard faulted for {Path}", context.Node.Path));
                    return Observable.Return(delivery.Forwarded());
                });
            });

    /// <summary>
    /// The Update context of a client merge patch of this hub's own node that turns it INTO a
    /// policy, or null for every other delivery.
    /// </summary>
    private static NodeValidationContext? RetypeIntoAPolicyByPatch(IMessageHub hub, IMessageDelivery delivery)
    {
        if (delivery.Message is not PatchDataRequest { Reference: MeshNodeReference } patchRequest)
            return null;
        var ownPath = hub.Address.Path;
        if (delivery.Target is { } target && !string.Equals(target.Path, ownPath, StringComparison.OrdinalIgnoreCase))
            return null;
        if (delivery.Sender is { } sender && string.Equals(sender.Path, ownPath, StringComparison.OrdinalIgnoreCase))
            return null;
        if (JsonNode.Parse(patchRequest.Patch.Content ?? "{}") is not JsonObject patch)
            return null;
        var key = hub.JsonSerializerOptions.PropertyNamingPolicy?.ConvertName(nameof(MeshNode.NodeType))
                  ?? nameof(MeshNode.NodeType);
        var proposed = patch
            .Where(kvp => string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
            .FirstOrDefault();
        var live = StampedFields.LiveOwnNode(hub);
        if (live is null || !PolicyPlacementValidator.IsPolicyType(proposed) || PolicyPlacementValidator.IsPolicyType(live.NodeType))
            return null;
        return new NodeValidationContext
        {
            Operation = NodeOperation.Update,
            Node = live with { NodeType = proposed },
            ExistingNode = live,
            AccessContext = delivery.AccessContext,
        };
    }
}

/// <summary>
/// The synced write-back half of <see cref="PolicyPlacementGuard.AddPolicyPlacementGuard"/>: a
/// <see cref="DataChangeRequest"/> update of the hub's own node that turns it into a policy is
/// judged against the node the hub holds.
/// </summary>
internal sealed class PolicyPlacementDataValidator(IMessageHub hub) : IDataValidator
{
    /// <inheritdoc />
    public IReadOnlyCollection<DataOperation> SupportedOperations => [DataOperation.Update, DataOperation.Create];

    /// <inheritdoc />
    public IObservable<DataValidationResult> Validate(DataValidationContext context)
    {
        var live = StampedFields.LiveOwnNode(hub);
        if (live is null || PolicyPlacementValidator.IsPolicyType(live.NodeType))
            return Observable.Return(DataValidationResult.Valid());

        var options = hub.JsonSerializerOptions;
        var after = context.Entity switch
        {
            EntityDeltaUpdate delta when delta.Collection == nameof(MeshNode)
                && (delta.Id == live.Id || string.Equals(delta.Id, live.Path, StringComparison.OrdinalIgnoreCase))
                => EntityDelta.Apply(live, delta, options) as MeshNode,
            EntityDeltaUpdate => null,
            { } entity when entity.As<MeshNode>(options) is { } node
                && string.Equals(node.Path, live.Path, StringComparison.OrdinalIgnoreCase) => node,
            _ => null,
        };
        if (after is null || !PolicyPlacementValidator.IsPolicyType(after.NodeType))
            return Observable.Return(DataValidationResult.Valid());

        return new PolicyPlacementValidator(hub).Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Update,
                Node = after,
                ExistingNode = live,
                AccessContext = context.AccessContext,
            })
            .Take(1)
            .DefaultIfEmpty(NodeValidationResult.Invalid(
                LocalizationCatalog.Get("policy.placement.unestablished", context.AccessContext?.Locale,
                    after.Path, after.Path.Split('/')[0]),
                NodeRejectionReason.Unavailable))
            .Select(result => result.IsValid
                ? DataValidationResult.Valid()
                : DataValidationResult.Invalid(result.ErrorMessage ?? live.Path,
                    result.Reason == NodeRejectionReason.Unavailable
                        ? DataValidationRejectionReason.Unknown
                        : DataValidationRejectionReason.ValidationFailed));
    }
}
