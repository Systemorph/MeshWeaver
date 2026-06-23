using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using NavigationOptions = MeshWeaver.Mesh.Services.NavigationOptions;

namespace Memex.Client.Services;

/// <summary>
/// Minimal Hybrid <see cref="INavigationService"/> for the in-process portal. The full server impl
/// (<c>MeshWeaver.Hosting.Blazor.NavigationService</c>) lives in an assembly that takes a framework
/// reference to <c>Microsoft.AspNetCore.App</c>, which a MAUI app cannot reference — so this strips it
/// to the essentials: read the path off <see cref="NavigationManager"/>, resolve it to a node + area
/// via <see cref="IPathResolver"/>, and publish the resulting <see cref="NavigationContext"/>. It drops
/// activity-tracking, creatable-types loading, the anonymous read-gate, prerender caching, and satellite
/// redirects — none of which a single-user local mesh needs to render a node.
/// </summary>
internal sealed class HybridNavigationService : INavigationService
{
    private readonly NavigationManager _nav;
    private readonly IPathResolver _pathResolver;
    private readonly ReplaySubject<string> _path = new(bufferSize: 1);
    private readonly ReplaySubject<NavigationContext?> _context = new(bufferSize: 1);
    private readonly BehaviorSubject<NavigationStatus> _status = new(NavigationStatus.Idle());
    private readonly BehaviorSubject<CreatableTypesSnapshot> _creatable = new(CreatableTypesSnapshot.Empty);
    private readonly IDisposable _pathSubscription;
    private IDisposable? _resolveSubscription;
    private bool _initialized;
    private bool _disposed;

    public HybridNavigationService(NavigationManager nav, IPathResolver pathResolver)
    {
        _nav = nav;
        _pathResolver = pathResolver;
        // Pure-Rx wiring only — NO NavigationManager touch until Initialize() (inside a circuit).
        _pathSubscription = _path.DistinctUntilChanged().Subscribe(Resolve);
    }

    public IObservable<string> Path => _path;
    public string? CurrentNamespace { get; private set; }
    public IObservable<NavigationContext?> NavigationContext => _context;
    public NavigationContext? Context { get; private set; }
    public bool IsResolving { get; private set; }
    public IObservable<NavigationStatus> Status => _status;
    public IObservable<CreatableTypesSnapshot> CreatableTypes => _creatable;
    public event Action<string>? SidePanelNavigationRequested;

    public void RefreshCreatableTypes() { }

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        _nav.LocationChanged += OnLocationChanged;
        _path.OnNext(_nav.ToBaseRelativePath(_nav.Uri));
    }

    private void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
        => _path.OnNext(_nav.ToBaseRelativePath(e.Location));

    public void SetCurrentNamespace(string? @namespace) => CurrentNamespace = @namespace;

    public void NavigateTo(NavigationOptions options)
    {
        if (options.Target == "SidePanel")
            SidePanelNavigationRequested?.Invoke(options.Uri.TrimStart('/'));
        else
            _nav.NavigateTo(options.Uri, options.ForceLoad, options.Replace);
    }

    public string GenerateHref(string address, string? area, string? areaId)
        => NavigationServiceExtensions.DefaultGenerateHref(address, area, areaId);

    public string GenerateContentUrl(string address, string path)
        => NavigationServiceExtensions.DefaultGenerateContentUrl(address, path);

    public string ResolveRelativePath(string relativePath)
        => NavigationServiceExtensions.DefaultResolveRelativePath(CurrentNamespace, relativePath);

    private void Resolve(string rawPath)
    {
        // A mesh node address is always the bare path (never a query string) — see "Mesh URL Shape".
        var route = rawPath.Split('?', 2)[0];
        IsResolving = true;
        _status.OnNext(NavigationStatus.LookingUp(route));

        _resolveSubscription?.Dispose();
        _resolveSubscription = _pathResolver.ResolvePath(route)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(15))
            .Subscribe(
                resolution =>
                {
                    if (resolution is null)
                    {
                        SettleNotFound(route);
                        return;
                    }
                    var (area, id) = LayoutAreaMarkdownParser.ParseAreaAndId(resolution.Remainder);
                    var context = new NavigationContext
                    {
                        Path = route,
                        Args = ImmutableDictionary<string, string>.Empty,
                        Resolution = resolution,
                        Area = area,
                        Id = id,
                        Node = resolution.Node,
                    };
                    Context = context;
                    CurrentNamespace = context.PrimaryPath;
                    IsResolving = false;
                    _context.OnNext(context);
                    _status.OnNext(NavigationStatus.Ready(resolution.Prefix));
                },
                _ => SettleNotFound(route));
    }

    private void SettleNotFound(string route)
    {
        IsResolving = false;
        Context = null;
        CurrentNamespace = null;
        _context.OnNext(null);
        _status.OnNext(NavigationStatus.NotFound(route));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _resolveSubscription?.Dispose();
        _pathSubscription.Dispose();
        _path.Dispose();
        _context.Dispose();
        _status.Dispose();
        _creatable.Dispose();
        try { _nav.LocationChanged -= OnLocationChanged; }
        catch (InvalidOperationException) { /* never initialized */ }
    }
}
