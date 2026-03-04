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

    protected override void OnInitialized()
    {
        base.OnInitialized();
        NavigationService.OnNavigationContextChanged += OnNavigationContextChanged;
    }

    protected override async Task OnParametersSetAsync()
    {
        await NavigationService.InitializeAsync();
        UpdateFromContext();
    }

    private void OnNavigationContextChanged(NavigationContext? context)
    {
        InvokeAsync(() =>
        {
            UpdateFromContext();
            StateHasChanged();
        });
    }

    private void UpdateFromContext()
    {
        var context = NavigationService.Context;

        if (context is null)
        {
            PageTitle = NavigationService.IsResolving ? "Loading..." : "Page Not Found";
            PreRenderedHtml = null;
            return;
        }

        // Get pre-rendered HTML from the resolved node
        PreRenderedHtml = context.Node?.PreRenderedHtml;

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

    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);
        if (firstRender)
        {
            IsInteractive = true;
            StateHasChanged();
        }
    }

    private string? GetDisplayNameFromId()
    {
        // TODO V10: This is very hand woven.
        // We need some configurability for how to create DisplayArea, PageTitle, etc.  (14.08.2024, Roland Bürgi)

        if (Reference.Id is null)
            return null!;

        return Reference.Id.ToString()!.Split("?").First().Split("/").Last();
    }

    public void Dispose()
    {
        NavigationService.OnNavigationContextChanged -= OnNavigationContextChanged;
    }
}
