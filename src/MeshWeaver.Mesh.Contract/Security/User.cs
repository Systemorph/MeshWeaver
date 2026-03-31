namespace MeshWeaver.Mesh.Security;

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
