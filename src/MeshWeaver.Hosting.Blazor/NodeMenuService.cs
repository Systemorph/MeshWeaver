using System.Reactive.Subjects;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// Provides context-aware menu state for the current navigation node.
/// Subscribes to NavigationService for context changes and resolves
/// satellite nodes to their primary node for permission checks.
/// </summary>
public class NodeMenuService : INodeMenuService
{
    private readonly INavigationService _navigationService;
    private readonly IMessageHub _hub;
    private readonly BehaviorSubject<NodeMenuState> _state = new(NodeMenuState.Empty);
    private IDisposable? _creatableTypesSubscription;
    private CreatableTypesSnapshot _latestCreatableTypes = CreatableTypesSnapshot.Empty;
    private bool _disposed;

    public NodeMenuService(INavigationService navigationService, IMessageHub hub)
    {
        _navigationService = navigationService;
        _hub = hub;

        _navigationService.OnNavigationContextChanged += OnNavigationContextChanged;

        _creatableTypesSubscription = _navigationService.CreatableTypes.Subscribe(snapshot =>
        {
            _latestCreatableTypes = snapshot;
            UpdateStateWithCreatableTypes(snapshot);
        });

        // Compute initial state from current context (may already be set)
        var currentContext = _navigationService.Context;
        if (currentContext != null)
            _ = ComputeStateAsync(currentContext);
    }

    /// <inheritdoc />
    public IObservable<NodeMenuState> State => _state;

    /// <inheritdoc />
    public void Refresh()
    {
        _navigationService.RefreshCreatableTypes();
        _ = RefreshPermissionsAsync();
    }

    private void OnNavigationContextChanged(NavigationContext? context)
    {
        _ = ComputeStateAsync(context);
    }

    private async Task ComputeStateAsync(NavigationContext? context)
    {
        if (context == null)
        {
            _state.OnNext(NodeMenuState.Empty);
            return;
        }

        var nodePath = context.Namespace;
        var primaryPath = context.PrimaryPath;
        var isSatellite = context.IsSatellite;

        var permissions = await PermissionHelper.GetEffectivePermissionsAsync(_hub, primaryPath);

        var newState = new NodeMenuState
        {
            NodePath = nodePath,
            PrimaryPath = primaryPath,
            IsSatellite = isSatellite,
            Permissions = permissions,
            NodeType = context.Node?.NodeType,
            CreatableTypes = _latestCreatableTypes
        };

        _state.OnNext(newState);
    }

    private async Task RefreshPermissionsAsync()
    {
        var context = _navigationService.Context;
        if (context == null)
            return;

        var primaryPath = context.PrimaryPath;
        var permissions = await PermissionHelper.GetEffectivePermissionsAsync(_hub, primaryPath);

        var current = _state.Value;
        _state.OnNext(current with { Permissions = permissions });
    }

    private void UpdateStateWithCreatableTypes(CreatableTypesSnapshot snapshot)
    {
        var current = _state.Value;
        _state.OnNext(current with { CreatableTypes = snapshot });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _navigationService.OnNavigationContextChanged -= OnNavigationContextChanged;
        _creatableTypesSubscription?.Dispose();
        _state.Dispose();
    }
}
