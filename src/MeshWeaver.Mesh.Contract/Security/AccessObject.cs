namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Base content type for User and Group mesh nodes.
/// Instances can be created anywhere in the node hierarchy.
/// </summary>
public record AccessObject
{
    /// <summary>Unique identifier.</summary>
    public string Id { get; init; } = "";

    /// <summary>Display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Optional icon name or URL.</summary>
    public string? Icon { get; init; }

    /// <summary>True for anonymous/cookie-tracked virtual users.</summary>
    public bool IsVirtual { get; init; }
}
