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

    private static readonly TimeSpan ReadBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reads ONE settings node, keeping ABSENT and UNREADABLE apart
    /// (<see cref="PreferenceRead{T}"/>). Existence comes from a synced query (empty-on-absent —
    /// these nodes usually do NOT exist, and a point read of an absent path NotFound-storms the
    /// owner's partition hub); the CONTENT of a node that exists comes from its authoritative
    /// <c>GetMeshNodeStream</c>, never from the query, whose snapshot can trail a write the person
    /// just made. A timeout, a fault or content that will not type is <c>Unreadable</c>, never
    /// <c>Absent</c>.
    /// </summary>
    /// <typeparam name="T">The node's content type.</typeparam>
    /// <param name="hub">The hub to read on.</param>
    /// <param name="path">The node path.</param>
    /// <param name="nodeType">The node's NodeType (narrows the existence query).</param>
    /// <returns>A cold observable of one read.</returns>
    internal static IObservable<PreferenceRead<T>> ReadAuthoritative<T>(IMessageHub hub, string path, string nodeType)
        where T : class
    {
        var workspace = hub.GetWorkspace();
        return workspace
            .GetQuery($"{nodeType}|exists|{path}", $"path:{path} nodeType:{nodeType} select:path,id,nodeType")
            .Take(1)
            .Timeout(ReadBound)
            .SelectMany(nodes => nodes.Any(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase))
                ? workspace.GetMeshNodeStream(path)
                    .Take(1)
                    .Timeout(ReadBound)
                    .Select(node => node?.ContentAs<T>(hub.JsonSerializerOptions) is { } value
                        ? PreferenceRead<T>.Found(value)
                        : PreferenceRead<T>.Unreadable($"'{path}' exists but its content did not read as {typeof(T).Name}"))
                    .DefaultIfEmpty(PreferenceRead<T>.Unreadable($"'{path}' exists but its stream answered nothing"))
                : Observable.Return(PreferenceRead<T>.Absent()))
            .DefaultIfEmpty(PreferenceRead<T>.Unreadable($"the existence query for '{path}' answered nothing"))
            .Catch((Exception ex) => Observable.Return(
                PreferenceRead<T>.Unreadable($"'{path}' could not be read — {ex.GetType().Name}: {ex.Message}")));
    }

    /// <summary>
    /// Create-on-absent (idempotent, reactive) of <paramref name="userId"/>'s preference node for
    /// <paramref name="feature"/>, seeded with the EFFECTIVE preference they have today
    /// (<see cref="NotificationChannelPreferences.Resolve"/> over their legacy settings, read from
    /// the legacy node's AUTHORITATIVE stream) — so the editor the settings tab binds opens on
    /// exactly what the dispatcher was already doing, and creating the node changes no delivery.
    /// Existence is read with <c>GetQuery</c> (empty-on-absent), never a point read of a
    /// maybe-absent path. Emits the node path. Runs under the caller's identity (the person owns
    /// their own partition). An existing node is left as is.
    ///
    /// <para>🚨 A legacy node that exists but cannot be read FAILS the seed rather than seeding
    /// defaults: the explicit node is authoritative once written, so a seed from a value we could
    /// not see would permanently replace the person's legacy choice.</para>
    /// </summary>
    /// <param name="hub">The hub to run on.</param>
    /// <param name="userId">The person.</param>
    /// <param name="feature">The feature key — must be <see cref="NotificationFeatures.IsValidKey"/>.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>A cold observable of the node path.</returns>
    public static IObservable<string> EnsureExists(IMessageHub hub, string userId, string feature, ILogger? logger = null)
    {
        if (!NotificationFeatures.IsValidKey(feature))
            return Observable.Throw<string>(new ArgumentException(
                $"'{feature}' is not a notification feature key (^[a-z][a-zA-Z0-9]*$)", nameof(feature)));
        var path = NotificationFeaturePreferencePaths.PathFor(userId, feature);
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
                return ReadAuthoritative<NotificationSettings>(
                        hub, NotificationSettingsPaths.PathFor(userId), NotificationSettingsNodeType.NodeType)
                    .SelectMany(legacy =>
                    {
                        if (legacy.IsUnreadable)
                            return Observable.Throw<string>(new InvalidOperationException(
                                $"not seeding the {feature} notification channels for {userId}: {legacy.Reason}"));
                        logger?.LogInformation("Seeding the {Feature} notification channels for {User}", feature, userId);
                        var node = new MeshNode(feature, NotificationFeaturePreferencePaths.NamespaceFor(userId))
                        {
                            NodeType = NodeType,
                            Name = feature,
                            State = MeshNodeState.Active,
                            Content = NotificationChannelPreferences.Resolve(feature, null, legacy.Value),
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
