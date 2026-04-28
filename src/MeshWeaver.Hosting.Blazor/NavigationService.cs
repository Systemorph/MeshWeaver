using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NavigationContext = MeshWeaver.Mesh.Services.NavigationContext;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Blazor.Test")]

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// Provides the current navigation path and namespace context from NavigationManager.
/// Automatically subscribes to location changes and manages path resolution and creatable types.
/// </summary>
internal class NavigationService : INavigationService
{
    private readonly NavigationManager _navigationManager;
    private readonly IPathResolver _pathResolver;
    private readonly IMeshService _meshQuery;
    private readonly IMessageHub _hub;
    private readonly ILogger<NavigationService>? _logger;
    private readonly int[] _retryDelays;

    // Production retry schedule â€” ~11.5 s total. Tests override via the internal
    // ctor overload with short delays so the full retry-exhaustion path runs fast.
    private static readonly int[] DefaultRetryDelays = [500, 1000, 2000, 3000, 5000];

    // ReplaySubject(1) — emits the last value to new subscribers; no initial value
    // required. NavigationContext starts unobserved until ProcessLocationChange fires.
    private readonly ReplaySubject<NavigationContext?> _navigationContext = new(bufferSize: 1);
    private readonly BehaviorSubject<CreatableTypesSnapshot> _creatableTypes = new(CreatableTypesSnapshot.Empty);
    private readonly BehaviorSubject<NavigationStatus> _status = new(NavigationStatus.Idle());
    private string? _lastLoadedNodePath;
    private CancellationTokenSource? _loadingCts;
    private bool _isInitialized;
    private bool _disposed;

    public NavigationService(
        NavigationManager navigationManager,
        IPathResolver pathResolver,
        IMeshService meshQuery,
        IMessageHub hub)
        : this(navigationManager, pathResolver, meshQuery, hub, DefaultRetryDelays)
    {
    }

    /// <summary>
    /// Test-only overload that accepts a custom retry schedule so the
    /// retry-exhaustion path runs fast.
    /// </summary>
    internal NavigationService(
        NavigationManager navigationManager,
        IPathResolver pathResolver,
        IMeshService meshQuery,
        IMessageHub hub,
        int[] retryDelays)
    {
        _navigationManager = navigationManager;
        _pathResolver = pathResolver;
        _meshQuery = meshQuery;
        _hub = hub;
        _logger = hub.ServiceProvider.GetService<ILogger<NavigationService>>();
        _retryDelays = retryDelays ?? DefaultRetryDelays;

        // Start with a descriptive status so the very first render has a label â€”
        // never a blank spinner. `CurrentPath` reads `NavigationManager.Uri`, which
        // throws "RemoteNavigationManager has not been initialized" if accessed
        // during DI construction (Blazor Server circuit activation). Fall back to
        // an empty path in that case â€” InitializeAsync re-emits LookingUp with the
        // real path once the NavigationManager is wired up.
        string? initialPath = null;
        try { initialPath = CurrentPath; } catch (InvalidOperationException) { /* not yet initialized */ }
        _status.OnNext(NavigationStatus.LookingUp(initialPath));
    }

    /// <inheritdoc />
    public string? CurrentPath => _navigationManager.ToBaseRelativePath(_navigationManager.Uri);

    /// <inheritdoc />
    public string? CurrentNamespace { get; private set; }

    /// <inheritdoc />
    public IObservable<NavigationContext?> NavigationContext => _navigationContext;

    /// <inheritdoc />
    public NavigationContext? Context { get; private set; }

    /// <inheritdoc />
    public bool IsResolving { get; private set; } = true;

    /// <inheritdoc />
    public IObservable<NavigationStatus> Status => _status;

    /// <inheritdoc />
    public IObservable<CreatableTypesSnapshot> CreatableTypes => _creatableTypes;

    /// <inheritdoc />
    public void RefreshCreatableTypes()
    {
        var snapshot = _creatableTypes.Value;
        if (snapshot.IsLoading || snapshot.Items.Count > 0)
            return;

        _ = LoadCreatableTypesAsync(CurrentNamespace ?? "");
    }

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        if (_isInitialized)
            return Task.CompletedTask;

        _isInitialized = true;
        _navigationManager.LocationChanged += OnLocationChanged;

        // Reactive — kicks off the resolution chain via Subscribe; never awaits it.
        // Callers that need to observe the resulting NavigationContext should
        // subscribe to OnNavigationContextChanged. Tests bridge that event at
        // their edge (TaskCompletionSource sanctioned per AsynchronousCalls.md).
        ProcessLocationChange(CurrentPath ?? "");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void SetCurrentNamespace(string? @namespace)
    {
        if (CurrentNamespace == @namespace)
            return;

        CurrentNamespace = @namespace;

        // Load creatable types in background when namespace changes
        var effectiveNamespace = @namespace ?? "";
        if (effectiveNamespace != _lastLoadedNodePath)
        {
            _ = LoadCreatableTypesAsync(effectiveNamespace);
        }
    }

    /// <inheritdoc />
    public void NavigateTo(string uri, bool forceLoad = false, bool replace = false)
        => NavigateTo(new MeshWeaver.Mesh.Services.NavigationOptions(uri) { ForceLoad = forceLoad, Replace = replace });

    /// <inheritdoc />
    public event Action<string>? SidePanelNavigationRequested;

    /// <inheritdoc />
    public void NavigateTo(MeshWeaver.Mesh.Services.NavigationOptions options)
    {
        if (options.Target == "SidePanel")
        {
            var path = options.Uri.TrimStart('/');
            SidePanelNavigationRequested?.Invoke(path);
        }
        else
        {
            _navigationManager.NavigateTo(options.Uri, options.ForceLoad, options.Replace);
        }
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

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var path = _navigationManager.ToBaseRelativePath(e.Location);
        ProcessLocationChange(path);
    }

    private void ProcessLocationChange(string path)
    {
        IsResolving = true;
        _status.OnNext(NavigationStatus.LookingUp(path));

        // Reactive — Subscribe, never await (await on resolution chain is a deadlock
        // surface; see Doc/Architecture/AsynchronousCalls.md).
        _pathResolver.ResolvePath(path).Subscribe(resolution =>
        {
            if (resolution is null)
            {
                // Do NOT fire OnNavigationContextChanged(null) here — that causes the
                // "Page Not Found" card to flash while retries are still running.
                RetryResolution(path);
                return;
            }
            ProcessResolvedPath(path, resolution);
        });
    }

    private void ProcessResolvedPath(string path, AddressResolution resolution)
    {
        var (area, id) = ParseRemainder(resolution.Remainder);
        _status.OnNext(NavigationStatus.Redirecting(resolution.Prefix, area));
        _status.OnNext(NavigationStatus.Loading(resolution.Prefix));

        // Reactive load â€” Subscribe, never await (every async bit through a hub
        // round-trip is a deadlock surface; see Doc/Architecture/AsynchronousCalls.md).
        LoadNodeWithPreRenderedHtml(resolution).Subscribe(node =>
        {
            IsResolving = false;

            var context = new NavigationContext
            {
                Path = path,
                Resolution = resolution,
                Area = area,
                Id = id,
                Node = node
            };

            Context = context;
            CurrentNamespace = context.PrimaryPath;

            if (node != null)
                TrackNavigationActivity(node);

            _navigationContext.OnNext(context);
            _status.OnNext(NavigationStatus.Ready(resolution.Prefix));

            var currentNodePath = context.PrimaryPath ?? "";
            if (currentNodePath != _lastLoadedNodePath)
                _ = LoadCreatableTypesAsync(currentNodePath);
        });
    }

    /// <summary>
    /// Retries path resolution with backoff when the initial attempt returns null.
    /// This handles the case where the mesh catalog is still initializing at startup.
    /// Runs in the background so the UI can show a spinner while waiting.
    /// </summary>
    /// <summary>
    /// Reactive retry chain — schedule timer + resolve, recurse to next attempt on
    /// miss. No await, no Task.Delay; uses Observable.Timer.
    /// </summary>
    private void RetryResolution(string path) => AttemptRetry(path, 0);

    private void AttemptRetry(string path, int attemptIdx)
    {
        if (attemptIdx >= _retryDelays.Length)
        {
            // All retries exhausted — flip to "Page Not Found".
            IsResolving = false;
            Context = null;
            CurrentNamespace = null;
            _status.OnNext(NavigationStatus.NotFound(path));
            _navigationContext.OnNext(null);
            return;
        }

        Observable.Timer(TimeSpan.FromMilliseconds(_retryDelays[attemptIdx]))
            .Where(_ => CurrentPath == path) // navigation moved on → drop
            .SelectMany(_ => _pathResolver.ResolvePath(path))
            .Subscribe(resolution =>
            {
                if (resolution is not null)
                    ProcessResolvedPath(path, resolution);
                else
                    AttemptRetry(path, attemptIdx + 1);
            });
    }

    /// <summary>
    /// Loads the MeshNode for the resolved address. If the node has MarkdownContent
    /// but no PreRenderedHtml, generates it and persists back. Reactive â€” no await
    /// on hub round-trips (100% deadlock; see Doc/Architecture/AsynchronousCalls.md).
    /// </summary>
    private IObservable<MeshNode?> LoadNodeWithPreRenderedHtml(AddressResolution resolution) =>
        // Single-node-by-path read goes through hub.GetMeshNode (per-node MeshNodeReference
        // reducer), NOT QueryAsync (lagged). See Doc/Architecture/AsynchronousCalls.md.
        _hub.GetMeshNode(resolution.Prefix, TimeSpan.FromSeconds(10))
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .Select(node =>
            {
                if (node == null) return null;
                if (node.PreRenderedHtml != null) return node;
                if (node.Content is not MarkdownContent md || string.IsNullOrEmpty(md.Content))
                    return node;

                var html = md.PrerenderedHtml
                    ?? MarkdownContent.Parse(md.Content, node.Path, node.Path).PrerenderedHtml;
                if (html == null) return node;

                node = node with { PreRenderedHtml = html };
                try { _hub.Post(new UpdateNodeRequest(node), o => o.WithTarget(_hub.Address)); }
                catch (Exception ex)
                {
                    _hub.ServiceProvider.GetService<ILogger<NavigationService>>()
                        ?.LogWarning(ex, "Failed to persist PreRenderedHtml for {Path}", node.Path);
                }
                return node;
            });

    /// <summary>
    /// Node types excluded from activity tracking.
    /// Satellite content (MainNode != Path) is also excluded automatically.
    /// </summary>
    private static readonly HashSet<string> ExcludedNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "User",
        "Role",
        "Group",
        "AccessAssignment",
    };

    /// <summary>
    /// Records a navigation activity for the "Recently Viewed" dashboard section.
    /// Posts TrackActivityRequest to the hub â€” handled asynchronously on a sub-hub.
    /// </summary>
    private void TrackNavigationActivity(MeshNode node)
    {
        var accessService = _hub.ServiceProvider.GetService<AccessService>();
        if ((accessService?.Context ?? accessService?.CircuitContext)?.ObjectId is not { } userId || string.IsNullOrEmpty(userId))
            return;

        // Skip system/internal paths
        if (string.IsNullOrEmpty(node.Path) || node.Path.StartsWith('_'))
            return;

        // Skip satellite content and excluded node types
        if (node.MainNode != node.Path)
            return;
        if (node.NodeType != null && ExcludedNodeTypes.Contains(node.NodeType))
            return;

        _hub.Post(new TrackActivityRequest(node.Path, userId, node.Name, node.NodeType, node.Namespace));
    }

    private static (string? Area, string? Id) ParseRemainder(string? remainder)
    {
        if (string.IsNullOrEmpty(remainder))
            return (null, null);

        // First, check for query string (?) - query string becomes part of the Id
        var queryIndex = remainder.IndexOf('?');
        string mainPart;
        string? querySuffix = null;

        if (queryIndex >= 0)
        {
            mainPart = remainder.Substring(0, queryIndex);
            querySuffix = remainder.Substring(queryIndex); // includes the '?'
        }
        else
        {
            mainPart = remainder;
        }

        // Now parse the main part for area/id using '/'
        var slashIndex = mainPart.IndexOf('/');
        if (slashIndex >= 0)
        {
            var area = mainPart.Substring(0, slashIndex);
            var id = mainPart.Substring(slashIndex + 1);
            // Append query string to id if present
            if (querySuffix != null)
                id = string.IsNullOrEmpty(id) ? querySuffix : id + querySuffix;
            return (area, id);
        }

        // No slash - the main part is the area, query string (if any) is the id
        return (mainPart, querySuffix);
    }

    private async Task LoadCreatableTypesAsync(string nodePath)
    {
        // INodeTypeService is registered at the Hub level, not in the main DI container
        var nodeTypeService = _hub.ServiceProvider.GetService<INodeTypeService>();
        if (nodeTypeService == null)
            return;

        // Cancel any previous loading
        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var ct = _loadingCts.Token;

        _lastLoadedNodePath = nodePath;
        var items = new List<CreatableTypeInfo>();
        _creatableTypes.OnNext(CreatableTypesSnapshot.Loading(items.ToArray()));

        try
        {
            await foreach (var typeInfo in nodeTypeService.GetCreatableTypesAsync(nodePath, ct).WithCancellation(ct))
            {
                items.Add(typeInfo);
                _creatableTypes.OnNext(CreatableTypesSnapshot.Loading(items.ToArray()));
            }
        }
        catch (OperationCanceledException)
        {
            // Path changed, loading was cancelled - this is expected
        }
        catch
        {
            // Fallback on error - keep whatever items we loaded
        }
        finally
        {
            _creatableTypes.OnNext(CreatableTypesSnapshot.Done(items.ToArray()));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _navigationContext.Dispose();
        _creatableTypes.Dispose();
        _status.Dispose();

        // Only unsubscribe if we actually subscribed (InitializeAsync was called)
        // Wrap in try-catch because NavigationManager may not be initialized if circuit was never established
        if (_isInitialized)
        {
            try
            {
                _navigationManager.LocationChanged -= OnLocationChanged;
            }
            catch (InvalidOperationException)
            {
                // NavigationManager was not initialized - nothing to unsubscribe
            }
        }
    }
}

