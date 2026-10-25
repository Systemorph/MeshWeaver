using System.Collections.Immutable;
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
/// Configuration for the <b>InboxPreferences</b> node — what one person chose to see in their inbox
/// (<see cref="InboxPreferences"/>), one singleton per person at <see cref="InboxPreferencePaths.PathFor"/>
/// (<c>{userId}/_Settings/Inbox</c>). The inbox's filter checkboxes are bound to it DIRECTLY through the
/// node stream — no <c>/data</c> copy, no save subscription — so a tick is a write and the choice
/// outlives the page. System-managed shape (excluded from search/create/autocomplete) but user-owned: it
/// lives in the person's own partition.
/// </summary>
public static class InboxPreferencesNodeType
{
    /// <summary>The NodeType value used to identify inbox-preference nodes.</summary>
    public const string NodeType = InboxPreferencePaths.NodeType;

    /// <summary>Registers the built-in NodeType and its content type on the mesh builder.</summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <returns>The builder.</returns>
    public static TBuilder AddInboxPreferencesType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<InboxPreferences>(nameof(InboxPreferences)));
        return builder;
    }

    /// <summary>Creates the MeshNode definition for the InboxPreferences node type.</summary>
    /// <returns>The NodeType node.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Inbox Preferences",
        Icon = "/static/NodeTypeIcons/bell.svg",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<InboxPreferences>())
    };

    /// <summary>
    /// What a person who never chose gets, for the given <paramref name="sections"/>: the kinds a leg
    /// declares <see cref="InboxQueryLeg.DefaultKind"/> ticked (the open approvals, the important
    /// notifications) and every other kind unticked, every source at its <c>ShownByDefault</c>, and
    /// each kind at its own state default (activities open only; mail and chats unread and read). Pure.
    /// </summary>
    /// <param name="sections">The resolved legs of the page.</param>
    public static InboxPreferences Defaults(IEnumerable<InboxSection> sections)
    {
        var list = sections as IReadOnlyCollection<InboxSection> ?? [.. sections];
        return new InboxPreferences
        {
            Kinds = list.GroupBy(s => s.Category, StringComparer.Ordinal)
                .ToImmutableDictionary(g => InboxRows.SourceKey(g.Key), g => g.Any(s => s.DefaultKind), StringComparer.Ordinal),
            Sources = list.GroupBy(s => s.Source, StringComparer.Ordinal)
                .ToImmutableDictionary(g => InboxRows.SourceKey(g.Key), g => g.Any(s => s.ShownByDefault), StringComparer.Ordinal),
            ActivityStates = InboxStateKind.Defaults(InboxStateKind.Activity),
            MailStates = InboxStateKind.Defaults(InboxStateKind.Mail),
            ChatStates = InboxStateKind.Defaults(InboxStateKind.Chat),
        };
    }

    /// <summary>
    /// <paramref name="stored"/> with every per-kind state filter resolved — what the page lists by. A filter
    /// the node does not hold yet is the default of its kind, EXCEPT the activities filter of a node that still
    /// holds the legacy global <see cref="InboxPreferences.States"/>: that choice (say <c>open</c> only) moves to
    /// the activities filter and to nothing else, so it can never hide a mail. Pure.
    /// </summary>
    /// <param name="stored">What the node holds.</param>
    public static InboxPreferences Effective(InboxPreferences stored)
        => stored with
        {
            ActivityStates = StatesOf(stored, InboxStateKind.Activity),
            MailStates = StatesOf(stored, InboxStateKind.Mail),
            ChatStates = StatesOf(stored, InboxStateKind.Chat),
            States = ImmutableDictionary<string, bool>.Empty,
        };

    /// <summary>The stored filter of <paramref name="kind"/>, else (activities only) the migrated legacy one, else the kind's default. Pure.</summary>
    internal static ImmutableDictionary<string, bool> StatesOf(InboxPreferences stored, string kind)
    {
        var own = kind switch
        {
            InboxStateKind.Activity => stored.ActivityStates,
            InboxStateKind.Mail => stored.MailStates,
            _ => stored.ChatStates,
        };
        // The legacy global choice belongs to the activities filter. A key the person edited since (Done ticked before
        // the migration was written) wins; a legacy key they did not touch is kept, so an edit never drops "open".
        if (kind == InboxStateKind.Activity && stored.States.Count > 0)
            return own.Count > 0 ? stored.States.SetItems(own) : stored.States;
        return own.Count > 0 ? own : InboxStateKind.Defaults(kind);
    }

    /// <summary>
    /// <paramref name="current"/> with every kind and source it lacks added from <paramref name="expected"/> —
    /// an entry the person already has (ticked or not) is never touched, and a per-kind state filter the node lacks is
    /// migrated from the legacy global one (activities only) or defaulted — or null when it lacks none. Pure: this is what makes the seeding
    /// converge, because a second pass finds nothing missing.
    /// </summary>
    /// <param name="current">What is stored.</param>
    /// <param name="expected">What the page knows (<see cref="Defaults"/>).</param>
    public static InboxPreferences? Completed(InboxPreferences current, InboxPreferences expected)
    {
        var kinds = Merge(current.Kinds, expected.Kinds);
        var sources = Merge(current.Sources, expected.Sources);
        // The per-kind state filters a node written before they existed lacks: migrated (activities) or defaulted.
        var migrated = MissingStates(current).Count > 0 ? Effective(current) : current;
        return ReferenceEquals(kinds, current.Kinds) && ReferenceEquals(sources, current.Sources) && ReferenceEquals(migrated, current)
            ? null
            : migrated with { Kinds = kinds, Sources = sources };
    }

    /// <summary>The per-kind state filters <paramref name="stored"/> does not hold yet (kind names), or the legacy global one still present. Pure.</summary>
    internal static IReadOnlyList<string> MissingStates(InboxPreferences stored)
        => [.. new[]
        {
            (InboxStateKind.Activity, stored.ActivityStates.Count == 0 || stored.States.Count > 0),
            (InboxStateKind.Mail, stored.MailStates.Count == 0),
            (InboxStateKind.Chat, stored.ChatStates.Count == 0),
        }.Where(k => k.Item2).Select(k => k.Item1)];

    private static ImmutableDictionary<string, bool> Merge(ImmutableDictionary<string, bool> current, ImmutableDictionary<string, bool> expected)
    {
        var missing = expected.Where(kv => !current.ContainsKey(kv.Key)).ToArray();
        return missing.Length == 0 ? current : current.SetItems(missing);
    }

    /// <summary>
    /// What a page seeds into an EXISTING node for the kinds and sources it lacks: every missing source at
    /// its <c>ShownByDefault</c> (a source a package brings later must not arrive switched off), every missing
    /// kind UNTICKED — a person who ticked some kinds has not asked for the new one, and a person who ticked
    /// none sees every kind anyway — and no state group at all. Pure.
    /// </summary>
    /// <param name="sections">The resolved legs of the page.</param>
    public static InboxPreferences SeedForExisting(IEnumerable<InboxSection> sections)
    {
        var defaults = Defaults(sections);
        return defaults with
        {
            Kinds = defaults.Kinds.ToImmutableDictionary(k => k.Key, _ => false, StringComparer.Ordinal),
            ActivityStates = ImmutableDictionary<string, bool>.Empty,
            MailStates = ImmutableDictionary<string, bool>.Empty,
            ChatStates = ImmutableDictionary<string, bool>.Empty,
        };
    }

    /// <summary>Parses the preferences from a node (typed or raw <see cref="JsonElement"/>); defaults to empty when absent or unreadable.</summary>
    /// <param name="node">The node, or null.</param>
    /// <param name="options">The hub's serializer options.</param>
    public static InboxPreferences? Parse(MeshNode? node, JsonSerializerOptions options)
        => node?.ContentAs<InboxPreferences>(options);

    /// <summary>
    /// Create-on-absent (idempotent, reactive) of <paramref name="userId"/>'s preferences node, seeded
    /// with <paramref name="defaults"/>, so the checkboxes bind to an existing node. Existence is read
    /// with <c>GetQuery</c> (empty-on-absent) — never a point read of a maybe-absent path. Runs under
    /// the caller's identity (the person owns their own partition); an existing node is left untouched.
    /// Emits the node path.
    /// </summary>
    /// <param name="hub">The hub to run on.</param>
    /// <param name="userId">The person.</param>
    /// <param name="defaults">What a person who never chose gets.</param>
    /// <param name="logger">Optional logger.</param>
    public static IObservable<string> EnsureExists(IMessageHub hub, string userId, InboxPreferences defaults, ILogger? logger = null)
    {
        var path = InboxPreferencePaths.PathFor(userId);
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
                logger?.LogInformation("Seeding the inbox preferences for {User}", userId);
                return meshService.CreateNode(new MeshNode(InboxPreferencePaths.NodeId, InboxPreferencePaths.NamespaceFor(userId))
                    {
                        NodeType = NodeType,
                        Name = "Inbox Preferences",
                        State = MeshNodeState.Active,
                        Content = defaults,
                    })
                    .Select(_ => path)
                    // Idempotent: a concurrent first writer won the create race.
                    .Catch<string, Exception>(ex => ex.IsNodeAlreadyExists()
                        ? Observable.Return(path)
                        : Observable.Throw<string>(ex));
            });
    }
}
