using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Configuration for the <b>NotificationFeaturePreference</b> node — one person's channel choice
/// for ONE notification feature (<see cref="NotificationFeaturePreference"/>), at
/// <see cref="NotificationFeaturePreferencePaths.PathFor"/>
/// (<c>{userId}/_Settings/Notifications/{feature}</c>). The Notifications settings tab binds the
/// standard node-content editor to one of these per feature, and
/// <see cref="NotificationService.Raise"/> reads it to decide which channels a notification of that
/// feature reaches.
///
/// <para>System-managed shape (excluded from search/create/autocomplete) but user-owned — it lives
/// in the person's own partition, so they edit it through the ordinary node stream.</para>
/// </summary>
public static class NotificationFeaturePreferenceNodeType
{
    /// <summary>The NodeType value used to identify per-feature preference nodes.</summary>
    public const string NodeType = "NotificationFeaturePreference";

    /// <summary>Registers the built-in NodeType and its content type on the mesh builder.</summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <returns>The builder.</returns>
    public static TBuilder AddNotificationFeaturePreferenceType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<NotificationFeaturePreference>(nameof(NotificationFeaturePreference)));
        return builder;
    }

    /// <summary>Creates the MeshNode definition for the NotificationFeaturePreference node type.</summary>
    /// <returns>The NodeType node.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Notification Channels",
        Icon = "/static/NodeTypeIcons/bell.svg",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<NotificationFeaturePreference>())
    };

    /// <summary>
    /// Create-on-absent (idempotent, reactive) of <paramref name="userId"/>'s preference node for
    /// <paramref name="feature"/>, seeded with the EFFECTIVE preference they have today
    /// (<see cref="NotificationChannelPreferences.Resolve"/> over their legacy settings) — so the
    /// editor the settings tab binds opens on exactly what the dispatcher was already doing, and
    /// creating the node changes no delivery. Existence is read with <c>GetQuery</c>
    /// (empty-on-absent), never a point read of a maybe-absent path. Emits the node path. Runs under
    /// the caller's identity (the person owns their own partition). An existing node is left as is.
    /// </summary>
    /// <param name="hub">The hub to run on.</param>
    /// <param name="userId">The person.</param>
    /// <param name="feature">The feature key.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>A cold observable of the node path.</returns>
    public static IObservable<string> EnsureExists(IMessageHub hub, string userId, string feature, ILogger? logger = null)
    {
        var path = NotificationFeaturePreferencePaths.PathFor(userId, feature);
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null || string.IsNullOrEmpty(userId))
            return Observable.Return(path);

        var workspace = hub.GetWorkspace();
        var legacyPath = NotificationSettingsPaths.PathFor(userId);
        var legacy = workspace
            .GetQuery($"{NotificationSettingsNodeType.NodeType}|{legacyPath}",
                $"path:{legacyPath} nodeType:{NotificationSettingsNodeType.NodeType} select:path,id,namespace,name,nodeType,content")
            .Take(1)
            .Select(nodes => nodes
                .Select(n => n.ContentAs<NotificationSettings>(hub.JsonSerializerOptions))
                .FirstOrDefault(s => s is not null));

        return workspace
            .GetQuery($"{NodeType}|{path}", $"path:{path} nodeType:{NodeType} select:path,id,namespace,name,nodeType,content")
            .Take(1)
            .SelectMany(nodes =>
            {
                if (nodes.Any(n => string.Equals(n.NodeType, NodeType, StringComparison.OrdinalIgnoreCase)))
                    return Observable.Return(path);
                return legacy.SelectMany(settings =>
                {
                    logger?.LogInformation("Seeding the {Feature} notification channels for {User}", feature, userId);
                    var node = new MeshNode(
                        NotificationFeaturePreferencePaths.IdFor(feature),
                        NotificationFeaturePreferencePaths.NamespaceFor(userId))
                    {
                        NodeType = NodeType,
                        Name = feature,
                        State = MeshNodeState.Active,
                        Content = NotificationChannelPreferences.Resolve(feature, null, settings),
                    };
                    return meshService.CreateNode(node)
                        .Select(_ => path)
                        // Idempotent: a concurrent first writer won the create race.
                        .Catch<string, Exception>(ex => IsAlreadyExists(ex)
                            ? Observable.Return(path)
                            : Observable.Throw<string>(ex));
                });
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
