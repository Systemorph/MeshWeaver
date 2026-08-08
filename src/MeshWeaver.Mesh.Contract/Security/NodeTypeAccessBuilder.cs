namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Builder for configuring node type access permissions.
/// Used via <c>builder.ConfigureNodeTypeAccess(access => access.WithPublicRead("User"))</c>.
/// </summary>
public class NodeTypeAccessBuilder
{
    private readonly HashSet<string> _publicReadTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NodeTypeGate> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Marks a node type as publicly readable by all authenticated users.
    /// </summary>
    public NodeTypeAccessBuilder WithPublicRead(string nodeType)
    {
        _publicReadTypes.Add(nodeType);
        return this;
    }

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
    /// Gets all node type permissions configured via this builder.
    /// </summary>
    public IReadOnlyList<NodeTypePermission> Build()
        => _publicReadTypes.Select(t => new NodeTypePermission(t, PublicRead: true)).ToList();

    /// <summary>
    /// Gets the type-declared subtree gates configured via <see cref="WithGate(NodeTypeGate)"/>.
    /// Empty by default — a mesh that declares none pays nothing and behaves exactly as before.
    /// </summary>
    public IReadOnlyList<NodeTypeGate> BuildGates() => _gates.Values.ToList();
}
