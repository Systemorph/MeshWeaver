namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Service for getting the current navigation path and namespace context.
/// </summary>
public interface INavigationContextService
{
    /// <summary>
    /// Gets the current relative path from navigation.
    /// </summary>
    string? CurrentPath { get; }

    /// <summary>
    /// Gets the current namespace (resolved Address path).
    /// Used as the default namespace for queries when none is specified.
    /// </summary>
    string? CurrentNamespace { get; }

    /// <summary>
    /// Sets the current namespace from the resolved Address.
    /// Called by page components after path resolution.
    /// </summary>
    /// <param name="namespace">The resolved namespace (e.g., "Systemorph/Marketing")</param>
    void SetCurrentNamespace(string? @namespace);

    /// <summary>
    /// Navigates to the specified URI.
    /// </summary>
    /// <param name="uri"></param>
    /// <param name="forceLoad"></param>
    void NavigateTo(string uri, bool forceLoad = false);
}
