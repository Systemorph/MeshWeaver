using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Pages;

/// <summary>
/// Main application page component. Resolves the current URL path to a mesh address,
/// subscribes to the navigation context stream, renders a layout area view, and handles
/// the prerender to interactive lifecycle including pre-rendered HTML caching.
/// </summary>
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

    [Inject]
    private Microsoft.Extensions.Logging.ILogger<ApplicationPage> Logger { get; set; } = null!;

    /// <summary>
    /// Auth state of the current visitor. Used to suppress the prerender HTML flash
    /// for a logged-OUT visitor: the anonymous gate (NavigationService → AccessDenied →
    /// RedirectToLogin) fires only once the circuit goes interactive, so without this
    /// check a PublicRead node's cached HTML would be served during the static prerender
    /// pass — the "public page shown to anonymous" symptom — before the redirect lands.
    /// </summary>
    [CascadingParameter]
    private Task<AuthenticationState>? AuthState { get; set; }

    /// <summary>
    /// Current status of the page-lookup pipeline. Always set to a non-null
    /// value so every render branch can safely display <see cref="NavigationStatus.Message"/>.
    /// </summary>
    private NavigationStatus Status { get; set; } = NavigationStatus.Idle();

    private IDisposable? _statusSubscription;

    /// <summary>
    /// Catch-all path parameter - the entire URL path is matched against registered namespace patterns.
    /// </summary>
    [Parameter]
    public string? Path { get; set; } = "";

    private string? PageTitle { get; set; } = "";

    /// <summary>Catches any route parameters not explicitly declared; passed through to the layout area as additional options.</summary>
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
    /// Tracks whether NavigationService.Initialize() has been called.
    /// Subsequent navigations rely on OnNavigationContextChanged instead
    /// of reading potentially stale NavigationService state.
    /// </summary>
    private bool _navigationServiceInitialized;

    private NavigationContext? _currentContext;
    private IDisposable? _navContextSubscription;

    /// <summary>
    /// Subscribes to the navigation context and status streams so the component reacts reactively
    /// to address resolution changes and navigation status updates throughout the circuit lifetime.
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();

        // Subscribe to the navigation-context stream — replaces the legacy
        // OnNavigationContextChanged event. ReplaySubject(1) on the service side
        // emits the current value on subscribe, so this also picks up state set
        // before the page initialised.
        _navContextSubscription = NavigationService.NavigationContext.Subscribe(
            context =>
            {
                _currentContext = context;
                InvokeAsync(() =>
                {
                    IsLoading = NavigationService.IsResolving;
                    UpdateFromContext();
                    StateHasChanged();
                });
            },
            // A faulting page-level stream with no onError propagates UNHANDLED on the Rx
            // scheduler and tears down the whole circuit (blank app). Log + leave the last-good
            // state; a subsequent navigation re-establishes the subscription.
            ex => Logger.LogWarning(ex, "NavigationContext subscription faulted"));

        _statusSubscription = NavigationService.Status.Subscribe(
            status =>
            {
                Status = status;
                _ = InvokeAsync(StateHasChanged);
            },
            ex => Logger.LogWarning(ex, "Navigation Status subscription faulted"));
    }

    /// <summary>
    /// During the prerender phase, fetches cached pre-rendered HTML for authenticated visitors.
    /// During the interactive phase, triggers a full navigation initialization when the path changes.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        if (!IsInteractive)
        {
            // Prerender: subscribe once for the cached HTML; first emission wins.
            // Subscribe + Take(1) — no await on a hub-touching observable.
            // Skip entirely for a logged-OUT visitor: serving a PublicRead node's
            // cached HTML here would leak content before the interactive anonymous
            // gate (NavigationService → AccessDenied → RedirectToLogin) redirects them.
            if (!string.IsNullOrEmpty(Path) && await IsAuthenticatedAsync())
            {
                MeshService.GetPreRenderedHtml(Path)
                    .Take(1)
                    .Subscribe(
                        html =>
                        {
                            PreRenderedHtml = html;
                            _ = InvokeAsync(StateHasChanged);
                        },
                        ex => Logger.LogWarning(ex, "GetPreRenderedHtml subscription faulted for {Path}", Path));
            }
            return;
        }

        // Interactive phase: re-initialize if the path changed (page navigation within circuit)
        if (Path != _lastInitializedPath)
            await InitializeForCurrentPath();
    }

    /// <summary>
    /// On the first render, marks the component as interactive and runs the full path initialization
    /// so the layout area subscription and navigation service are set up for the interactive circuit.
    /// </summary>
    /// <param name="firstRender">True on the first render after the circuit becomes interactive.</param>
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

    /// <summary>
    /// True when the current visitor is logged in. Returns false when no auth-state
    /// cascade is present (fail-closed: treat an unknown visitor as anonymous so the
    /// prerender content gate is never silently bypassed).
    /// </summary>
    private async Task<bool> IsAuthenticatedAsync()
        => AuthState is not null && (await AuthState).User.Identity?.IsAuthenticated == true;

    private async Task InitializeForCurrentPath()
    {
        _lastInitializedPath = Path;
        IsLoading = true;
        PreRenderedHtml = null;

        if (!_navigationServiceInitialized)
        {
            _navigationServiceInitialized = true;
            // Synchronous: Initialize() only wires Rx subscriptions and pushes the
            // current path — no await (a Task here would deadlock the circuit). The
            // resolved IsResolving snapshot below is the initial reactive state;
            // OnNavigationContextChanged keeps it live thereafter.
            NavigationService.Initialize();
            IsLoading = NavigationService.IsResolving;
            UpdateFromContext();
        }
        // Subsequent navigations: stay in loading state until OnNavigationContextChanged fires
        // to avoid showing stale content from a previous path
    }

    private void UpdateFromContext()
    {
        var context = _currentContext;

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

        // Seed the layout-area progress message with "Subscribing to area …" so
        // that the LayoutAreaView never renders a labelless spinner while waiting
        // for its first stream emission. CompileProgressIndicator still wins when
        // a node-type compile is running (more specific signal).
        var progressMessage = NavigationStatus
            .Subscribing(context.Address.ToString() ?? string.Empty, area)
            .Message;

        ViewModel = Controls.LayoutArea(context.Address, Reference)
            with
        {
            ShowProgress = true,
            ProgressMessage = progressMessage
        };

        // Use node name for the page title, falling back to the last address segment
        PageTitle = context.Node?.Name
            ?? context.Address.Segments.LastOrDefault()
            ?? context.Address.Type;
    }

    /// <summary>
    /// Render-branch decision: does the page replace the whole content area with the
    /// full-page <c>NavigationProgressBar</c>? ONLY when there is no previously-rendered
    /// <see cref="ViewModel"/> to keep showing: once a layout area has rendered, an
    /// in-circuit navigation keeps it mounted (LayoutAreaView / NamedAreaView keep-last-good
    /// swap in the new content when its first frame arrives) and a slow navigation surfaces
    /// only the compact overlay on top — never the full-page "interrupt" that blanked the
    /// page on every slide-to-slide navigation. Pure and static so it is table-testable.
    /// </summary>
    internal static bool ShowFullProgress(bool isInteractive, bool isLoading, bool hasContext, bool hasViewModel)
        => !isInteractive || (!hasViewModel && (isLoading || !hasContext));

    private string? GetDisplayNameFromId()
    {
        if (Reference.Id is null)
            return null!;

        return Reference.Id.ToString()!.Split("?").First().Split("/").Last();
    }

    /// <summary>Disposes the navigation context and status subscriptions.</summary>
    public void Dispose()
    {
        _navContextSubscription?.Dispose();
        _statusSubscription?.Dispose();
    }
}
