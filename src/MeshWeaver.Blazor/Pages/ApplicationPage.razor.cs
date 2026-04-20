using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
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

    // Resolved lazily from the service provider so the page still renders when
    // INodeTypeService isn't registered. A hard [Inject] would throw during
    // component construction and leave the user with a black screen.
    [Inject]
    private IServiceProvider Services { get; set; } = null!;
    private INodeTypeService? NodeTypeService => Services.GetService<INodeTypeService>();

    /// <summary>
    /// Path of any NodeType currently compiling. Used by the razor template to flip
    /// the "Looking up …" placeholder into "Compiling &lt;path&gt; (Ns)…" during the
    /// navigation blocking phase, so the user sees activity instead of a blank spinner.
    /// </summary>
    private string? CompilingPath { get; set; }

    /// <summary>Elapsed seconds since the current compile started.</summary>
    private int CompilingSeconds { get; set; }

    private System.Threading.Timer? _compileProgressTimer;

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

    /// <summary>
    /// Tracks whether NavigationService.InitializeAsync() has been called.
    /// Subsequent navigations rely on OnNavigationContextChanged instead
    /// of reading potentially stale NavigationService state.
    /// </summary>
    private bool _navigationServiceInitialized;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        NavigationService.OnNavigationContextChanged += OnNavigationContextChanged;

        // Poll NodeTypeService.GetCompilingPaths while the page is in "Looking up"
        // state so the user sees "Compiling <path> (Ns)…" rather than a blank spinner.
        // Stopped once IsLoading flips to false. Two-second granularity is enough —
        // most compiles are sub-second; the tick is for reassurance on slow ones.
        _compileProgressTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (!IsLoading) return;
                var paths = NodeTypeService?.GetCompilingPaths();
                var first = paths?.FirstOrDefault();
                if (first != CompilingPath)
                {
                    CompilingPath = first;
                    CompilingSeconds = 0;
                }
                else if (first != null)
                {
                    CompilingSeconds++;
                }
                _ = InvokeAsync(StateHasChanged);
            }
            catch
            {
                // Timer tick should never take down the page. The worst-case is a stale
                // "Compiling…" message; that's better than a crashed circuit.
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
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
        PreRenderedHtml = null;

        if (!_navigationServiceInitialized)
        {
            _navigationServiceInitialized = true;
            await NavigationService.InitializeAsync();
            // First init: safe to read state since InitializeAsync awaited resolution
            IsLoading = NavigationService.IsResolving;
            UpdateFromContext();
        }
        // Subsequent navigations: stay in loading state until OnNavigationContextChanged fires
        // to avoid showing stale content from a previous path
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
        _compileProgressTimer?.Dispose();
    }
}
