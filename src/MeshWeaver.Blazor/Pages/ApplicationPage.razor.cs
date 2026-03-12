using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Pages;

public partial class ApplicationPage : ComponentBase, IDisposable
{
    private DesignThemeModes Mode { get; set; }
    private LayoutAreaControl ViewModel { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    [Inject]
    private IMessageHub Hub { get; set; } = null!;

    [Inject]
    private INavigationService NavigationService { get; set; } = null!;

    [Inject]
    private IMeshService MeshService { get; set; } = null!;

    /// <summary>
    /// Catch-all path parameter - the entire URL path is matched against registered namespace patterns.
    /// </summary>
    [Parameter]
    public string? Path { get; set; } = "";

    private string? PageTitle { get; set; } = "";

    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? Options { get; set; } = ImmutableDictionary<string, object>.Empty;

    private LayoutAreaReference Reference { get; set; } = null!;

    /// <summary>
    /// Pre-rendered HTML from the MeshNode for instant display during Blazor prerender phase.
    /// </summary>
    private string? PreRenderedHtml { get; set; }

    /// <summary>
    /// Set to true after OnAfterRender fires, indicating we're in the interactive phase.
    /// </summary>
    private bool IsInteractive { get; set; }

    /// <summary>
    /// True while the component is still waiting for address resolution.
    /// </summary>
    private bool IsLoading { get; set; } = true;

    /// <summary>
    /// Tracks the Path value that was last initialized, so we can detect
    /// parameter changes after the interactive phase has started.
    /// </summary>
    private string? _lastInitializedPath;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        NavigationService.OnNavigationContextChanged += OnNavigationContextChanged;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!IsInteractive)
        {
            // Prerender: one-shot attempt to get cached HTML directly from the MeshNode.
            // No path resolution, no stream subscriptions, no PortalApplication.
            if (!string.IsNullOrEmpty(Path))
                PreRenderedHtml = await MeshService.GetPreRenderedHtmlAsync(Path);
            return;
        }

        // Interactive phase: re-initialize if the path changed (page navigation within circuit)
        if (Path != _lastInitializedPath)
            await InitializeForCurrentPath();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Transition from prerender to interactive — start everything now.
            IsInteractive = true;
            await InitializeForCurrentPath();
            StateHasChanged();
        }
    }

    private async Task InitializeForCurrentPath()
    {
        _lastInitializedPath = Path;
        IsLoading = true;
        await NavigationService.InitializeAsync();
        IsLoading = NavigationService.IsResolving;
        UpdateFromContext();
    }

    private void OnNavigationContextChanged(NavigationContext? context)
    {
        InvokeAsync(() =>
        {
            IsLoading = NavigationService.IsResolving;
            UpdateFromContext();
            StateHasChanged();
        });
    }

    private void UpdateFromContext()
    {
        var context = NavigationService.Context;

        if (context is null)
        {
            PageTitle = IsLoading ? "Loading..." : "Page Not Found";
            PreRenderedHtml = null;
            return;
        }

        // Clear prerender HTML now that we have the interactive view
        PreRenderedHtml = null;

        // Decode area and id, append query string
        var area = context.Area != null ? (string)WorkspaceReference.Decode(context.Area) : null;
        var id = context.Id != null ? (string)WorkspaceReference.Decode(context.Id) : "";

        var query = Navigation.ToAbsoluteUri(Navigation.Uri).Query;
        if (!string.IsNullOrEmpty(query))
            id = id + query;

        Reference = new(area)
        {
            Id = id,
        };

        ViewModel = Controls.LayoutArea(context.Address, Reference)
            with
        {
            ShowProgress = true
        };

        // Use node name for the page title, falling back to the last address segment
        PageTitle = context.Node?.Name
            ?? context.Address.Segments.LastOrDefault()
            ?? context.Address.Type;
    }

    private string? GetDisplayNameFromId()
    {
        if (Reference.Id is null)
            return null!;

        return Reference.Id.ToString()!.Split("?").First().Split("/").Last();
    }

    public void Dispose()
    {
        NavigationService.OnNavigationContextChanged -= OnNavigationContextChanged;
    }
}
