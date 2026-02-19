using System.Reactive.Subjects;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NavigationContext = MeshWeaver.Mesh.Services.NavigationContext;

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// Provides the current navigation path and namespace context from NavigationManager.
/// Automatically subscribes to location changes and manages path resolution and creatable types.
/// </summary>
public class NavigationService : INavigationService
{
    private readonly NavigationManager _navigationManager;
    private readonly IMeshCatalog _meshCatalog;
    private readonly IMessageHub _hub;

    private NavigationContext? _context;
    private readonly BehaviorSubject<CreatableTypesSnapshot> _creatableTypes = new(CreatableTypesSnapshot.Empty);
    private string? _lastLoadedNodePath;
    private CancellationTokenSource? _loadingCts;
    private bool _isInitialized;
    private bool _disposed;

    public NavigationService(
        NavigationManager navigationManager,
        IMeshCatalog meshCatalog,
        IMessageHub hub)
    {
        _navigationManager = navigationManager;
        _meshCatalog = meshCatalog;
        _hub = hub;
    }

    /// <inheritdoc />
    public string? CurrentPath => _navigationManager.ToBaseRelativePath(_navigationManager.Uri);

    /// <inheritdoc />
    public string? CurrentNamespace { get; private set; }

    /// <inheritdoc />
    public NavigationContext? Context => _context;

    /// <inheritdoc />
    public event Action<NavigationContext?>? OnNavigationContextChanged;

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
    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        _isInitialized = true;
        _navigationManager.LocationChanged += OnLocationChanged;

        // Process the current location
        await ProcessLocationChangeAsync(CurrentPath ?? "");
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
    public void NavigateTo(MeshWeaver.Mesh.Services.NavigationOptions options)
    {
        _navigationManager.NavigateTo(options.Uri, options.ForceLoad, options.Replace);
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
        _ = ProcessLocationChangeAsync(path);
    }

    private async Task ProcessLocationChangeAsync(string path)
    {
        // Resolve the path using pattern matching
        var resolution = await _meshCatalog.ResolvePathAsync(path);

        if (resolution is null)
        {
            _context = null;
            CurrentNamespace = null;
            OnNavigationContextChanged?.Invoke(null);
            return;
        }

        // Parse remainder into area and id
        var (area, id) = ParseRemainder(resolution.Remainder);

        // Load the MeshNode for pre-rendered HTML and satellite detection
        var node = await LoadNodeWithPreRenderedHtmlAsync(resolution);

        // Create the navigation context
        var context = new NavigationContext
        {
            Path = path,
            Resolution = resolution,
            Area = area,
            Id = id,
            Node = node
        };

        _context = context;
        CurrentNamespace = context.Namespace;

        OnNavigationContextChanged?.Invoke(context);

        // Load creatable types in background when namespace changes
        var currentNodePath = context.Namespace ?? "";
        if (currentNodePath != _lastLoadedNodePath)
        {
            _ = LoadCreatableTypesAsync(currentNodePath);
        }
    }

    /// <summary>
    /// Loads the MeshNode for the resolved address.
    /// If the node has MarkdownContent but no PreRenderedHtml, generates it and persists back.
    /// </summary>
    private async Task<MeshNode?> LoadNodeWithPreRenderedHtmlAsync(AddressResolution resolution)
    {
        try
        {
            var address = (Address)resolution.Prefix;
            var node = await _meshCatalog.GetNodeAsync(address);
            if (node == null)
                return null;

            // If node has MarkdownContent but no PreRenderedHtml, generate it
            if (node.PreRenderedHtml == null && node.Content is MarkdownContent md && !string.IsNullOrEmpty(md.Content))
            {
                var html = md.PrerenderedHtml;
                if (html == null)
                {
                    // PrerenderedHtml not on the MarkdownContent either — generate it
                    var parsed = MarkdownContent.Parse(md.Content, node.Path);
                    html = parsed.PrerenderedHtml;
                }

                if (html != null)
                {
                    node = node with { PreRenderedHtml = html };

                    // Fire-and-forget: persist the generated PreRenderedHtml back
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var persistence = _hub.ServiceProvider.GetService<IPersistenceService>();
                            if (persistence != null)
                                await persistence.SaveNodeAsync(node);
                        }
                        catch (Exception ex)
                        {
                            var logger = _hub.ServiceProvider.GetService<ILogger<NavigationService>>();
                            logger?.LogWarning(ex, "Failed to persist PreRenderedHtml for {Path}", node.Path);
                        }
                    });
                }
            }

            return node;
        }
        catch
        {
            // Node loading is best-effort for prerender optimization
            return null;
        }
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
        _creatableTypes.Dispose();

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
