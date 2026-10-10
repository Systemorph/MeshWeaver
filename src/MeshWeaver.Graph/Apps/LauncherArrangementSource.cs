using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Apps;

/// <summary>
/// Reads a viewer's launcher ARRANGEMENT (<see cref="LauncherArrangement"/> at
/// <c>{user}/_Settings/Launcher</c>) live, and seeds it ONCE from the viewer's legacy
/// <c>{user}/_App</c> records the first time the directory renders for them.
/// <para>🚨 <b>The seed never feeds on its own writes.</b> Its trigger is "the arrangement node is
/// ABSENT", and its one write makes it present; on top of that, the seed is attempted at most once
/// per viewer per process (<see cref="TrySeed"/>), so a stale negative from the index cannot repeat
/// it, and the create is create-if-absent ("already exists" is the benign outcome of a race), so
/// it cannot overwrite an arrangement the viewer already made.</para>
/// <para>Existence is read with a <c>namespace:</c> LISTING, never a point read of the possibly
/// absent node (a point read of an absent path opens the storm-breaker on it — AGENTS.md CQRS).</para>
/// </summary>
public sealed class LauncherArrangementSource
{
    private readonly IMessageHub hub;
    private readonly ILogger<LauncherArrangementSource>? logger;
    private readonly object gate = new();
    private ImmutableHashSet<string> seedAttempted = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the source over the mesh hub.</summary>
    public LauncherArrangementSource(IMessageHub hub)
    {
        this.hub = hub;
        logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<LauncherArrangementSource>();
    }

    /// <summary>The query listing <paramref name="owner"/>'s arrangement node (its settings namespace,
    /// narrowed to the type).</summary>
    public static string ArrangementQuery(string owner) =>
        $"namespace:{LauncherArrangementPaths.SettingsNamespaceFor(owner)} nodeType:{LauncherArrangementPaths.NodeType}";

    /// <summary>The query listing <paramref name="owner"/>'s legacy app records.</summary>
    public static string LegacyRecordsQuery(string owner) =>
        $"path:{owner}/{AppNodeType.UserNamespace} scope:children nodeType:{AppNodeType.NodeType}";

    /// <summary>
    /// The viewer's arrangement — live; <c>null</c> while the node does not exist. Read as System
    /// (it is the viewer's own data, and the rows it shapes are only served back to the viewer).
    /// </summary>
    public IObservable<LauncherArrangement?> Observe(string owner)
    {
        var core = hub.ServiceProvider.GetRequiredService<IMeshQueryCore>();
        var options = hub.JsonSerializerOptions;
        var arrangementPath = LauncherArrangementPaths.PathFor(owner);
        return Fold(core.Query<MeshNode>(SystemRequest(ArrangementQuery(owner)), options))
            .Select(nodes => nodes.TryGetValue(arrangementPath, out var node)
                ? node.ContentAs<LauncherArrangement>(options) ?? new LauncherArrangement()
                : null);
    }

    /// <summary>
    /// Seeds <paramref name="owner"/>'s arrangement from their <c>_App</c> records — at most once per
    /// viewer in this process. Cold; completes when the create landed or was a benign race.
    /// Returns <c>false</c> without doing anything when this process already attempted it.
    /// </summary>
    public bool TrySeed(string owner, out IObservable<Unit> seed)
    {
        lock (gate)
        {
            if (seedAttempted.Contains(owner))
            {
                seed = Observable.Empty<Unit>();
                return false;
            }
            seedAttempted = seedAttempted.Add(owner);
        }
        seed = Seed(owner);
        return true;
    }

    private IObservable<Unit> Seed(string owner)
    {
        var core = hub.ServiceProvider.GetRequiredService<IMeshQueryCore>();
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        var options = hub.JsonSerializerOptions;
        return core.Query<MeshNode>(SystemRequest(LegacyRecordsQuery(owner)), options)
            .Where(change => change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1)
            .Select(change => FromLegacyRecords(change.Items, options))
            .SelectMany(arrangement =>
            {
                var node = BuildNode(owner, arrangement);
                // The viewer's own data, created as the viewer — never as the hub.
                return access.RunAs(new AccessContext { ObjectId = owner, Name = owner },
                        () => mesh.CreateNode(node))
                    .Select(_ => Unit.Default)
                    .Catch((Exception exception) =>
                    {
                        if (exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                            return Observable.Return(Unit.Default);
                        logger?.LogWarning(exception,
                            "[AppDirectory] seeding the launcher arrangement of {Owner} failed", owner);
                        return Observable.Throw<Unit>(exception);
                    });
            })
            .Do(_ => logger?.LogInformation(
                "[AppDirectory] seeded the launcher arrangement of {Owner} from its _App records", owner));
    }

    /// <summary>The arrangement node for <paramref name="owner"/>. Pure.</summary>
    public static MeshNode BuildNode(string owner, LauncherArrangement arrangement) =>
        new(LauncherArrangementPaths.NodeId, LauncherArrangementPaths.SettingsNamespaceFor(owner))
        {
            NodeType = LauncherArrangementPaths.NodeType,
            Name = "Launcher",
            State = MeshNodeState.Active,
            Content = arrangement,
        };

    /// <summary>
    /// The arrangement the legacy records carried: per record, its group, order and
    /// <c>customGroup</c> flag, keyed by the app it stands for — the package root
    /// (<see cref="App.Plugin"/>), or for an area app (<c>~/Inbox</c>) the record's id. Records that
    /// carry no placement are left out (the directory's defaults apply). Pure.
    /// </summary>
    public static LauncherArrangement FromLegacyRecords(IEnumerable<MeshNode> records, JsonSerializerOptions options)
    {
        var entries = ImmutableDictionary.CreateBuilder<string, LauncherEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (record.ContentAs<App>(options) is not { } app)
                continue;
            if (app.Group is null && app.Order == 0)
                continue;
            var appId = string.IsNullOrWhiteSpace(app.Plugin) ? record.Id : app.Plugin.Trim('/');
            entries[appId] = new LauncherEntry
            {
                Group = app.Group,
                Order = app.Order,
                CustomGroup = ReadCustomGroup(record, options),
            };
        }
        return new LauncherArrangement { Entries = entries.ToImmutable() };
    }

    /// <summary>The <c>customGroup</c> flag the launcher view writes onto a record — not a property
    /// of <see cref="App"/>, so read from the raw content in whatever shape it arrived.</summary>
    private static bool ReadCustomGroup(MeshNode record, JsonSerializerOptions options)
    {
        var element = record.Content switch
        {
            null => (JsonElement?)null,
            JsonElement je => je,
            System.Text.Json.Nodes.JsonNode jn => JsonSerializer.SerializeToElement(jn, options),
            var typed => JsonSerializer.SerializeToElement(typed, typed.GetType(), options),
        };
        if (element is not { ValueKind: JsonValueKind.Object } content)
            return false;
        foreach (var property in content.EnumerateObject())
            if (string.Equals(property.Name, "customGroup", StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.True;
        return false;
    }

    private static MeshQueryRequest SystemRequest(string query) =>
        MeshQueryRequest.FromQuery(query) with { UserId = WellKnownUsers.System };

    /// <summary>Folds a query's change stream into the live set, keyed by path.</summary>
    internal static IObservable<ImmutableDictionary<string, MeshNode>> Fold(IObservable<QueryResultChange<MeshNode>> changes) =>
        changes
            .Scan(ImmutableDictionary.Create<string, MeshNode>(StringComparer.OrdinalIgnoreCase), (acc, change) =>
            {
                var next = change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset ? acc.Clear() : acc;
                foreach (var node in change.Items)
                {
                    if (string.IsNullOrEmpty(node.Path))
                        continue;
                    next = change.ChangeType == QueryChangeType.Removed
                        ? next.Remove(node.Path)
                        : next.SetItem(node.Path, node);
                }
                return next;
            });
}
