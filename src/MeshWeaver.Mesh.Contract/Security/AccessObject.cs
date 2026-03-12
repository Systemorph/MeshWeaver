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

/// <summary>
/// Content type for User nodes. Properties match the onboarding flow.
/// Avatar/Image is stored as MeshNode.Icon.
/// </summary>
public record User : AccessObject
{
    /// <summary>Full name from OAuth claims (e.g. "John Doe").</summary>
    public string? FullName { get; init; }

    /// <summary>Email address (from OAuth, set during onboarding).</summary>
    public string? Email { get; init; }

    /// <summary>Short biography.</summary>
    public string? Bio { get; init; }

    /// <summary>Profile role (e.g. Developer, Manager, Designer).</summary>
    public string? Role { get; init; }
}
