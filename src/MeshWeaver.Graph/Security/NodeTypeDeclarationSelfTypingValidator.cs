using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Fail-closed guard for Create/Update: refuses a write that DECLARES a NodeType — its
/// <see cref="MeshNode.Content"/> IS a <see cref="Configuration.NodeTypeDefinition"/> — while also
/// naming ITSELF via <see cref="MeshNode.NodeType"/>, i.e. enrolling the declaration in its own
/// instance query.
///
/// <para><b>The defect this closes at the WRITER (#2160/#2161/#2162, #2245, #2358).</b> A NodeType
/// declaration is the MeshNode that DEFINES a type. Three built-in declarations (<c>User</c>,
/// <c>VUser</c>, <c>Partition</c>) shipped with <see cref="MeshNode.NodeType"/> set to their OWN
/// name — i.e. each claimed to also BE an instance of the type it defines. That makes a declaration
/// indistinguishable from a real instance to every <c>nodeType:&lt;Type&gt;</c> query in the mesh:
/// the portal's user directory (<c>UserIdentityCache</c>) returned the <c>User</c> declaration
/// alongside real accounts and logged <c>As&lt;User&gt; for User: value is NodeTypeDefinition</c> on
/// every index snapshot — 355k+ occurrences in production. #2245 retyped the three known offenders
/// (<c>NodeType = MeshNode.NodeTypePath</c>, exactly as <c>Space</c>/<c>Release</c>/<c>Build</c>
/// already did) and added a STATIC ratchet test over every built-in declaration
/// (<c>NodeTypeDeclarationSelfTypingTest</c>) — but nothing stopped a FUTURE write (a repair path, a
/// plugin-installed NodeType, a hand-authored patch) from reintroducing the exact same collision at
/// RUNTIME, which a static-registration-only ratchet cannot see. #2358 reported the identical
/// signature from a different host category, which is exactly the shape of recurrence a
/// retroactive, instance-by-instance fix cannot close. This validator is the "ONE change that fixes
/// the class of bug rather than an instance of it" #2358 itself asks for: refuse the collision AT
/// THE WRITE BOUNDARY, for every declaration present or future, not just the three named so far.</para>
///
/// <para>🚨 <b>SELF-typing only — a declaration naming an UNRELATED type stays legal, and that is
/// deliberate.</b> The guard first refused any <c>NodeType</c> other than <c>NodeType</c>, and that
/// broke a shape the plugins repo actually ships: a package ROOT that is a <c>Space</c> and whose
/// content also happens to be a <c>NodeTypeDefinition</c> (the UWDeepfield shape, pinned by
/// <c>NodeRepoInstanceOrderingTest</c>). Refusing it made the whole package un-installable — a
/// strictly worse outcome than the degradation it was guarding against, and one CI caught before
/// merge. The harm this guard exists to stop is a declaration polluting the instance query for the
/// type IT DECLARES (<c>User</c> declaration answering <c>nodeType:User</c> beside real accounts);
/// a <c>Space</c> root answering <c>nodeType:Space</c> is simply a Space, correctly returned.</para>
///
/// <para>The residual is real but SMALLER and is NOT closed here: a <c>nodeType:Space</c> consumer
/// that calls <c>ContentAs&lt;Space&gt;()</c> on such a root gets a degraded null, because the
/// content is a <c>NodeTypeDefinition</c>. Closing that means changing the shipped package first
/// (a cross-repo change in MeshWeaver.Plugins), not refusing the write here — refusing it at the
/// boundary while the content still ships is how a guard breaks production rather than protecting
/// it.</para>
///
/// <para>Bad-data TOLERANCE is unchanged: this guard never rejects a READ. An existing row that
/// already carries the collision keeps loading (degraded, tolerated by
/// <see cref="ObjectAsExtensions.As{T}"/>); only a NEW create/update that would (re)introduce the
/// collision is refused. Rows PERSISTED with the collision before this guard existed are healed at
/// startup by <see cref="SelfTypedDeclarationDurableRepair"/> (#2425/#2506) using
/// <see cref="IsSelfTypedDeclaration"/> — the shared predicate that keeps the two halves exact
/// mirrors.</para>
/// </summary>
public sealed class NodeTypeDeclarationSelfTypingValidator : INodeValidator
{
    /// <summary>Create + Update — the two surfaces that persist a node's own NodeType field.</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Create, NodeOperation.Update];

    /// <summary>
    /// Rejects a create/update whose content declares a NodeType while its own
    /// <see cref="MeshNode.NodeType"/> names an instance type — unset or
    /// <see cref="MeshNode.NodeTypePath"/> are both legal for a declaration.
    /// </summary>
    /// <param name="context">The validation context describing the node and operation.</param>
    /// <returns>An observable emitting the validation result.</returns>
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        var node = context.Node;

        if (!IsSelfTypedDeclaration(node))
            return Observable.Return(NodeValidationResult.Valid());

        return Observable.Return(NodeValidationResult.Invalid(
            $"'{node.Path}' carries NodeTypeDefinition content (it DECLARES a NodeType) and its own " +
            $"NodeType field is '{node.NodeType}' — i.e. the declaration claims to be an INSTANCE OF " +
            "ITSELF. That makes it indistinguishable from a real instance to every " +
            $"'nodeType:{node.NodeType}' query in the mesh. Set NodeType to " +
            $"'{MeshNode.NodeTypePath}', or leave it unset.",
            NodeRejectionReason.ValidationFailed));
    }

    /// <summary>
    /// THE collision predicate, shared by the two halves of the fix: the write-boundary guard
    /// (this validator, #2378) and the startup repair of rows persisted BEFORE the guard existed
    /// (<see cref="SelfTypedDeclarationDurableRepair"/>, #2425/#2506). One definition, so what the
    /// guard refuses and what the repair heals can never drift apart.
    ///
    /// <para>True when the node DECLARES a NodeType — its content is a
    /// <see cref="Configuration.NodeTypeDefinition"/> — while its own
    /// <see cref="MeshNode.NodeType"/> names ITSELF (its <see cref="MeshNode.Path"/> or
    /// <see cref="MeshNode.Id"/>), i.e. the declaration is enrolled in its own instance query.
    /// SELF-typing only: an instance references a type by the declaration's path
    /// (<c>nodeType:"Pack/Widget"</c>) or, for a built-in at the root, by its id
    /// (<c>nodeType:"User"</c>) — those two are the ways a declaration can collide with its own
    /// instances. A declaration naming an UNRELATED type is a different, legal shape (see the
    /// class doc: the UWDeepfield package root).</para>
    /// </summary>
    /// <param name="node">The node to classify.</param>
    /// <returns><see langword="true"/> when the node is a declaration claiming to be an instance
    /// of the type it declares.</returns>
    public static bool IsSelfTypedDeclaration(MeshNode node)
    {
        // Nothing to judge unless the NodeType field claims to be an INSTANCE of something.
        if (string.IsNullOrEmpty(node.NodeType)
            || string.Equals(node.NodeType, MeshNode.NodeTypePath, StringComparison.Ordinal))
            return false;

        if (!IsNodeTypeDeclarationContent(node.Content))
            return false;

        return string.Equals(node.NodeType, node.Path, StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.NodeType, node.Id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="content"/> is a <see cref="Configuration.NodeTypeDefinition"/> —
    /// already typed (in-process content), or degraded to the raw JSON a cross-hub write can arrive
    /// as (the same discriminator shape <c>ContentDiscriminatorValidator</c> reads for its own
    /// <c>$type</c> probe). Public because it is also the CANDIDATE test of
    /// <see cref="SelfTypedDeclarationDurableRepair"/>: what counts as "declares a NodeType" must
    /// be one definition for the guard, the repair's predicate and the repair's candidate set —
    /// a bare <c>is NodeTypeDefinition</c> on a candidate would silently drop a declaration whose
    /// content arrived untyped (AGENTS.md: never cast an object payload).
    /// </summary>
    /// <param name="content">The node content to classify.</param>
    /// <returns><see langword="true"/> when the content declares a NodeType.</returns>
    public static bool IsNodeTypeDeclarationContent(object? content) => content switch
    {
        Configuration.NodeTypeDefinition => true,
        JsonElement je when je.ValueKind == JsonValueKind.Object
            && je.TryGetProperty("$type", out var discriminator)
            && discriminator.ValueKind == JsonValueKind.String
            && IsNodeTypeDefinitionDiscriminator(discriminator.GetString()) => true,
        _ => false,
    };

    /// <summary>
    /// True when <paramref name="discriminator"/> names <see cref="Configuration.NodeTypeDefinition"/>
    /// — as a bare short name, a namespace-qualified name, OR the full CLR
    /// <c>AssemblyQualifiedName</c> shape (<c>"Namespace.Type, AssemblyName, Version=…"</c>).
    /// Hand-authored JSON (including MCP writes) is not guaranteed to use the framework's own
    /// "Namespace.Type" convention — an assembly-qualified <c>$type</c> that the framework's own
    /// writers never produce would otherwise fail the <c>EndsWith</c> check (it ends with
    /// <c>", AssemblyName"</c>, not <c>".NodeTypeDefinition"</c>) and let the guard fail OPEN.
    /// </summary>
    private static bool IsNodeTypeDefinitionDiscriminator(string? discriminator)
    {
        if (string.IsNullOrEmpty(discriminator))
            return false;

        // Strip a trailing assembly qualifier ("…, AssemblyName, Version=…, Culture=…, …") before
        // comparing the type-name portion — comma-separated, never present in the bare/namespaced
        // shapes this compares against.
        var typeName = discriminator.Split(',')[0].Trim();

        return string.Equals(typeName, nameof(Configuration.NodeTypeDefinition), StringComparison.Ordinal)
            || typeName.EndsWith("." + nameof(Configuration.NodeTypeDefinition), StringComparison.Ordinal);
    }
}
