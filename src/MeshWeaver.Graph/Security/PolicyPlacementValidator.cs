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
            .Select(change => ClassifyLayout(change, partition, options))
            .DefaultIfEmpty((Established: false, Definition: null));
    }

    /// <summary>
    /// What one answer of the layout read establishes. Pure.
    /// <list type="bullet">
    /// <item>An INCOMPLETE answer (<see cref="QueryResultChange{T}.SnapshotIncomplete"/>, or a provider in
    /// <see cref="QueryResultChange{T}.SilentProviders"/>) establishes nothing: its empty list is not
    /// "no record".</item>
    /// <item>A returned record whose content does not read as a <see cref="PartitionDefinition"/> for this
    /// partition establishes nothing either: it is not confirmed absence.</item>
    /// <item>Only a complete answer without a record is the standard layout.</item>
    /// </list>
    /// </summary>
    /// <param name="change">The read's Initial/Reset answer.</param>
    /// <param name="partition">The partition whose record was read.</param>
    /// <param name="options">The hub's serializer options.</param>
    /// <returns>Whether the layout is established, and the record's definition when there is one.</returns>
    public static (bool Established, PartitionDefinition? Definition) ClassifyLayout(
        QueryResultChange<MeshNode> change, string partition, System.Text.Json.JsonSerializerOptions options)
    {
        if (change.SnapshotIncomplete || change.SilentProviders is { Count: > 0 })
            return (false, null);
        var records = (change.Items ?? []).Where(n => n is not null).ToList();
        if (records.Count == 0)
            return (true, null);
        var definition = records
            .Select(n => n.ContentAs<PartitionDefinition>(options))
            .FirstOrDefault(d => d is not null
                && string.Equals(d.Namespace, partition, StringComparison.OrdinalIgnoreCase));
        return definition is null ? (false, null) : (true, definition);
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
                    if (!IsClientPatchOfThisHub(hub, delivery))
                        return next.Invoke(delivery, ct);
                    var patchRequest = (PatchDataRequest)delivery.Message;
                    var verdict = PatchVerdict(
                        JsonNode.Parse(patchRequest.Patch.Content ?? "{}"),
                        patchRequest.Reference is MeshNodeReference,
                        StampedFields.LiveOwnNode(hub),
                        hub.JsonSerializerOptions,
                        patchRequest.BaseValues is { Content: { } baseContent } ? JsonNode.Parse(baseContent) : null);
                    if (verdict == PatchRetype.None)
                        return next.Invoke(delivery, ct);

                    var locale = delivery.AccessContext?.Locale;
                    var message = verdict == PatchRetype.OwnerNotLoaded
                        ? LocalizationCatalog.Get("policy.placement.ownerNotLoaded", locale, hub.Address.Path)
                        : LocalizationCatalog.Get("policy.placement.patchRetype", locale, hub.Address.Path);
                    hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Graph.PolicyPlacement")
                        .LogWarning("PolicyPlacement: refused a patch of {Path} from {Sender}: {Message}",
                            hub.Address.Path, delivery.Sender, message);
                    hub.Post(new DeliveryFailure(delivery)
                    {
                        ErrorType = verdict == PatchRetype.OwnerNotLoaded ? ErrorType.Unavailable : ErrorType.Rejected,
                        Message = message,
                    }, o => o.ResponseFor(delivery));
                    return Observable.Return(delivery.Forwarded());
                });
            });

    /// <summary>What a client merge patch does to the hub's node type, as far as placement is concerned.</summary>
    public enum PatchRetype
    {
        /// <summary>The patch does not turn the node into a policy: it continues on this turn.</summary>
        None,
        /// <summary>The patch turns a node of another type into a policy: refused.</summary>
        IntoAPolicy,
        /// <summary>The patch names the policy type, and the hub has not loaded its node, so it cannot tell.</summary>
        OwnerNotLoaded,
    }

    /// <summary>
    /// The verdict on a client merge patch, decided SYNCHRONOUSLY on the hub's turn. Pure.
    ///
    /// <para><b>Why a patch is refused rather than judged.</b> Placement needs the partition's layout,
    /// which is a read. Waiting for it here would let a later patch from the same sender pass ahead of
    /// this one. So a patch that turns a node INTO a policy is refused, and a policy is created as one
    /// (<c>{scope}/_Policy</c>). <c>IMeshService.UpdateNode</c> and the upsert reach the owner as this
    /// same patch, so a retype into a policy is refused on those routes too, even where its placement
    /// is valid (#7017 review; tracked in triage). A patch of a node that already IS a policy is not a
    /// retype and passes, so legacy policies stay editable.</para>
    ///
    /// <para><b>The owner's merge semantics, not the literal.</b> A string leaf can also arrive as a
    /// SPLICE (<c>{"$sd":[start,removed,inserted]}</c>, <see cref="MeshWeaver.Data.Serialization.PatchStringSplice"/>),
    /// which the owner applies to the live value. A spliced <c>nodeType</c> is judged on the value the
    /// splice produces from the live type. A splice whose base value differs from the live type, which the
    /// owner would rebase, and any other non-string <c>nodeType</c> cannot be judged here. Both count as
    /// naming the policy type.</para>
    ///
    /// <para><b>Every reference shape.</b> A <see cref="MeshNodeReference"/> patch is the node itself, so
    /// only its top-level <c>nodeType</c> counts. Any other reference (an
    /// <see cref="EntityReference"/> to the <c>MeshNode</c> collection, a JSON pointer, …) is searched at
    /// every depth, because its shape says nothing this guard can map field by field.</para>
    ///
    /// <para><b>A cold owner is not permission.</b> When the hub has not loaded its node, a patch that
    /// names the policy type is refused as Unavailable, never let through.</para>
    /// </summary>
    /// <param name="patch">The parsed patch.</param>
    /// <param name="isNodeReference">Whether the patch addresses the node itself.</param>
    /// <param name="live">The node as the hub holds it; null before it has loaded.</param>
    /// <param name="options">The hub's serializer options.</param>
    /// <param name="baseValues">The patch's <c>BaseValues</c>, the writer's old values, when it carries them.</param>
    /// <returns>The verdict.</returns>
    public static PatchRetype PatchVerdict(
        JsonNode? patch, bool isNodeReference, MeshNode? live, System.Text.Json.JsonSerializerOptions options,
        JsonNode? baseValues = null)
    {
        var key = options.PropertyNamingPolicy?.ConvertName(nameof(MeshNode.NodeType)) ?? nameof(MeshNode.NodeType);
        var baseType = baseValues is JsonObject baseObject
            ? baseObject.FirstOrDefault(kvp => IsNodeTypeKey(kvp.Key, key)).Value
            : null;
        var namesPolicy = isNodeReference
            ? patch is JsonObject top && top.Any(kvp => IsNodeTypeKey(kvp.Key, key) && MergesIntoPolicy(kvp.Key, kvp.Value, live, baseType))
            : NamesPolicyAnywhere(patch, key);
        if (!namesPolicy)
            return PatchRetype.None;
        if (live is null)
            return PatchRetype.OwnerNotLoaded;
        return PolicyPlacementValidator.IsPolicyType(live.NodeType) ? PatchRetype.None : PatchRetype.IntoAPolicy;
    }

    private static bool IsNodeTypeKey(string candidate, string key)
        => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a <c>nodeType</c> patch value would leave the node as a policy, under the owner's merge
    /// semantics, or cannot be judged (which counts as yes).
    /// </summary>
    private static bool NamesPolicy(JsonNode? value, MeshNode? live, JsonNode? baseValue)
    {
        switch (value)
        {
            case null:
                return false;
            case JsonValue v when v.TryGetValue<string>(out var literal):
                return PolicyPlacementValidator.IsPolicyType(literal);
        }
        if (MeshWeaver.Data.Serialization.PatchStringSplice.TryDecode(value, out var splice))
        {
            if (live?.NodeType is not { } current)
                return true;
            if (baseValue is JsonValue b && b.TryGetValue<string>(out var baseString)
                && !string.Equals(baseString, current, StringComparison.Ordinal))
                return true;
            return PolicyPlacementValidator.IsPolicyType(splice.Apply(current));
        }
        return true;
    }

    /// <summary>
    /// Whether the owner's own merge (<see cref="MeshWeaver.Data.Serialization.MeshNodePatchMerge.Apply"/>)
    /// of this <c>nodeType</c> leaf onto the live type leaves a policy type. The merge is RUN, not
    /// re-implemented: a literal with a stale base is three-way merged by the owner (disjoint edits of
    /// <c>Parition</c>→<c>Partition</c> and →<c>ParitionAccessPolicy</c> produce
    /// <c>PartitionAccessPolicy</c>), and a splice is applied to the live value. A cold owner
    /// (<paramref name="live"/> null) or a non-string merge result that is not plainly another type
    /// cannot be judged and counts as yes.
    /// </summary>
    private static bool MergesIntoPolicy(string patchKey, JsonNode? value, MeshNode? live, JsonNode? baseValue)
    {
        if (value is null)
            return false;
        if (live is null)
            return NamesPolicy(value, null, null);
        var simulated = new JsonObject { [patchKey] = live.NodeType is { } current ? JsonValue.Create(current) : null };
        var patchLeaf = new JsonObject { [patchKey] = value.DeepClone() };
        var baseLeaf = baseValue is null ? null : new JsonObject { [patchKey] = baseValue.DeepClone() };
        MeshWeaver.Data.Serialization.MeshNodePatchMerge.Apply(simulated, patchLeaf, baseLeaf);
        return simulated[patchKey] switch
        {
            null => false,
            JsonValue v when v.TryGetValue<string>(out var merged) => PolicyPlacementValidator.IsPolicyType(merged),
            _ => true,
        };
    }

    private static bool NamesPolicyAnywhere(JsonNode? node, string key) => node switch
    {
        JsonObject obj => obj.Any(kvp => (IsNodeTypeKey(kvp.Key, key) && NamesPolicy(kvp.Value, null, null))
                                         || NamesPolicyAnywhere(kvp.Value, key)),
        JsonArray array => array.Any(item => NamesPolicyAnywhere(item, key)),
        _ => false,
    };

    /// <summary>
    /// A merge patch ADDRESSED to this hub's own node that came from anywhere but this hub. The owning
    /// hub's own writes never arrive as a message, so the sender test is exact.
    /// </summary>
    private static bool IsClientPatchOfThisHub(IMessageHub hub, IMessageDelivery delivery)
    {
        if (delivery.Message is not PatchDataRequest)
            return false;
        var ownPath = hub.Address.Path;
        if (delivery.Target is { } target && !string.Equals(target.Path, ownPath, StringComparison.OrdinalIgnoreCase))
            return false;
        return delivery.Sender is null
            || !string.Equals(delivery.Sender.Path, ownPath, StringComparison.OrdinalIgnoreCase);
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
        var options = hub.JsonSerializerOptions;

        // 🚨 A policy written through ANOTHER node's hub (#7017 review). A DataChangeRequest may carry
        // a MeshNode whose path is not this hub's; the change pipeline adds it to this hub's collection
        // and the type source persists it, so the target's own owner never sees it. Such a write of a
        // policy cannot be judged against the target's predecessor here: refuse it.
        if (context.Entity is not EntityDeltaUpdate
            && context.Entity.As<MeshNode>(options) is { Path.Length: > 0 } carried
            && PolicyPlacementValidator.IsPolicyType(carried.NodeType)
            && !string.Equals(carried.Path, hub.Address.Path, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(DataValidationResult.Invalid(
                LocalizationCatalog.Get("policy.placement.foreignWrite", context.AccessContext?.Locale,
                    carried.Path, hub.Address.Path),
                DataValidationRejectionReason.ValidationFailed));

        var held = StampedFields.LiveOwnNode(hub);
        if (held is not null)
            return Judge(context, held, options);

        // 🚨 A cold owner is not permission (#7017 review). Establish the node from storage: absent
        // there, the write is a create and the creation chain judges it
        // (MeshNodeCreationDataValidator); present, it is judged against the stored node. A read that
        // answers nothing refuses as Unavailable.
        if (context.Entity.As<MeshNode>(options) is not { Path.Length: > 0 } incoming
            || !PolicyPlacementValidator.IsPolicyType(incoming.NodeType))
            return Observable.Return(context.Entity is EntityDeltaUpdate
                ? DataValidationResult.Invalid(
                    LocalizationCatalog.Get("policy.placement.ownerNotLoaded", context.AccessContext?.Locale, hub.Address.Path),
                    DataValidationRejectionReason.Unknown)
                : DataValidationResult.Valid());
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            return Observable.Return(DataValidationResult.Valid());
        return Observable.Defer(() => storage.Read(incoming.Path, options))
            .Take(1)
            .Select(stored => (Answered: true, Stored: stored))
            .DefaultIfEmpty((Answered: false, Stored: null))
            .SelectMany(read => !read.Answered
                ? Observable.Return(DataValidationResult.Invalid(
                    LocalizationCatalog.Get("policy.placement.ownerNotLoaded", context.AccessContext?.Locale, hub.Address.Path),
                    DataValidationRejectionReason.Unknown))
                : read.Stored is null
                    ? Observable.Return(DataValidationResult.Valid())
                    : Judge(context, read.Stored, options));
    }

    private IObservable<DataValidationResult> Judge(
        DataValidationContext context, MeshNode live, System.Text.Json.JsonSerializerOptions options)
    {
        if (PolicyPlacementValidator.IsPolicyType(live.NodeType))
            return Observable.Return(DataValidationResult.Valid());

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
