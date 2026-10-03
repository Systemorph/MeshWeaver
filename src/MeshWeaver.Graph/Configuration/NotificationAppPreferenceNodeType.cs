using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Configuration for the <b>NotificationAppPreference</b> node — one person's notification choice for
/// ONE app (<see cref="NotificationAppPreference"/>), at <see cref="NotificationAppPreferencePaths.PathFor"/>
/// (<c>{userId}/_Settings/Notifications/Apps/{appId}</c>). The Notifications settings tab lists one
/// per installed app (iOS <i>Settings → Notifications</i>), and <see cref="NotificationService.Raise"/>
/// reads it to gate what the per-feature preference would deliver.
///
/// <para>System-managed shape (excluded from search/create/autocomplete) but user-owned, like
/// <see cref="NotificationFeaturePreferenceNodeType"/>.</para>
/// </summary>
public static class NotificationAppPreferenceNodeType
{
    /// <summary>The NodeType value used to identify per-app preference nodes.</summary>
    public const string NodeType = "NotificationAppPreference";

    /// <summary>Registers the built-in NodeType and its content type on the mesh builder.</summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <returns>The builder.</returns>
    public static TBuilder AddNotificationAppPreferenceType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<NotificationAppPreference>(nameof(NotificationAppPreference)));
        return builder;
    }

    /// <summary>Creates the MeshNode definition for the NotificationAppPreference node type.</summary>
    /// <returns>The NodeType node.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "App Notifications",
        Icon = "/static/NodeTypeIcons/bell.svg",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<NotificationAppPreference>())
    };

    private static readonly TimeSpan ReadBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The person's installed apps — ONE single-partition query over <c>{userId}/_App</c>, the same
    /// read the home's Apps grid makes. Unreadable (timeout, fault) is reported as null, never as
    /// "no apps": attributing nothing would lift the app gate on a choice we could not see.
    /// </summary>
    /// <param name="hub">The hub to read on.</param>
    /// <param name="userId">The person.</param>
    /// <returns>A cold observable of the apps, or null when the read failed.</returns>
    internal static IObservable<IReadOnlyCollection<InstalledAppRef>?> ReadInstalledApps(IMessageHub hub, string userId)
        => hub.GetWorkspace()
            .GetQuery($"{NodeType}|apps|{userId}",
                $"path:{userId}/{AppNodeType.UserNamespace} scope:children nodeType:{AppNodeType.NodeType}")
            .Take(1)
            .Timeout(ReadBound)
            .Select(nodes => (IReadOnlyCollection<InstalledAppRef>?)(nodes ?? [])
                .Select(n => new InstalledAppRef(n.Id, n.ContentAs<App>(hub.JsonSerializerOptions)?.Plugin ?? string.Empty))
                .Where(a => NotificationApps.IsValidKey(a.Id))
                .ToList())
            .DefaultIfEmpty(null)
            .Catch((Exception _) => Observable.Return<IReadOnlyCollection<InstalledAppRef>?>(null));

    /// <summary>
    /// Reads the person's preference for <paramref name="app"/>, ABSENT and UNREADABLE kept apart
    /// (<see cref="NotificationFeaturePreferenceNodeType.ReadAuthoritative{T}"/>).
    /// </summary>
    /// <param name="hub">The hub to read on.</param>
    /// <param name="userId">The person.</param>
    /// <param name="app">The app id.</param>
    /// <returns>A cold observable of one read.</returns>
    internal static IObservable<PreferenceRead<NotificationAppPreference>> Read(IMessageHub hub, string userId, string app)
        => NotificationApps.IsValidKey(app)
            ? NotificationFeaturePreferenceNodeType.ReadAuthoritative<NotificationAppPreference>(
                hub, NotificationAppPreferencePaths.PathFor(userId, app), NodeType)
            : Observable.Return(PreferenceRead<NotificationAppPreference>.Unreadable($"'{app}' is not an app key"));

    /// <summary>
    /// Create-on-absent (idempotent, reactive) of <paramref name="userId"/>'s preference node for
    /// <paramref name="app"/>, seeded with the DEFAULT — which is exactly what the dispatcher does for
    /// an app with no node (allowed, delivering quietly), so opening the settings tab changes no
    /// delivery. Emits the node path. Runs under the caller's identity (the person owns their own
    /// partition). An existing node is left as is.
    /// </summary>
    /// <param name="hub">The hub to run on.</param>
    /// <param name="userId">The person.</param>
    /// <param name="app">The app id — must be <see cref="NotificationApps.IsValidKey"/>.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>A cold observable of the node path.</returns>
    public static IObservable<string> EnsureExists(IMessageHub hub, string userId, string app, ILogger? logger = null)
    {
        if (!NotificationApps.IsValidKey(app))
            return Observable.Throw<string>(new ArgumentException(
                $"'{app}' is not a notification app key (^[A-Za-z0-9][A-Za-z0-9._-]*$)", nameof(app)));
        var path = NotificationAppPreferencePaths.PathFor(userId, app);
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null || string.IsNullOrEmpty(userId))
            return Observable.Return(path);

        return hub.GetWorkspace()
            .GetQuery($"{NodeType}|{path}", $"path:{path} nodeType:{NodeType} select:path,id,namespace,name,nodeType")
            .Take(1)
            .SelectMany(nodes =>
            {
                if (nodes.Any(n => string.Equals(n.NodeType, NodeType, StringComparison.OrdinalIgnoreCase)))
                    return Observable.Return(path);
                logger?.LogInformation("Seeding the {App} app notification preference for {User}", app, userId);
                var node = new MeshNode(app, NotificationAppPreferencePaths.NamespaceFor(userId))
                {
                    NodeType = NodeType,
                    Name = app,
                    State = MeshNodeState.Active,
                    Content = new NotificationAppPreference { App = app },
                };
                return meshService.CreateNode(node)
                    .Select(_ => path)
                    // Idempotent: a concurrent first writer won the create race.
                    .Catch<string, Exception>(ex => IsAlreadyExists(ex)
                        ? Observable.Return(path)
                        : Observable.Throw<string>(ex));
            });
    }

    private static bool IsAlreadyExists(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.Message?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        return false;
    }
}
