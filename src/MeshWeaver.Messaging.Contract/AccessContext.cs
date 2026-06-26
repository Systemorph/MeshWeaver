namespace MeshWeaver.Messaging;

/// <summary>
/// Identity and authorization context of the caller on whose behalf a message is
/// processed. Carried on every <see cref="IMessageDelivery"/> and used by the
/// permission evaluator to authorize reads and writes.
/// </summary>
public record AccessContext
{
    /// <summary>
    /// Stable unique identifier of the principal (e.g. the Entra object id, or a
    /// hub's mesh address when <see cref="IsHub"/> is set). Empty when anonymous.
    /// </summary>
    public string ObjectId { get; init; } = string.Empty;
    /// <summary>
    /// Human-readable display name of the principal.
    /// </summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>
    /// Email address of the principal, when available.
    /// </summary>
    public string Email { get; init; } = string.Empty;
    /// <summary>
    /// The roles assigned to the principal, folded into the effective permissions.
    /// </summary>
    public IReadOnlyCollection<string> Roles { get; init; } = [];
    /// <summary>
    /// True when this context represents a virtual (non-interactive) user rather
    /// than a real signed-in person.
    /// </summary>
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
