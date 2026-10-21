using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Refuses a <c>PartitionAccessPolicy</c> written where the permission fold can never read it, so a
/// restriction that would not apply fails at the write instead of being stored and ignored.
///
/// <para><b>Where the fold reads policies.</b> The fold reads two places: the root node at
/// <c>_Policy</c>, and every node with id <c>_Policy</c> in the target's partition
/// (<c>path:{partition} scope:descendants id:_Policy nodeType:PartitionAccessPolicy</c>). That
/// partition read is a CONTENT read, so it never returns a row stored in a satellite table.
/// <list type="bullet">
/// <item>On Postgres, a node under a configured satellite segment (<c>_Storage</c>, <c>_Thread</c>,
/// <c>_Activity</c>, …) is stored in that segment's table, not in <c>mesh_nodes</c>.</item>
/// <item>The in-memory adapter mirrors this by dropping satellite-path rows from a query that does not
/// target satellites.</item>
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
/// <para>Runs on Create only: a node's path is fixed once it exists. A move's copy leg and an
/// import's create branch go through the same creation chain. Not an
/// <see cref="IOwnerEnforcedNodeValidator"/>: this is placement integrity, and the caller must see
/// the refusal before the write is issued.</para>
/// </summary>
public sealed class PolicyPlacementValidator : INodeValidator
{
    /// <summary>The only id the permission fold reads a policy under.</summary>
    public const string PolicyId = "_Policy";

    /// <summary>Create — the one operation that places a node at a path.</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Create];

    /// <summary>Validates where a <c>PartitionAccessPolicy</c> node is placed.</summary>
    /// <param name="context">The validation context describing the node and operation.</param>
    /// <returns>An observable emitting the one validation result.</returns>
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        => Observable.Return(Judge(context));

    /// <summary>The verdict, synchronously — the same rule <see cref="Validate"/> emits.</summary>
    /// <param name="context">The validation context describing the node and operation.</param>
    /// <returns>The validation result.</returns>
    public static NodeValidationResult Judge(NodeValidationContext context)
    {
        var node = context.Node;
        if (context.Operation != NodeOperation.Create
            || !string.Equals(node.NodeType, PartitionAccessPolicyNodeType.NodeType, StringComparison.Ordinal))
            return NodeValidationResult.Valid();

        if (!string.Equals(node.Id, PolicyId, StringComparison.Ordinal))
            return NodeValidationResult.Invalid(
                $"A PartitionAccessPolicy at '{node.Path}' would never take effect: the permission fold reads a "
                + $"policy only under the id '{PolicyId}'. Write it as '{{scope}}/{PolicyId}'.");

        if (SatelliteTableMapping.IsSatellitePath(node.Path))
        {
            var owner = SatelliteTableMapping.OwnerOfSatellitePath(node.Path);
            return NodeValidationResult.Invalid(
                $"A PartitionAccessPolicy at '{node.Path}' would never take effect: its scope '{node.Namespace}' lies "
                + "under a satellite segment, whose rows the permission fold does not read. A satellite's access "
                + $"follows its main node, so place the policy at '{(string.IsNullOrEmpty(owner) ? "" : owner + "/")}{PolicyId}'.");
        }

        return NodeValidationResult.Valid();
    }
}
