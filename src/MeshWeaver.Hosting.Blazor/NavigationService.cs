using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// Provides the current navigation path and namespace context from NavigationManager.
/// </summary>
public class NavigationService(NavigationManager navigationManager) : INavigationService
{
    /// <inheritdoc />
    public string? CurrentPath => navigationManager.ToBaseRelativePath(navigationManager.Uri);

    /// <inheritdoc />
    public string? CurrentNamespace { get; private set; }

    /// <inheritdoc />
    public void SetCurrentNamespace(string? @namespace)
    {
        CurrentNamespace = @namespace;
    }

    /// <inheritdoc />
    public void NavigateTo(string uri, bool forceLoad = false)
    {
        navigationManager.NavigateTo(uri, forceLoad);
    }

    /// <inheritdoc />
    public string GenerateHref(string address, string? area, string? areaId)
    {
        return NavigationServiceExtensions.DefaultGenerateHref(address, area, areaId);
    }

    /// <inheritdoc />
    public string GenerateContentUrl(string address, string path)
    {
        return NavigationServiceExtensions.DefaultGenerateContentUrl(address, path);
    }

    /// <inheritdoc />
    public string ResolveRelativePath(string relativePath)
    {
        return NavigationServiceExtensions.DefaultResolveRelativePath(CurrentNamespace, relativePath);
    }
}
