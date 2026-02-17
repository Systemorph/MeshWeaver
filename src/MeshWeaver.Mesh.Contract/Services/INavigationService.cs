using System.Web;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Options for navigation.
/// </summary>
/// <param name="Uri">The URI to navigate to.</param>
public record NavigationOptions(string Uri)
{
    /// <summary>
    /// If true, forces a full page load.
    /// </summary>
    public bool ForceLoad { get; init; }

    /// <summary>
    /// If true, replaces the current history entry instead of adding a new one.
    /// </summary>
    public bool Replace { get; init; }
}

/// <summary>
/// Service for navigation and getting the current navigation path and namespace context.
/// Automatically subscribes to location changes and manages path resolution and creatable types.
/// </summary>
public interface INavigationService : IDisposable
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
    /// Gets the current navigation context containing resolved path information.
    /// Null if the path could not be resolved.
    /// </summary>
    NavigationContext? Context { get; }

    /// <summary>
    /// Event raised when the navigation context changes due to location change.
    /// </summary>
    event Action<NavigationContext?>? OnNavigationContextChanged;

    /// <summary>
    /// Observable that emits the current creatable types snapshot for the current node path.
    /// Automatically reloaded when the node path changes.
    /// Emits incrementally as types are loaded. <see cref="CreatableTypesSnapshot.IsLoading"/>
    /// indicates whether more items may still arrive.
    /// </summary>
    IObservable<CreatableTypesSnapshot> CreatableTypes { get; }

    /// <summary>
    /// Triggers a background reload of creatable types for the current namespace
    /// if they haven't been loaded yet. Results arrive through <see cref="CreatableTypes"/>.
    /// </summary>
    void RefreshCreatableTypes();

    /// <summary>
    /// Initializes the service and subscribes to NavigationManager.LocationChanged.
    /// Should be called once during application startup or component initialization.
    /// Multiple calls are idempotent.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Sets the current namespace from the resolved Address.
    /// Called by page components after path resolution.
    /// </summary>
    /// <param name="namespace">The resolved namespace (e.g., "Systemorph/Marketing")</param>
    void SetCurrentNamespace(string? @namespace);

    /// <summary>
    /// Navigates to the specified URI.
    /// </summary>
    /// <param name="uri">The URI to navigate to.</param>
    /// <param name="forceLoad">If true, forces a full page load.</param>
    /// <param name="replace">If true, replaces the current history entry instead of adding a new one.</param>
    void NavigateTo(string uri, bool forceLoad = false, bool replace = false)
        => NavigateTo(new NavigationOptions(uri) { ForceLoad = forceLoad, Replace = replace });

    /// <summary>
    /// Navigates using the specified navigation options.
    /// </summary>
    /// <param name="options">The navigation options.</param>
    void NavigateTo(NavigationOptions options);

    /// <summary>
    /// Generates a navigation href from address/area/id combination.
    /// </summary>
    /// <param name="address">The address (e.g., "app/Northwind")</param>
    /// <param name="area">The area name (optional)</param>
    /// <param name="areaId">The area ID (optional)</param>
    /// <returns>A navigation href (e.g., "/app/Northwind/Dashboard")</returns>
    string GenerateHref(string address, string? area, string? areaId);

    /// <summary>
    /// Generates a content URL for the specified address and path.
    /// </summary>
    /// <param name="address">The address (e.g., "app/Northwind")</param>
    /// <param name="path">The content path (e.g., "Documents/report.pdf")</param>
    /// <returns>A content URL</returns>
    string GenerateContentUrl(string address, string path);

    /// <summary>
    /// Resolves a relative UCR path to an absolute path using current namespace.
    /// </summary>
    /// <param name="relativePath">The relative path</param>
    /// <returns>The absolute path</returns>
    string ResolveRelativePath(string relativePath);
}

/// <summary>
/// Default implementations for INavigationService helper methods.
/// </summary>
public static class NavigationServiceExtensions
{
    /// <summary>
    /// Default implementation of GenerateHref.
    /// </summary>
    public static string DefaultGenerateHref(string address, string? area, string? areaId)
    {
        var href = $"/{address}";
        if (!string.IsNullOrEmpty(area))
        {
            href += $"/{HttpUtility.UrlEncode(area)}";
            if (!string.IsNullOrEmpty(areaId))
                href += $"/{HttpUtility.UrlEncode(areaId)}";
        }
        return href;
    }

    /// <summary>
    /// Default implementation of GenerateContentUrl.
    /// </summary>
    public static string DefaultGenerateContentUrl(string address, string path)
    {
        return $"/content/{address}/{path}";
    }

    /// <summary>
    /// Default implementation of ResolveRelativePath.
    /// </summary>
    public static string DefaultResolveRelativePath(string? currentNamespace, string relativePath)
    {
        if (string.IsNullOrEmpty(currentNamespace))
            return relativePath;
        return $"{currentNamespace}/{relativePath}";
    }
}
