namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Builder for configuring node type access declarations.
/// Used via <c>builder.ConfigureNodeTypeAccess(access =&gt; access.WithGate("Store/Plugin", …))</c>.
///
/// <para>🔒 #953 — this builder used to carry a <c>WithPublicRead(nodeType)</c> declaration that
/// projected into the per-schema <c>node_type_permissions</c> table. It was removed, not wired up:
/// nothing in the product ever wrote that table, so the matching RLS term was a constant
/// <c>false</c>, and the ~24 types that declared it — <c>Thread</c>, <c>ThreadMessage</c>,
/// <c>Markdown</c>, <c>Code</c>, <c>Document</c> and the whole <c>Course</c>/<c>Module</c>/
/// <c>Exercise</c>/<c>ExerciseAttempt</c> set — would have skipped the node-level check inside any
/// partition the viewer could already reach, overriding the DENY rows that paywall gating relies
/// on. There is also no node-type-keyed term in <see cref="PermissionEvaluator"/>, so the SQL and
/// evaluator read paths would have diverged.</para>
///
/// <para>To make content publicly readable, use a <c>PartitionAccessPolicy</c> <c>_Policy</c> node
/// with <c>PublicRead = true</c> (issue #603 — projected into <c>user_effective_permissions</c> as
/// allow-Read rows for <c>Public</c>/<c>Anonymous</c> that PARTICIPATE in the longest-prefix fold,
/// so a deeper deny still wins), or <see cref="NodeTypeGate"/> (issue #701) for a type that opens a
/// short, explicitly listed set of surfaces on its own subtree.</para>
/// </summary>
public class NodeTypeAccessBuilder
{
    private readonly Dictionary<string, NodeTypeGate> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Declares that every node of <paramref name="nodeType"/> gates its own subtree, opening
    /// exactly <paramref name="publicSurfaces"/> to everyone — anonymous visitors included — and
    /// nothing else. Use <see cref="NodeTypeGate.Self"/> (the empty string) for the gated node
    /// itself (the cover); any other entry is a path relative to it, subtree included.
    ///
    /// <para>Last declaration per node type wins, so a deployment can re-declare a type's gate
    /// without the two accumulating into a union nobody wrote.</para>
    /// </summary>
    public NodeTypeAccessBuilder WithGate(string nodeType, params string[] publicSurfaces)
        => WithGate(new NodeTypeGate(nodeType) { PublicSurfaces = publicSurfaces });

    /// <summary>
    /// Declares a full <see cref="NodeTypeGate"/> — public surfaces plus the type-declared
    /// <see cref="NodeTypeGate.RedirectOnDenied"/> target. Last declaration per node type wins.
    /// </summary>
    public NodeTypeAccessBuilder WithGate(NodeTypeGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (!string.IsNullOrWhiteSpace(gate.NodeType))
            _gates[gate.NodeType] = gate;
        return this;
    }

    /// <summary>
    /// Gets the type-declared subtree gates configured via <see cref="WithGate(NodeTypeGate)"/>.
    /// Empty by default — a mesh that declares none pays nothing and behaves exactly as before.
    /// </summary>
    public IReadOnlyList<NodeTypeGate> BuildGates() => _gates.Values.ToList();
}
