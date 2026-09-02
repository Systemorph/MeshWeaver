using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// The AUTHORING gate for <see cref="NodeTypeDefinition.InstanceLocations"/> (#3039): refuses a
/// Create/Update that declares where a type's instances live when that type is one the permission
/// fold enumerates MESH-WIDE — <see cref="NeverNarrowedNodeTypes"/>: <c>Role</c>,
/// <c>GroupMembership</c>, <c>AccessAssignment</c>, <c>PartitionAccessPolicy</c>, and every
/// type-declared gate on this mesh.
///
/// <para><b>Why refuse at authoring when the planner already refuses at query time.</b> The storage
/// planner (MeshWeaver.Plugins, <c>PostgreSqlPartitionedMeshQuery</c>) consults the SAME set and fans
/// out in full for these types whatever a declaration says, so a declaration on one of them can only
/// ever be INERT — and an inert declaration is a lie in the data: the next reader trusts it, the one
/// after that "fixes" the planner to honour it, and the fold is narrowed. In that fold "no result"
/// and "not allowed" are the same value: a short read makes a group-derived grant vanish (#2011) and
/// makes a group-scoped deny fail OPEN, with nothing logged and nothing failing
/// (<c>Doc/Architecture/UnanchoredSecurityReads</c>). Refusing the write turns that mistake into a
/// red PR (a node-repo import fails) instead of a silent declaration.</para>
///
/// <para>Runs on Create and Update — the two surfaces that persist a declaration — and judges only
/// the declaration's own type name (its path, or its id for a root built-in), the two names an
/// instance can carry in <c>nodeType:</c>. A fold type declaring NOTHING stays legal: the gate is
/// on the declaration, not the type. Static (in-process) declarations have no write boundary; the
/// static fold in <see cref="NodeTypeInstanceLocations.FromStaticNodes"/> applies
/// <see cref="Refusal"/> to those and throws at startup.</para>
/// </summary>
public sealed class InstanceLocationDeclarationValidator(IMessageHub hub, MeshConfiguration configuration)
    : INodeValidator
{
    /// <summary>Create + Update — the two surfaces that persist a NodeType declaration.</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Create, NodeOperation.Update];

    /// <summary>
    /// Rejects a create/update whose content is a <see cref="NodeTypeDefinition"/> declaring
    /// <see cref="NodeTypeDefinition.InstanceLocations"/> for a never-narrowed type.
    /// </summary>
    /// <param name="context">The validation context describing the node and operation.</param>
    /// <returns>An observable emitting the validation result.</returns>
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        var node = context.Node;
        if (!ImportWriteOrder.IsNodeTypeDefinition(node)
            && !NodeTypeDeclarationSelfTypingValidator.IsNodeTypeDeclarationContent(node.Content))
            return Observable.Return(NodeValidationResult.Valid());

        // ContentAs, never `is NodeTypeDefinition`: a cross-hub write arrives as the JSON it was
        // written as, and a pattern match would silently wave the declaration through.
        var locations = node.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.InstanceLocations;
        var refusal = Refusal(node, locations, NeverNarrowedNodeTypes.GatedNodeTypesOf(configuration));
        return Observable.Return(refusal is null
            ? NodeValidationResult.Valid()
            : NodeValidationResult.Invalid(refusal, NodeRejectionReason.ValidationFailed));
    }

    /// <summary>
    /// THE gate predicate, shared by the write boundary (this validator) and the static fold
    /// (<see cref="NodeTypeInstanceLocations.FromStaticNodes"/>) so the two cannot drift: the reason
    /// <paramref name="declaration"/> may not carry <paramref name="locations"/>, or null when it may.
    /// </summary>
    /// <param name="declaration">The NodeType definition node.</param>
    /// <param name="locations">Its declared instance locations.</param>
    /// <param name="gatedNodeTypes">The mesh's type-declared gates, or null when none.</param>
    /// <returns>The refusal, naming the type and why the fold may never be narrowed; null if legal.</returns>
    public static string? Refusal(
        MeshNode declaration,
        IReadOnlyList<string>? locations,
        IReadOnlySet<string>? gatedNodeTypes)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (locations is not { Count: > 0 })
            return null;

        var name = NeverNarrowedNodeTypes.Refuses(declaration.Path, gatedNodeTypes) ? declaration.Path
            : NeverNarrowedNodeTypes.Refuses(declaration.Id, gatedNodeTypes) ? declaration.Id
            : null;
        if (name is null)
            return null;

        var kind = NeverNarrowedNodeTypes.Names.Contains(name)
            ? "a type the permission fold reads mesh-wide"
            : "a type-declared gate (ConfigureNodeTypeAccess), which the permission fold enumerates mesh-wide";
        return $"'{declaration.Path}' declares instanceLocations for '{name}' — {kind}. In that fold " +
               "\"no result\" and \"not allowed\" are the same value: a narrowed read makes a group-derived " +
               "grant vanish (#2011) and makes a group-scoped deny fail OPEN, so these types are never " +
               "narrowed whatever a declaration says (NeverNarrowedNodeTypes) and the declaration could " +
               "only ever be inert. Remove instanceLocations from this declaration. See " +
               "Doc/Architecture/UnanchoredSecurityReads.";
    }
}
