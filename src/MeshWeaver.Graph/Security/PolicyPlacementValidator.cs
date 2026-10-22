using System.Reactive.Linq;
using System.Text.Json;
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
/// <item>Every write that COMMITS into an owner (a merge patch, a data change) meets
/// <see cref="PolicyCommitGuard"/> at the commit point, which refuses a retype into a policy outright:
/// a policy is create-only.</item>
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

/// <summary>
/// Installs the policy rule at the OWNING hub's write boundary: <see cref="PolicyCommitGuard"/>, judged
/// at the commit point, plus <see cref="PolicyPlacementDataValidator"/> for the one case the commit
/// point cannot see: a data change to an owner that has not loaded its node yet.
/// </summary>
public static class PolicyPlacementGuard
{
    /// <summary>
    /// Registers the cold-owner data validator on a node hub. The commit guard itself is registered
    /// once for the mesh (<c>AddGraph</c>), where every committing hub resolves it.
    /// </summary>
    /// <param name="config">The node hub's configuration.</param>
    /// <returns>The configuration with the guard installed.</returns>
    public static MessageHubConfiguration AddPolicyPlacementGuard(this MessageHubConfiguration config)
        => config.WithServices(services => services
            .AddScoped<IDataValidator, PolicyPlacementDataValidator>());
}

/// <summary>
/// 🚨 <b>A <c>PartitionAccessPolicy</c> is CREATE-ONLY</b> (owner decision, #7017), and this is the ONE
/// check that enforces it for every write that commits into an owner. It is judged at the COMMIT
/// point (<see cref="IEntityCommitGuard"/>), in the turn that applies the write, against the entity
/// exactly as it would commit and the entity it replaces.
///
/// <para><b>Refused:</b>
/// <list type="bullet">
/// <item>A node that the owner holds as another type and that would commit as a policy: a retype,
/// whatever route or encoding produced it. That covers a literal, a string splice, a stale-base
/// three-way merge, <c>UpdateNode</c>, the upsert, a synced write-back, an entity-reference patch,
/// and a patch queued behind another that changed the type first.</item>
/// <item>A policy committed to a hub that is not its own node's. A data change can carry another
/// node, and that node's owner would never see it.</item>
/// </list>
/// A policy that already is one commits, so a legacy policy stays editable. A first commit of the
/// hub's own node, with nothing held under its key, is the create path's, whose placement
/// <see cref="PolicyPlacementValidator"/> judges.</para>
///
/// <para>Before this, the rule was interpreted at DELIVERY: the patch text was parsed, splices were
/// applied and the owner's merge was replayed, against the node held when the patch arrived. Each
/// review round found another shape that interpretation missed, and a queued patch could still be
/// judged against a predecessor that was gone by the time it merged. At the commit point there is
/// nothing to interpret.</para>
/// </summary>
public sealed class PolicyCommitGuard : IEntityCommitGuard
{
    /// <inheritdoc />
    public string? Refusal(string collection, object? before, object after, string ownerPath,
        AccessContext? writer, JsonSerializerOptions options)
    {
        if (!string.Equals(collection, nameof(MeshNode), StringComparison.Ordinal)
            || after.As<MeshNode>(options) is not { } next
            || !PolicyPlacementValidator.IsPolicyType(next.NodeType))
            return null;

        if (!IsOwnPath(next.Path, ownerPath))
            return LocalizationCatalog.Get("policy.placement.foreignWrite", writer?.Locale, next.Path, ownerPath);

        var previous = before?.As<MeshNode>(options);
        if (previous is null || PolicyPlacementValidator.IsPolicyType(previous.NodeType))
            return null;

        return LocalizationCatalog.Get("policy.placement.createOnly", writer?.Locale, next.Path);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is the committing hub's own node. The namespace matches
    /// case-insensitively, as hub addresses do, but the TERMINAL segment ordinally: the fold reads a
    /// policy only under the exact id <c>_Policy</c>, so an identity change to <c>_policy</c> is a
    /// different node, and one the fold never reads.
    /// </summary>
    /// <param name="path">The path of the entity about to commit.</param>
    /// <param name="ownerPath">The committing hub's path.</param>
    /// <returns>True when it is the hub's own node.</returns>
    internal static bool IsOwnPath(string? path, string ownerPath)
    {
        if (path is null)
            return false;
        var cut = path.LastIndexOf('/');
        var ownerCut = ownerPath.LastIndexOf('/');
        return string.Equals(path[(cut + 1)..], ownerPath[(ownerCut + 1)..], StringComparison.Ordinal)
               && string.Equals(cut < 0 ? string.Empty : path[..cut],
                   ownerCut < 0 ? string.Empty : ownerPath[..ownerCut], StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The case <see cref="PolicyCommitGuard"/> cannot see: a <see cref="DataChangeRequest"/> carrying
/// this hub's own node, as a policy, while the hub has not loaded its node. The commit point then
/// holds nothing under the key and cannot tell a create from a retype. So the node is established
/// from storage first:
/// <list type="bullet">
/// <item>absent there, the write is a create and the creation chain judges it;</item>
/// <item>stored as another type, it is a retype and is refused, because a policy is create-only;</item>
/// <item>a read that answers nothing refuses as not loaded.</item>
/// </list>
/// Once the node is loaded, the commit guard decides and this validator stands aside.
/// </summary>
internal sealed class PolicyPlacementDataValidator(IMessageHub hub) : IDataValidator
{
    /// <inheritdoc />
    public IReadOnlyCollection<DataOperation> SupportedOperations => [DataOperation.Update, DataOperation.Create];

    /// <inheritdoc />
    public IObservable<DataValidationResult> Validate(DataValidationContext context)
    {
        var options = hub.JsonSerializerOptions;
        if (StampedFields.LiveOwnNode(hub) is not null
            || context.Entity is EntityDeltaUpdate
            || context.Entity.As<MeshNode>(options) is not { Path.Length: > 0 } incoming
            || !PolicyPlacementValidator.IsPolicyType(incoming.NodeType)
            || !string.Equals(incoming.Path, hub.Address.Path, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(DataValidationResult.Valid());

        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            return Observable.Return(DataValidationResult.Valid());
        var locale = context.AccessContext?.Locale;
        return Observable.Defer(() => storage.Read(incoming.Path, options))
            .Take(1)
            .Select(stored => (Answered: true, Stored: stored))
            .DefaultIfEmpty((Answered: false, Stored: null))
            .Select(read => !read.Answered
                ? DataValidationResult.Invalid(
                    LocalizationCatalog.Get("policy.placement.ownerNotLoaded", locale, hub.Address.Path),
                    DataValidationRejectionReason.Unknown)
                : read.Stored is { } stored && !PolicyPlacementValidator.IsPolicyType(stored.NodeType)
                    ? DataValidationResult.Invalid(
                        LocalizationCatalog.Get("policy.placement.createOnly", locale, incoming.Path),
                        DataValidationRejectionReason.ValidationFailed)
                    : DataValidationResult.Valid());
    }
}
