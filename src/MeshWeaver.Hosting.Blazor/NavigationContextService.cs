using MeshWeaver.Data.Services;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// Provides the current navigation path from NavigationManager.
/// </summary>
public class NavigationContextService : INavigationContextService
{
    private readonly NavigationManager navigationManager;

    public NavigationContextService(NavigationManager navigationManager)
    {
        this.navigationManager = navigationManager;
    }

    /// <inheritdoc />
    public string? CurrentPath => navigationManager.ToBaseRelativePath(navigationManager.Uri);
}
