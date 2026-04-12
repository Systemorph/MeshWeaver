namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Well-known user IDs for access control.
/// </summary>
public static class WellKnownUsers
{
    /// <summary>
    /// The "Anonymous" user represents unauthenticated/virtual access.
    /// Assign roles to "Anonymous" to grant permissions to visitors who haven't logged in.
    /// </summary>
    public const string Anonymous = "Anonymous";

    /// <summary>
    /// The "Public" user represents the baseline permissions for all authenticated users.
    /// Every logged-in user inherits "Public" permissions in addition to their own.
    /// </summary>
    public const string Public = "Public";

    /// <summary>
    /// The "system-security" identity used by SecurityService for internal operations.
    /// Bypasses RLS validation — security operations (creating/updating AccessAssignment,
    /// PartitionAccessPolicy nodes) must not be blocked by the permissions they manage.
    /// </summary>
    public const string System = "system-security";
}
