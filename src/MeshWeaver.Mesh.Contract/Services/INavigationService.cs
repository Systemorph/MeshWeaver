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

    /// <summary>
    /// Navigation target. When "SidePanel", the URI is loaded in the side panel
    /// instead of the main browser location.
    /// </summary>
    public string? Target { get; init; }
}

/// <summary>
/// Service for navigation and getting the current navigation path and namespace context.
/// Automatically subscribes to location changes and manages path resolution and creatable types.
/// </summary>
public interface INavigationService : IDisposable
{
    /// <summary>
    /// Reactive stream of the current relative path. <c>ReplaySubject&lt;T&gt;(1)</c>
    /// semantics — new subscribers immediately receive the last seen path, and every
    /// subsequent location change emits again. Never emits null or empty: the first
    /// emission is held back until the underlying <c>NavigationManager</c> has a
    /// real URI (RemoteNavigationManager throws "...has not been initialized"
    /// until the Blazor circuit's first JS interop tick — the previous
    /// <c>string? CurrentPath { get; }</c> property surfaced that as a
    /// caller-visible exception or a null fallback).
    /// </summary>
    IObservable<string> Path { get; }

    /// <summary>
    /// Gets the current namespace (resolved Address path).
    /// Used as the default namespace for queries when none is specified.
    /// </summary>
    string? CurrentNamespace { get; }

    /// <summary>
    /// Reactive: the navigation context stream — ReplaySubject(1) semantics.
    /// Emits the latest value on subscribe and on every change. Emits
    /// <c>null</c> for not-found / cleared state. Replaces the prior
    /// <c>OnNavigationContextChanged</c> event — Blazor views subscribe and call
    /// <c>StateHasChanged</c> on emit. See <c>Doc/Architecture/BlazorDataBinding.md</c>.
    /// </summary>
    IObservable<NavigationContext?> NavigationContext { get; }

    /// <summary>
    /// Snapshot of the latest <see cref="NavigationContext"/> — for sync read sites
    /// (Razor markup conditions, helper methods that need a single check).
    /// Reactive consumers should subscribe to <see cref="NavigationContext"/> instead.
    /// </summary>
    NavigationContext? Context { get; }

    /// <summary>
    /// True while the service is resolving the current path. When true, the
    /// current value of <see cref="NavigationContext"/> being <c>null</c> means
    /// "still loading", not "not found".
    /// </summary>
    bool IsResolving { get; }

    /// <summary>
    /// Observable stream of <see cref="NavigationStatus"/> values describing the
    /// page-lookup pipeline. Always has a current value (BehaviorSubject semantics)
    /// and every emitted <see cref="NavigationStatus.Message"/> is a non-empty,
    /// human-readable string — this is the "no endless spinner" contract.
    /// </summary>
    IObservable<NavigationStatus> Status { get; }

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
    /// When Target == "SidePanel", fires SidePanelNavigationRequested instead of browser navigation.
    /// </summary>
    /// <param name="options">The navigation options.</param>
    void NavigateTo(NavigationOptions options);

    /// <summary>
    /// Raised when a navigation request targets the side panel instead of the main browser.
    /// Subscribers (e.g., SidePanelStateService) handle by setting content path.
    /// </summary>
    event Action<string>? SidePanelNavigationRequested;

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
