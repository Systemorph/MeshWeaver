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
    // IMeshQueryCore is registered on the mesh hub's Autofac scope (see
    // PersistenceExtensions.RegisterMeshQueryCoreOnMeshHub) — NOT on the root
    // container. NavigationService is scoped to the Blazor circuit (root scope),
    // so injecting IMeshQueryCore via the constructor would fail with
    // "Cannot resolve parameter IMeshQueryCore". Resolve from the hub's
    // ServiceProvider instead — same instance, just sourced from the right scope.
    private readonly Lazy<IMeshQueryCore> _queryCore;
    private readonly IMessageHub _hub;
    // Reactive path stream — see <see cref="INavigationService.Path"/>. Never
    // emits null/empty; the first emission lands when ProcessLocationChange or
    // OnLocationChanged read a real URI off NavigationManager. Latecomer
    // subscribers get the last seen path via ReplaySubject(1) cache.
    private readonly System.Reactive.Subjects.ReplaySubject<string> _pathSubject = new(bufferSize: 1);
    // Mirror of _pathSubject's last emission. Used for synchronous "did the user
    // navigate away during the retry delay?" checks where we don't want to plumb
    // an Rx subscription. Updated on every OnNext below; reads are racy but
    // bounded — the worst case is a stale retry that's then re-dropped on the
    // next tick.
    private string? _currentPathSnapshot;
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
        IMessageHub hub)
        : this(navigationManager, pathResolver, hub, DefaultRetryDelays)
    {
    }

    /// <summary>
    /// Test-only overload that accepts a custom retry schedule so the
    /// retry-exhaustion path runs fast.
    /// </summary>
    internal NavigationService(
        NavigationManager navigationManager,
        IPathResolver pathResolver,
        IMessageHub hub,
        int[] retryDelays)
    {
        _navigationManager = navigationManager;
        _pathResolver = pathResolver;
        _hub = hub;
        _queryCore = new Lazy<IMeshQueryCore>(
            () => hub.ServiceProvider.GetRequiredService<IMeshQueryCore>(),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _logger = hub.ServiceProvider.GetService<ILogger<NavigationService>>();
        _retryDelays = retryDelays ?? DefaultRetryDelays;

        // Status starts unlabelled — InitializeAsync emits LookingUp with the
        // real path the moment NavigationManager has a URI and the Path stream
        // gets its first emission.
        _status.OnNext(NavigationStatus.LookingUp(null));
    }

    /// <inheritdoc />
    public IObservable<string> Path => _pathSubject;

    /// <summary>
    /// Reads <c>NavigationManager.Uri</c> guarded against RemoteNavigationManager's
    /// "has not been initialized" exception. Internal — external code subscribes
    /// to <see cref="Path"/> instead. Returns null when the manager isn't ready;
    /// the caller decides whether to fall back, retry, or skip emitting.
    /// </summary>
    private string? TryReadCurrentPath()
    {
        try { return _navigationManager.ToBaseRelativePath(_navigationManager.Uri); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Pushes <paramref name="path"/> onto the <see cref="Path"/> stream and
    /// updates the synchronous mirror used by retry's stale-check. Skips empty
    /// paths so the never-null-or-empty contract holds.
    /// </summary>
    private void PublishPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _currentPathSnapshot = path;
        _pathSubject.OnNext(path);
    }

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

        // Push the initial path onto the Path stream and kick off resolution.
        // Reactive — Subscribe-driven; never awaits a hub round-trip. Callers
        // that need to observe the resulting NavigationContext subscribe to
        // <see cref="NavigationContext"/>.
        var initial = TryReadCurrentPath();
        if (!string.IsNullOrEmpty(initial))
        {
            PublishPath(initial);
            ProcessLocationChange(initial);
        }
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
        PublishPath(path);
        ProcessLocationChange(path);
    }

    // Per-path resolution subscription. Cancelled when the user navigates away
    // so the previous path's live resolution stream stops emitting into a
    // navigation context that's no longer relevant.
    private IDisposable? _resolutionSubscription;
    // Watchdog disposable for the not-found timeout. Cancelled when the
    // resolution arrives in time or when the user navigates away.
    private IDisposable? _notFoundWatchdog;

    private void ProcessLocationChange(string path)
    {
        IsResolving = true;
        _status.OnNext(NavigationStatus.LookingUp(path));

        // Cancel any prior resolution + watchdog. The user navigated; the
        // previous path's resolution stream is now stale.
        _resolutionSubscription?.Dispose();
        _notFoundWatchdog?.Dispose();

        // Subscribe-and-stay: the live ResolvePath stream re-emits whenever
        // the catalog changes. Once we get a non-null resolution we proceed,
        // and the subscription stays open so a later catalog change (e.g. the
        // node being deleted) reflows into the navigation context. No retry
        // timer, no backoff array — the catalog change feed drives re-emit.
        var resolved = false;
        _resolutionSubscription = _pathResolver.ResolvePath(path).Subscribe(resolution =>
        {
            if (resolution is null)
                return; // wait for the catalog to learn about the path
            resolved = true;
            _notFoundWatchdog?.Dispose();
            ProcessResolvedPath(path, resolution);
        });

        // Watchdog: if no resolution arrives within the cumulative retry budget,
        // flip to NotFound. Replaces the previous per-attempt Observable.Timer
        // chain — same outer time budget, but the work happens through the live
        // stream above instead of polling re-resolves.
        var totalBudget = _retryDelays.Sum();
        _notFoundWatchdog = Observable.Timer(TimeSpan.FromMilliseconds(totalBudget))
            .Subscribe(_ =>
            {
                if (resolved) return;
                if (_currentPathSnapshot != path) return; // user navigated away
                IsResolving = false;
                Context = null;
                CurrentNamespace = null;
                _status.OnNext(NavigationStatus.NotFound(path));
                _navigationContext.OnNext(null);
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
    /// Loads the MeshNode for the resolved address. If the node has MarkdownContent
    /// but no PreRenderedHtml, generates it and persists back. Reactive â€” no await
    /// on hub round-trips (100% deadlock; see Doc/Architecture/AsynchronousCalls.md).
    /// </summary>
    private IObservable<MeshNode?> LoadNodeWithPreRenderedHtml(AddressResolution resolution) =>
        // Navigation startup uses IMeshQueryCore.ObserveQuery — infrastructure
        // surface, no ISecurityService dep, no hub round-trip, no awaited Rx
        // bridge. hub.GetMeshNode is the CQRS-correct primitive for application
        // code, but it's an extension method over IMessageHub which NSubstitute
        // can't proxy; IMeshQueryCore is a plain interface and is mockable.
        // Staleness doesn't matter here — we read MainNode + PreRenderedHtml at
        // navigation startup, not for CQRS-sensitive writes.
        _queryCore.Value
            .ObserveQuery<MeshNode>(
                // select: projects to only the routing-relevant fields — content
                // is loaded only if we need to pre-render markdown below.
                MeshQueryRequest.FromQuery(
                    $"path:{resolution.Prefix} select:path,name,mainNode,nodeType,icon,preRenderedHtml,content"),
                _hub.JsonSerializerOptions)
            .Select(change => change.Items.Count > 0 ? change.Items[0] : null)
            .Take(1)
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
        _resolutionSubscription?.Dispose();
        _notFoundWatchdog?.Dispose();
        _pathSubject.OnCompleted();
        _pathSubject.Dispose();
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

