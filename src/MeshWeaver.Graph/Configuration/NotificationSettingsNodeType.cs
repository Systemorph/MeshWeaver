using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Configuration for the <b>NotificationSettings</b> node — the per-user, deterministic delivery
/// preferences (per <see cref="NotificationCategory"/>: in-app bell and/or email) that back the
/// Notifications settings tab. One singleton node per user at
/// <see cref="NotificationSettingsPaths.PathFor"/> (<c>{userId}/_Settings/Notifications</c>).
///
/// <para>System-managed shape (excluded from search/create/autocomplete) but user-owned — the
/// settings tab data-binds to it and writes per-field via the node stream. Complements the advanced
/// AI-triage layer (<see cref="NotificationRule"/> / <see cref="NotificationChannel"/>): the
/// deterministic email path defers to triage for users who authored routing rules.</para>
/// </summary>
public static class NotificationSettingsNodeType
{
    /// <summary>The NodeType value used to identify notification-settings nodes.</summary>
    public const string NodeType = "NotificationSettings";

    /// <summary>Registers the built-in "NotificationSettings" MeshNode on the mesh builder.</summary>
    public static TBuilder AddNotificationSettingsType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<NotificationSettings>(nameof(NotificationSettings)));
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the NotificationSettings node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Notification Settings",
        Icon = "/static/NodeTypeIcons/bell.svg",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<NotificationSettings>())
    };

    /// <summary>
    /// Create-on-absent (idempotent, reactive) of the <paramref name="userId"/>'s
    /// <c>{userId}/_Settings/Notifications</c> node with default preferences, so the settings editor
    /// binds to an existing node. Existence is read via <c>GetQuery</c> (empty-on-absent) — never a
    /// point <c>GetMeshNodeStream</c> probe of a maybe-absent path. Emits the node path. Runs under
    /// the caller's identity (the user owns their own partition). An existing node is left untouched.
    /// </summary>
    public static IObservable<string> EnsureExists(IMessageHub hub, string userId, ILogger? logger = null)
    {
        var path = NotificationSettingsPaths.PathFor(userId);
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null || string.IsNullOrEmpty(userId))
            return Observable.Return(path);

        MeshNode BuildNode() => new(NotificationSettingsPaths.NodeId, NotificationSettingsPaths.NamespaceFor(userId))
        {
            NodeType = NodeType,
            Name = "Notification Settings",
            State = MeshNodeState.Active,
            Content = new NotificationSettings(),
        };

        return hub.GetWorkspace()
            .GetQuery($"{NodeType}|{path}", $"path:{path} nodeType:{NodeType}")
            .Take(1)
            .SelectMany(nodes =>
            {
                var existing = nodes.FirstOrDefault(n =>
                    string.Equals(n.NodeType, NodeType, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                    return Observable.Return(path);
                logger?.LogInformation("Seeding notification settings for {User}", userId);
                return meshService.CreateNode(BuildNode())
                    .Select(_ => path)
                    // Idempotent: a concurrent first-writer won the create race.
                    .Catch<string, Exception>(ex => IsAlreadyExists(ex)
                        ? Observable.Return(path)
                        : Observable.Throw<string>(ex));
            });
    }

    /// <summary>Parses the settings content from a node (typed or raw <see cref="JsonElement"/>);
    /// returns defaults when absent/unparseable.</summary>
    public static NotificationSettings Parse(MeshNode? node, JsonSerializerOptions options) =>
        node?.Content switch
        {
            NotificationSettings c => c,
            JsonElement je => TryDeserialize(je, options) ?? new NotificationSettings(),
            _ => new NotificationSettings(),
        };

    private static NotificationSettings? TryDeserialize(JsonElement je, JsonSerializerOptions options)
    {
        try { return JsonSerializer.Deserialize<NotificationSettings>(je.GetRawText(), options); }
        catch { return null; }
    }

    private static bool IsAlreadyExists(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.Message?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        return false;
    }
}
