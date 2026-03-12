namespace MeshWeaver.Messaging;

public record AccessContext
{
    public string ObjectId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public IReadOnlyCollection<string> Roles { get; init; } = [];
    public bool IsVirtual { get; init; }

    /// <summary>
    /// When set, indicates that this context is impersonated by another identity
    /// (e.g., a portal hub acting on behalf of a virtual user).
    /// The impersonator's identity is used for authorization when the
    /// impersonated user's own permissions are insufficient.
    /// </summary>
    public string? ImpersonatedBy { get; init; }
}
