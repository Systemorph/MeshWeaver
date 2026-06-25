using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Memex.Client.Pages;
using Memex.Client.Services;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace Memex.Client.Prefs;

/// <summary>
/// The reactive, layered application-preferences service. Resolves a single
/// <see cref="ResolvedAppPreferences"/> from a four-layer cascade — Device → User → Space → System
/// (highest priority first) — and lets callers write an override at any chosen scope.
/// <list type="bullet">
///   <item><b>Device</b>: MAUI <see cref="Preferences"/> (local, never synced), pushed through a
///     <see cref="BehaviorSubject{T}"/>.</item>
///   <item><b>User / Space / System</b>: mesh nodes read live via
///     <c>hub.GetMeshNodeStream(path)</c> and written via create-or-update.</item>
/// </list>
/// </summary>
public interface IPreferencesService
{
    /// <summary>
    /// The live resolved preferences — re-emits whenever ANY layer changes. The cascade applies
    /// hard-coded defaults at the end, so this always carries a fully-populated value.
    /// </summary>
    IObservable<ResolvedAppPreferences> Resolved { get; }

    /// <summary>The current device-layer overrides (synchronous snapshot of the device subject).</summary>
    AppPreferences Device { get; }

    /// <summary>Applies <paramref name="mutate"/> to the override at <paramref name="scope"/> and persists it.</summary>
    void Set(PreferenceScope scope, Func<AppPreferences, AppPreferences> mutate);

    /// <summary>Convenience: set (or clear, when <paramref name="zoomLevel"/> is null) the zoom override at <paramref name="scope"/>.</summary>
    void SetZoomLevel(PreferenceScope scope, double? zoomLevel);

    /// <summary>
    /// Reactively applies the resolved <see cref="ResolvedAppPreferences.ZoomLevel"/> as a global UI
    /// scale on <paramref name="page"/>'s root content (top-left anchored). Returns the subscription —
    /// dispose it (e.g. on page disappearing) to stop applying. This is the shell seam: the navigator
    /// page calls <c>ApplyTo(this)</c> once to zoom the whole app.
    /// </summary>
    IDisposable ApplyTo(Page page);
}

/// <summary>
/// Mesh-scoped singleton implementation of <see cref="IPreferencesService"/>. Holds the device-layer
/// state as an instance <see cref="BehaviorSubject{T}"/> (no static state) and composes the four layers
/// with <c>CombineLatest</c>. Everything is <see cref="IObservable{T}"/> — no async/await.
/// </summary>
public sealed class PreferencesService : IPreferencesService
{
    /// <summary>Stable MAUI <see cref="Preferences"/> key for the JSON-serialized device layer.</summary>
    private const string DeviceKey = "app.preferences.device";

    private readonly IMessageHub _hub;
    private readonly IMeshService _meshService;
    private readonly AccessService _accessService;
    private readonly ILogger? _logger;

    private readonly BehaviorSubject<AppPreferences> _device;
    private readonly IObservable<ResolvedAppPreferences> _resolved;

    /// <summary>This device user's id — keys the user-layer partition (e.g. <c>device-user</c>).</summary>
    private readonly string _userId;

    /// <summary>
    /// The active space path for the space layer, or <c>null</c> when no space is selected. The local
    /// single-user client has no active-space concept yet, so this is null and the space layer
    /// contributes no override — the framework still resolves Space the moment a path is supplied.
    /// </summary>
    private readonly string? _spacePath;

    public PreferencesService(IMessageHub hub)
    {
        _hub = hub;
        _meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        _accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        _logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<PreferencesService>();

        var ctx = _accessService.Context ?? _accessService.CircuitContext;
        _userId = string.IsNullOrEmpty(ctx?.ObjectId) ? DeviceOnboarding.DeviceUserId : ctx.ObjectId;
        _spacePath = null;

        _device = new BehaviorSubject<AppPreferences>(LoadDevice());

        // The four-layer cascade. Each mesh layer StartWith(null) so CombineLatest fires immediately
        // (an absent node otherwise never emits → Resolved would never produce a value). The device
        // subject always carries a value. Resolution walks Device → User → Space → System.
        _resolved = Observable.CombineLatest(
                _device.Select(p => (AppPreferences?)p),
                MeshLayer(AppPreferencesNodeType.UserPath(_userId)),
                MeshLayer(_spacePath is null ? null : AppPreferencesNodeType.SpacePath(_spacePath)),
                MeshLayer(AppPreferencesNodeType.SystemPath),
                Resolve)
            .DistinctUntilChanged()
            .Replay(1)
            .RefCount();

        // Establish the platform-default (system) node if absent — a genuine system write
        // (ImpersonateAsSystem), exactly like DeviceOnboarding seeds the partition root.
        SeedSystemDefaults();
    }

    /// <inheritdoc/>
    public IObservable<ResolvedAppPreferences> Resolved => _resolved;

    /// <inheritdoc/>
    public AppPreferences Device => _device.Value;

    /// <inheritdoc/>
    public void SetZoomLevel(PreferenceScope scope, double? zoomLevel) =>
        Set(scope, p => p with { ZoomLevel = zoomLevel });

    /// <inheritdoc/>
    public void Set(PreferenceScope scope, Func<AppPreferences, AppPreferences> mutate)
    {
        switch (scope)
        {
            case PreferenceScope.Device:
                var updated = mutate(Device);
                Preferences.Default.Set(DeviceKey, JsonSerializer.Serialize(updated));
                _device.OnNext(updated);
                break;

            case PreferenceScope.User:
                WriteMeshLayer(AppPreferencesNodeType.UserPath(_userId), mutate, asSystem: false);
                break;

            case PreferenceScope.Space:
                if (string.IsNullOrEmpty(_spacePath))
                {
                    _logger?.LogWarning("Set(Space) ignored — no active space is selected.");
                    return;
                }
                WriteMeshLayer(AppPreferencesNodeType.SpacePath(_spacePath), mutate, asSystem: false);
                break;

            case PreferenceScope.System:
                // Platform-wide write — run as system (Permission.All), like the seed.
                WriteMeshLayer(AppPreferencesNodeType.SystemPath, mutate, asSystem: true);
                break;
        }
    }

    /// <inheritdoc/>
    public IDisposable ApplyTo(Page page) =>
        _resolved
            .Select(r => r.ZoomLevel)
            .DistinctUntilChanged()
            .Subscribe(
                zoom => MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (page is ContentPage { Content: { } root })
                    {
                        // Anchor top-left so the page grows from the corner, not the centre.
                        root.AnchorX = 0;
                        root.AnchorY = 0;
                        root.Scale = zoom;
                    }
                }),
                ex => _logger?.LogWarning(ex, "Applying zoom to page failed"));

    // ── cascade ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A single mesh layer as an <c>AppPreferences?</c> stream. Null path (e.g. no active space) →
    /// a constant null. Otherwise the live node content, prefixed with null so CombineLatest fires
    /// before/without the node, and guarded so a read fault degrades to "no override" not a dead stream.
    /// </summary>
    private IObservable<AppPreferences?> MeshLayer(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return Observable.Return<AppPreferences?>(null);

        return _hub.GetMeshNodeStream(path)
            .Select(node => AsPrefs(node.Content))
            .StartWith((AppPreferences?)null)
            .Catch<AppPreferences?, Exception>(ex =>
            {
                _logger?.LogWarning(ex, "Preferences layer read failed for {Path}", path);
                return Observable.Return<AppPreferences?>(null);
            });
    }

    /// <summary>Device → User → Space → System: first non-null wins; defaults + clamp applied last.</summary>
    private static ResolvedAppPreferences Resolve(
        AppPreferences? device, AppPreferences? user, AppPreferences? space, AppPreferences? system)
    {
        var zoom = device?.ZoomLevel
                   ?? user?.ZoomLevel
                   ?? space?.ZoomLevel
                   ?? system?.ZoomLevel
                   ?? AppPreferences.DefaultZoom;
        return new ResolvedAppPreferences
        {
            ZoomLevel = Math.Clamp(zoom, AppPreferences.MinZoom, AppPreferences.MaxZoom),
        };
    }

    // ── writes ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Create-or-update the singleton preferences node at <paramref name="path"/>. Existence is probed
    /// via the SAME cached <c>GetQuery</c> the reads use (empty-on-absent — never a point-read on a
    /// missing node, which would routing-NotFound-storm); a present node is patched via
    /// <c>GetMeshNodeStream(path).Update</c>, an absent one is created via the node-lifecycle
    /// <c>CreateNode</c>. System writes run inside <see cref="AccessService.ImpersonateAsSystem"/>.
    /// </summary>
    private void WriteMeshLayer(string path, Func<AppPreferences, AppPreferences> mutate, bool asSystem)
    {
        var nodeType = AppPreferencesNodeType.NodeType;

        IObservable<MeshNode> Write(MeshNode? existing) =>
            existing is null
                ? _meshService.CreateNode(MeshNode.FromPath(path) with
                {
                    NodeType = nodeType,
                    Name = "App Preferences",
                    Content = mutate(new AppPreferences()),
                })
                : _hub.GetMeshNodeStream(path).Update(cur =>
                    cur with { Content = mutate(AsPrefs(cur.Content) ?? new AppPreferences()) });

        // Establish the system identity synchronously, immediately before the write is built and
        // subscribed (matching DeviceOnboarding's Observable.Using shape, so the write captures it).
        IObservable<MeshNode> Scoped(MeshNode? existing) =>
            asSystem
                ? Observable.Using(() => _accessService.ImpersonateAsSystem(), _ => Write(existing))
                : Write(existing);

        _hub.GetWorkspace()
            .GetQuery($"{nodeType}|{path}", $"path:{path} nodeType:{nodeType}")
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(15))
            .SelectMany(nodes => Scoped(nodes.FirstOrDefault(n =>
                string.Equals(n.NodeType, nodeType, StringComparison.OrdinalIgnoreCase))))
            .Subscribe(
                _ => { },
                ex => _logger?.LogWarning(ex, "Set preference failed for {Path}", path));
    }

    /// <summary>Create-on-absent platform default (empty AppPreferences) in the Admin partition, as system.</summary>
    private void SeedSystemDefaults()
    {
        var nodeType = AppPreferencesNodeType.NodeType;
        var path = AppPreferencesNodeType.SystemPath;

        _hub.GetWorkspace()
            .GetQuery($"{nodeType}|{path}", $"path:{path} nodeType:{nodeType}")
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(15))
            .Where(nodes => !nodes.Any(n =>
                string.Equals(n.NodeType, nodeType, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(_ => Observable.Using(
                () => _accessService.ImpersonateAsSystem(),
                _ => _meshService.CreateNode(MeshNode.FromPath(path) with
                {
                    NodeType = nodeType,
                    Name = "App Preferences",
                    Content = new AppPreferences(), // empty — establishes the node; overrides nothing yet
                })))
            .Subscribe(
                _ => { },
                ex => _logger?.LogWarning(ex, "Seeding system preferences failed for {Path}", path));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static AppPreferences LoadDevice()
    {
        var json = Preferences.Default.Get(DeviceKey, string.Empty);
        if (string.IsNullOrEmpty(json))
            return new AppPreferences();
        try
        {
            return JsonSerializer.Deserialize<AppPreferences>(json) ?? new AppPreferences();
        }
        catch
        {
            return new AppPreferences();
        }
    }

    /// <summary>
    /// Coerces a node's <c>Content</c> to <see cref="AppPreferences"/>. <c>GetMeshNodeStream</c> already
    /// types content from the TypeRegistry, so the typed branch is the norm; the JsonElement branch is a
    /// belt-and-braces fallback.
    /// </summary>
    private static AppPreferences? AsPrefs(object? content) => content switch
    {
        AppPreferences p => p,
        JsonElement je => TryDeserialize(je),
        _ => null,
    };

    private static AppPreferences? TryDeserialize(JsonElement je)
    {
        try { return je.Deserialize<AppPreferences>(); }
        catch { return null; }
    }
}

/// <summary>
/// One-call registration for the layered preferences framework: the <c>AppPreferences</c> mesh-node
/// type + content-type, the mesh-scoped <see cref="IPreferencesService"/> singleton, and the native
/// <see cref="SettingsPage"/>. Call once from <c>MauiProgram</c>'s mesh-builder chain.
/// </summary>
public static class PreferencesRegistration
{
    /// <summary>Registers everything the preferences framework needs on the mesh builder.</summary>
    public static TBuilder AddPreferences<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        // The AppPreferences node type (the synced layers' storage) + global TypeRegistry entry so the
        // content round-trips across hubs.
        builder.AddMeshNodes(AppPreferencesNodeType.CreateMeshNode());
        builder.ConfigureHub(config => config.WithType<AppPreferences>(nameof(AppPreferences)));

        // Mesh-scoped singletons: the service (its lifetime IS the mesh) + the settings page.
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IPreferencesService, PreferencesService>();
            services.AddTransient<SettingsPage>();
            return services;
        });
        return builder;
    }
}
