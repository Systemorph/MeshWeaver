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

    /// <summary>
    /// When true, this context was established via an API token (Bearer authentication).
    /// The Api permission flag is required for operations in this context.
    /// </summary>
    public bool IsApiToken { get; init; }

    /// <summary>
    /// When true, this context is a HUB credential: <see cref="ObjectId"/> is the hub's own
    /// mesh address (set by <c>ImpersonateAsHub</c>), not a user/group identity. A hub
    /// initializes and syncs its own EntityStore under this credential and a sub-hub subscribes
    /// to its parent/owner under it — so the permission evaluator grants a hub-credential
    /// <c>Read</c> on its OWN path and its ANCESTOR scopes (the sync direction), and nothing
    /// else. This is what lets a sub-hub read its parent hub without a per-hub-address
    /// <c>AccessAssignment</c> (which never exists). See AccessControl.md.
    /// </summary>
    public bool IsHub { get; init; }
}
