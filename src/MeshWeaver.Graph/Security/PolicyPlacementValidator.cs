using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

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
/// <c>Admin/Partition</c> record, or the standard layout when it has none. They are matched as
/// <see cref="PartitionDefinition.ResolveTable"/> matches them, case-insensitively. That covers
/// <c>_storage</c> as well as <c>_Storage</c>, and a custom segment as well as a standard one.</item>
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
/// <para><b>Create and every TRANSITION into the policy type.</b> A fixed path does not fix the type:
/// <c>IMeshService.UpdateNode</c> may retype a node, and so may the upsert, which runs this rule
/// through <see cref="INodeTypeTransitionValidator"/>. An update of a node that is ALREADY a policy is
/// never judged, so a legacy policy stays editable. The partition record is read as System: it is
/// platform configuration, and a caller who cannot read <c>Admin/Partition</c> must not fall back to
/// the standard layout and pass a custom segment. Not an <see cref="IOwnerEnforcedNodeValidator"/>:
/// this is placement integrity, and the caller must see the refusal before the write is issued.</para>
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

        var partition = PartitionOf(context.Node.Path);
        if (string.IsNullOrEmpty(partition))
            return Observable.Return(Judge(context, null));

        var registry = hub.ServiceProvider.GetRequiredService<PartitionRegistry>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => registry.GetPartition(partition).Take(1))
            .Select(definition => Judge(context, definition));
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
        if (!string.Equals(node.Id, PolicyId, StringComparison.Ordinal))
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

    private static PartitionDefinition Effective(PartitionDefinition? definition, string partition)
        => definition is { TableMappings: not null }
            ? definition
            : (definition ?? new PartitionDefinition { Namespace = partition }) with
            {
                TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            };

    private static bool IsPolicyType(string? nodeType)
        => string.Equals(nodeType, PartitionAccessPolicyNodeType.NodeType, StringComparison.Ordinal);

    private static string PartitionOf(string? path)
    {
        var segments = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1 ? segments[0] : string.Empty;
    }
}
