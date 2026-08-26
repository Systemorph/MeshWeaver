using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Fail-closed guard for Create/Update: refuses a write that DECLARES a NodeType — its
/// <see cref="MeshNode.Content"/> IS a <see cref="Configuration.NodeTypeDefinition"/> — while also
/// naming itself an INSTANCE of a type via <see cref="MeshNode.NodeType"/>.
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
/// <para>Bad-data TOLERANCE is unchanged: this guard never rejects a READ. An existing row that
/// already carries the collision keeps loading (degraded, tolerated by
/// <see cref="ObjectAsExtensions.As{T}"/>) until it is next written; only a NEW create/update that
/// would (re)introduce the collision is refused.</para>
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

        // Nothing to judge unless the NodeType field claims to be an INSTANCE of something.
        if (string.IsNullOrEmpty(node.NodeType)
            || string.Equals(node.NodeType, MeshNode.NodeTypePath, StringComparison.Ordinal))
            return Observable.Return(NodeValidationResult.Valid());

        if (!IsNodeTypeDeclarationContent(node.Content))
            return Observable.Return(NodeValidationResult.Valid());

        return Observable.Return(NodeValidationResult.Invalid(
            $"'{node.Path}' carries NodeTypeDefinition content (it DECLARES a NodeType) but its own " +
            $"NodeType field is '{node.NodeType}' — a declaration must never claim to be an instance " +
            "of the type it declares, or of anything else: doing so makes it indistinguishable from " +
            $"a real instance to every 'nodeType:{node.NodeType}' query in the mesh. Set NodeType to " +
            $"'{MeshNode.NodeTypePath}', or leave it unset.",
            NodeRejectionReason.ValidationFailed));
    }

    /// <summary>
    /// True when <paramref name="content"/> is a <see cref="Configuration.NodeTypeDefinition"/> —
    /// already typed (in-process content), or degraded to the raw JSON a cross-hub write can arrive
    /// as (the same discriminator shape <c>ContentDiscriminatorValidator</c> reads for its own
    /// <c>$type</c> probe).
    /// </summary>
    private static bool IsNodeTypeDeclarationContent(object? content) => content switch
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
