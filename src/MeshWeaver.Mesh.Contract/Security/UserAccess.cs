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
}
