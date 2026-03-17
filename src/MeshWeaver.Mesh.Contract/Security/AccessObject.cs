namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Base content type for access-controlled mesh nodes (User, Group).
/// Id, Name, and Icon live on MeshNode — not duplicated here.
/// </summary>
public record AccessObject
{
    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>True for anonymous/cookie-tracked virtual users.</summary>
    public bool IsVirtual { get; init; }
}

