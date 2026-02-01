using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
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
    private List<CreatableTypeInfo> _creatableTypes = new();
    private string? _lastLoadedNodePath;
    private CancellationTokenSource? _loadingCts;
    private bool _isInitialized;
    private bool _isLoadingCreatableTypes;
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
    public IReadOnlyList<CreatableTypeInfo> CreatableTypes => _creatableTypes;

    /// <inheritdoc />
    public event Action<IReadOnlyList<CreatableTypeInfo>>? OnCreatableTypesChanged;

    /// <inheritdoc />
    public bool IsLoadingCreatableTypes => _isLoadingCreatableTypes;

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

        // Reload creatable types if namespace changed
        if (@namespace != _lastLoadedNodePath)
        {
            _ = LoadCreatableTypesAsync(@namespace ?? "");
        }
    }

    /// <inheritdoc />
    public void NavigateTo(string uri, bool forceLoad = false)
    {
        _navigationManager.NavigateTo(uri, forceLoad);
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

        // Create the navigation context
        var context = new NavigationContext
        {
            Path = path,
            Resolution = resolution,
            Area = area,
            Id = id
        };

        _context = context;
        CurrentNamespace = context.Namespace;

        OnNavigationContextChanged?.Invoke(context);

        // Check if node path changed and reload creatable types if needed
        var currentNodePath = context.Namespace;
        if (currentNodePath != _lastLoadedNodePath)
        {
            await LoadCreatableTypesAsync(currentNodePath);
        }
    }

    private static (string? Area, string? Id) ParseRemainder(string? remainder)
    {
        if (string.IsNullOrEmpty(remainder))
            return (null, null);

        var slashIndex = remainder.IndexOf('/');
        if (slashIndex >= 0)
            return (remainder.Substring(0, slashIndex), remainder.Substring(slashIndex + 1));

        return (remainder, null);
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
        _creatableTypes = new List<CreatableTypeInfo>();
        _isLoadingCreatableTypes = true;
        OnCreatableTypesChanged?.Invoke(_creatableTypes);

        try
        {
            await foreach (var typeInfo in nodeTypeService.GetCreatableTypesAsync(nodePath, ct).WithCancellation(ct))
            {
                _creatableTypes.Add(typeInfo);
                OnCreatableTypesChanged?.Invoke(_creatableTypes);
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
            _isLoadingCreatableTypes = false;
            OnCreatableTypesChanged?.Invoke(_creatableTypes);
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
