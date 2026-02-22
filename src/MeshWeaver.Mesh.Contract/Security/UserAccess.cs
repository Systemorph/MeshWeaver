namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Well-known user IDs for access control.
/// </summary>
public static class WellKnownUsers
{
    /// <summary>
    /// The "Public" user represents anonymous/unauthenticated access.
    /// Assign roles to "Public" to grant permissions to anonymous users.
    /// </summary>
    public const string Public = "Public";
}
