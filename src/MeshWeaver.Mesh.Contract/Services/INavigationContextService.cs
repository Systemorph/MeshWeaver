using System.Web;

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
/// Default implementations for INavigationContextService helper methods.
/// </summary>
public static class NavigationContextServiceExtensions
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
