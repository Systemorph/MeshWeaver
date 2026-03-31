namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Lightweight index node stored at ApiToken/{hashPrefix}.
/// Points to the full ApiToken node under the user's namespace (User/{userId}/ApiToken/{hashPrefix}).
/// Used for O(1) token validation without knowing the userId upfront.
/// </summary>
public record ApiTokenIndex
{
    /// <summary>SHA-256 hex hash of the raw token (for validation).</summary>
    public string TokenHash { get; init; } = "";

    /// <summary>Full path to the ApiToken node (e.g., User/alice/ApiToken/abc123def456).</summary>
    public string TokenPath { get; init; } = "";
}
