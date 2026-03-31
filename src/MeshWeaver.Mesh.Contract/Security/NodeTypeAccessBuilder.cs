namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Builder for configuring node type access permissions.
/// Used via <c>builder.ConfigureNodeTypeAccess(access => access.WithPublicRead("User"))</c>.
/// </summary>
public class NodeTypeAccessBuilder
{
    private readonly HashSet<string> _publicReadTypes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Marks a node type as publicly readable by all authenticated users.
    /// </summary>
    public NodeTypeAccessBuilder WithPublicRead(string nodeType)
    {
        _publicReadTypes.Add(nodeType);
        return this;
    }

    /// <summary>
    /// Gets all node type permissions configured via this builder.
    /// </summary>
    public IReadOnlyList<NodeTypePermission> Build()
        => _publicReadTypes.Select(t => new NodeTypePermission(t, PublicRead: true)).ToList();
}
