using MeshWeaver.Mesh.Security;

namespace Memex.Portal.Shared.Courses;

/// <summary>
/// Pure decision logic for the course-asset endpoint (<c>GET /assets/{Space}/{path…}</c>):
/// the URL → (space, relative path) parse, the relative-path → repo-file-path mapping
/// (through the Space's GitSync <c>Subdirectory</c>), and the entitlement gate itself.
/// No I/O, no hub — every rule is a static function so the gate semantics are unit-testable
/// in isolation (see <c>CourseAssetGateTest</c>).
/// </summary>
public static class CourseAssetGate
{
    /// <summary>The gate's verdict for one request.</summary>
    public enum Decision
    {
        /// <summary>Serve the asset (302 to the tokenized download URL).</summary>
        Allowed,

        /// <summary>The viewer is anonymous and anonymous access does not suffice → 401.</summary>
        NotAuthenticated,

        /// <summary>The viewer is authenticated but lacks Read on the Space or the required entitlement → 403.</summary>
        Forbidden,
    }

    /// <summary>
    /// Parses the catch-all route value into the Space id (first segment) and the asset's
    /// path relative to the Space's synced content (the remaining segments). Rejects
    /// paths without at least a space + one file segment, empty segments (<c>//</c>),
    /// dot segments (<c>.</c> / <c>..</c> — path traversal), segments containing
    /// whitespace (the space segment is interpolated into mesh query strings — embedded
    /// whitespace would break tokenization / allow query injection), and a
    /// satellite-shaped (<c>_</c>-prefixed) space segment.
    /// </summary>
    /// <param name="path">The raw <c>{**path}</c> route value (already URL-decoded).</param>
    /// <param name="space">The Space id (first path segment).</param>
    /// <param name="relativePath">The asset path relative to the Space's synced subtree.</param>
    /// <returns>True when the path has the expected <c>{Space}/{rest…}</c> shape.</returns>
    public static bool TryParsePath(string? path, out string space, out string relativePath)
    {
        space = string.Empty;
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var segments = path.Trim('/').Split('/');
        if (segments.Length < 2)
            return false;
        if (segments.Any(s => s.Length == 0 || s.Any(char.IsWhiteSpace) || s == "." || s == ".."))
            return false;
        if (segments[0].StartsWith('_'))
            return false;

        space = segments[0];
        relativePath = string.Join('/', segments.Skip(1));
        return true;
    }

    /// <summary>
    /// Maps the asset's Space-relative path to the file path inside the synced repository:
    /// the Space's GitSync <c>Subdirectory</c> (when configured) prefixes the relative path,
    /// mirroring how the sync itself lays the Space's content out in the repo.
    /// </summary>
    /// <param name="subdirectory">The <c>GitHubSyncConfig.Subdirectory</c> (null/empty = repository root).</param>
    /// <param name="relativePath">The asset path relative to the Space's synced subtree.</param>
    /// <returns>The repository file path to request from the GitHub contents API.</returns>
    public static string MapToRepoPath(string? subdirectory, string relativePath)
    {
        var prefix = subdirectory?.Trim().Trim('/');
        return string.IsNullOrEmpty(prefix) ? relativePath : $"{prefix}/{relativePath}";
    }

    /// <summary>
    /// The entitlement gate. Course admins (<see cref="Permission.Update"/> on the Space)
    /// always pass. Everyone else needs <see cref="Permission.Read"/> on the Space AND —
    /// when the course is paid (its <c>{Space}/_Entitlements</c> container has entries) —
    /// an entitlement of their own. Denials split by authentication so the endpoint can
    /// answer 401 (log in) vs 403 (no access).
    /// </summary>
    /// <param name="spacePermissions">The viewer's effective permissions on the Space node.</param>
    /// <param name="isAuthenticated">True when the viewer is a real signed-in user (not anonymous/virtual).</param>
    /// <param name="isPaid">True when the Space's <c>_Entitlements</c> container holds any entries.</param>
    /// <param name="isEntitled">True when an entitlement node exists for this viewer.</param>
    /// <returns>The verdict for this request.</returns>
    public static Decision Decide(
        Permission spacePermissions,
        bool isAuthenticated,
        bool isPaid,
        bool isEntitled)
    {
        // Course admins always pass — they manage the content the gate protects.
        if (spacePermissions.HasFlag(Permission.Update))
            return Decision.Allowed;

        if (!spacePermissions.HasFlag(Permission.Read))
            return isAuthenticated ? Decision.Forbidden : Decision.NotAuthenticated;

        if (isPaid && !isEntitled)
            return isAuthenticated ? Decision.Forbidden : Decision.NotAuthenticated;

        return Decision.Allowed;
    }
}
