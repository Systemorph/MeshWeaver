using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Per-user <b>AiSettings</b> singleton node — the user's AI configuration, stored at
/// <c>{user}/_Memex/AiSettings</c> (the default-settings <c>_Memex</c> namespace, non-satellite →
/// <c>mesh_nodes</c>). Single source of options for the chat composer: enabled harnesses + the
/// agent/model picker query templates. Edited from the "AI Settings" page.
///
/// <para><b>Robust by design:</b> the node is (1) seeded empty at User onboarding
/// (<see cref="AiSettingsSeedHandler"/>) AND (2) created-with-defaults on first read for any user that
/// predates the seed (<see cref="Observe"/> → <see cref="EnsureExists"/>). Reads go through a query
/// (empty-on-absent), never a direct exact-path stream, to avoid the routing-NotFound resubscribe storm.</para>
/// </summary>
public static class AiSettingsNodeType
{
    /// <summary>NodeType discriminator.</summary>
    public const string NodeType = "AiSettings";

    /// <summary>The default-settings namespace segment (<c>_Memex</c>, a non-satellite dotfile).</summary>
    public const string UserNamespace = ThreadComposerNodeType.MemexDefaultsNamespace; // "_Memex"

    /// <summary>The singleton instance id.</summary>
    public const string NodeId = "AiSettings";

    /// <summary>The per-user settings path: <c>{user}/_Memex/AiSettings</c>.</summary>
    public static string PathFor(string user) => $"{user}/{UserNamespace}/{NodeId}";

    /// <summary>Registers the AiSettings node type, content type, and the per-user seed handler.</summary>
    public static TBuilder AddAiSettingsType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<AiSettings>(nameof(AiSettings)));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodePostCreationHandler>(_ => new AiSettingsSeedHandler());
            return services;
        });
        return builder;
    }

    /// <summary>MeshNode definition for <c>nodeType:AiSettings</c>.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "AI Settings",
        Icon = "/static/NodeTypeIcons/sparkle.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<AiSettings>())
    };

    /// <summary>
    /// Sensible defaults: enabled harnesses = every registered <see cref="IHarness"/> (already
    /// feature-flag-gated at registration), ordered by harness order; agent/model queries = the
    /// canonical <see cref="AgentPickerProjection"/> templates (tokenized context).
    /// </summary>
    public static AiSettings BuildDefaults(IServiceProvider services)
    {
        var harnesses = services.GetServices<IHarness>()
            .OrderBy(h => h.Definition.Order)
            .Select(h => h.Id)
            .ToImmutableArray();

        return new AiSettings
        {
            EnabledHarnesses = harnesses,
            // Tokenized templates — BuildAgentQueries/BuildModelQueries are the single source of truth;
            // we pass the placeholder tokens as their context args and resolve them per render.
            // Agents: per-partition /Agent registry — user + current space + platform default.
            AgentQueries = AgentPickerProjection
                .BuildAgentQueries(UserPathToken, CurrentPathToken)
                .ToImmutableArray(),
            ModelQueries = AgentPickerProjection
                .BuildModelQueries(CurrentPathToken, NodeTypePathToken, null, UserPathToken)
                .ToImmutableArray(),
            SkillQueries = DefaultSkillQueryTemplates,
        };
    }

    /// <summary>
    /// The default SKILL SOURCES, one template ROW per source so each resolves (or drops) independently:
    /// the platform <c>Skill</c> catalog, the current space's <c>{space}/Skill</c>, the current node
    /// type's <c>{typePartition}/Skill</c> (skills a plugin ships next to its types), and the user's own
    /// <c>{user}/Skill</c>. A skill package adds a fifth row (<see cref="MergeSkillSource"/>).
    /// </summary>
    public static readonly ImmutableArray<string> DefaultSkillQueryTemplates = ImmutableArray.Create(
        $"namespace:{AgentPickerProjection.SkillSubNamespace} nodeType:{SkillNodeType.NodeType}",
        $"namespace:{CurrentPathToken}/{AgentPickerProjection.SkillSubNamespace} nodeType:{SkillNodeType.NodeType}",
        $"namespace:{NodeTypePathToken}/{AgentPickerProjection.SkillSubNamespace} nodeType:{SkillNodeType.NodeType}",
        $"namespace:{UserPathToken}/{AgentPickerProjection.SkillSubNamespace} nodeType:{SkillNodeType.NodeType}");

    /// <summary>
    /// Resolves the user's skill sources for a context: takes <see cref="AiSettings.SkillQueries"/>
    /// (empty ⇒ <see cref="DefaultSkillQueryTemplates"/>), substitutes <c>{currentPath}</c> with the
    /// context's PARTITION, <c>{nodeTypePath}</c> with the node type's PARTITION and <c>{userPath}</c>
    /// with the user's home (reserved/rogue partitions nulled so a poisoned context can't break the
    /// query), drops rows whose token has no value, and dedupes. Falls back to the canonical
    /// <see cref="SkillNodeType.SkillQueries"/> defaults if everything resolves away.
    /// </summary>
    public static string[] ResolveSkillQueries(
        AiSettings? settings, string? contextPath, string? nodeTypePath, string? userPath)
    {
        var templates = settings is { SkillQueries.IsDefaultOrEmpty: false }
            ? settings.SkillQueries.AsEnumerable()
            : DefaultSkillQueryTemplates;
        string? Partition(string? path)
            => AgentPickerProjection.IsReservedPartition(path) ? null : AgentPickerProjection.PartitionOf(path);
        var resolved = ResolveQueries(templates, Partition(contextPath), Partition(nodeTypePath), userPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return resolved.Length > 0
            ? resolved
            : SkillNodeType.SkillQueries(contextPath, userPath, nodeTypePath);
    }

    /// <summary>
    /// Pure merge for "install this skill package": appends the package's source query
    /// (<c>namespace:{sourceNamespace} nodeType:Skill</c>) to <see cref="AiSettings.SkillQueries"/>.
    /// When the list is still empty (= "code defaults"), it is seeded with
    /// <see cref="DefaultSkillQueryTemplates"/> FIRST so adding a package never silently drops the
    /// standard sources. Idempotent — an already-present source (case-insensitive) is not duplicated.
    /// </summary>
    public static AiSettings MergeSkillSource(AiSettings settings, string sourceNamespace)
    {
        var rows = settings.SkillQueries.IsDefaultOrEmpty
            ? DefaultSkillQueryTemplates
            : settings.SkillQueries;
        var query = $"namespace:{sourceNamespace} nodeType:{SkillNodeType.NodeType}";
        return rows.Contains(query, StringComparer.OrdinalIgnoreCase)
            ? settings with { SkillQueries = rows }
            : settings with { SkillQueries = rows.Add(query) };
    }

    /// <summary>
    /// Installs a SKILL PACKAGE for <paramref name="user"/>: appends the package's skill folder (e.g.
    /// <c>Office/Skill</c>) to the user's <see cref="AiSettings.SkillQueries"/> sources — idempotent,
    /// fire-and-forget, same keyed-query read discipline as <see cref="EnsureExists"/> (never a
    /// point-read of a possibly-absent node).
    /// </summary>
    public static void AddSkillSource(
        IMessageHub hub, IServiceProvider services, string user, string sourceNamespace)
    {
        var meshService = services.GetService<IMeshService>();
        if (meshService is null || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(sourceNamespace))
            return;
        var path = PathFor(user);
        hub.GetWorkspace()
            .GetQuery($"{NodeType}|{user}", $"path:{path} nodeType:{NodeType}")
            .Take(1)
            .SelectMany(nodes =>
            {
                var node = nodes.FirstOrDefault(n =>
                    string.Equals(n.NodeType, NodeType, StringComparison.OrdinalIgnoreCase));
                var current = Effective(node, BuildDefaults(services), hub.JsonSerializerOptions);
                var merged = MergeSkillSource(current, sourceNamespace);
                var target = node ?? MeshNode.FromPath(path) with { NodeType = NodeType, Name = "AI Settings" };
                return meshService.CreateOrUpdateNode(target with { Content = merged });
            })
            .Subscribe(
                _ => { },
                ex => services.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(AiSettingsNodeType))
                    .LogWarning(ex, "AddSkillSource: appending {Source} for {Path} failed",
                        sourceNamespace, path));
    }

    /// <summary>
    /// The LIVE resolved skill queries for a user + context — the reactive form the skill surfaces
    /// (slash autocomplete, slash execution) consume: observes the user's <see cref="AiSettings"/>
    /// (defaults when there is no signed-in user) and re-resolves the source templates on change.
    /// </summary>
    public static IObservable<string[]> ObserveSkillQueries(
        IWorkspace workspace, IMessageHub hub, IServiceProvider services,
        string? user, string? contextPath, string? nodeTypePath)
        => string.IsNullOrEmpty(user)
            ? Observable.Return(ResolveSkillQueries(null, contextPath, nodeTypePath, user))
            : Observe(workspace, hub, services, user!)
                .Select(settings => ResolveSkillQueries(settings, contextPath, nodeTypePath, user))
                .DistinctUntilChanged(qs => string.Join("\n", qs));

    private const string CurrentPathToken = "{currentPath}";
    private const string NodeTypePathToken = "{nodeTypePath}";
    private const string UserPathToken = "{userPath}";

    /// <summary>
    /// Resolves query templates for a composer instance: substitutes <c>{currentPath}</c> /
    /// <c>{nodeTypePath}</c> / <c>{userPath}</c>, and DROPS any template whose referenced token has
    /// an empty value (mirroring how the builders only add those queries when the arg is non-empty).
    /// </summary>
    public static string[] ResolveQueries(
        IEnumerable<string> templates, string? currentPath, string? nodeTypePath, string? userPath)
    {
        var subs = new[]
        {
            (CurrentPathToken, currentPath),
            (NodeTypePathToken, nodeTypePath),
            (UserPathToken, userPath),
        };

        var result = new List<string>();
        foreach (var template in templates)
        {
            var q = template;
            var drop = false;
            foreach (var (token, value) in subs)
            {
                if (!q.Contains(token, StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(value)) { drop = true; break; }
                q = q.Replace(token, value);
            }
            if (!drop)
                result.Add(q);
        }
        return result.ToArray();
    }

    /// <summary>
    /// Live per-user <see cref="AiSettings"/>. Creates the node with <see cref="BuildDefaults"/> if it
    /// doesn't exist yet (idempotent, fire-and-forget) and reads it via a query (empty-on-absent). An
    /// empty field falls back to the in-memory default so a seeded-empty or partial node behaves as
    /// defaults. Emits the defaults immediately for the first paint, then the live node content.
    /// </summary>
    public static IObservable<AiSettings> Observe(
        IWorkspace workspace, IMessageHub hub, IServiceProvider services, string user)
    {
        var defaults = BuildDefaults(services);
        EnsureExists(hub, services, user);
        return workspace
            .GetQuery($"{NodeType}|{user}", $"path:{PathFor(user)} nodeType:{NodeType}")
            .Select(nodes => Effective(
                nodes.FirstOrDefault(n => string.Equals(n.NodeType, NodeType, StringComparison.OrdinalIgnoreCase)),
                defaults, hub.JsonSerializerOptions))
            .StartWith(defaults)
            .DistinctUntilChanged();
    }

    /// <summary>Create-on-absent (with defaults); existing node untouched.</summary>
    public static void EnsureExists(IMessageHub hub, IServiceProvider services, string user)
    {
        var meshService = services.GetService<IMeshService>();
        if (meshService is null)
            return;
        var path = PathFor(user);
        // 🚨 Create-on-absent must NEVER point-read/patch the node via
        // GetMeshNodeStream(path).Update. On an ABSENT node that opens a cross-hub
        // SubscribeRequest + JSON-merge patch to a node/hub that does not exist, which
        // Orleans-NotFound-RESUBSCRIBE-STORMS (the rbuergi/_Memex/AiSettings +
        // system-security/_Memex/AiSettings NotFound flood — fired on EVERY thread
        // execution through Observe, it burned the action block and helped wedge the
        // portal). Read existence via the SAME keyed GetQuery the Observe read uses
        // (empty-on-absent, shared cached stream — no point-read, never storms), and seed
        // only when genuinely absent through the node-lifecycle CreateNode (create-only:
        // it does not clobber an existing customised node). See
        // feedback_optional_node_query_not_access / Doc/Architecture/AsynchronousCalls.md.
        hub.GetWorkspace()
            .GetQuery($"{NodeType}|{user}", $"path:{path} nodeType:{NodeType}")
            .Take(1)
            .Where(nodes => !nodes.Any(n =>
                string.Equals(n.NodeType, NodeType, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(_ => meshService.CreateNode(
                MeshNode.FromPath(path) with
                {
                    NodeType = NodeType,
                    Name = "AI Settings",
                    Content = BuildDefaults(services)
                }))
            .Subscribe(
                _ => { },
                ex => services.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(AiSettingsNodeType))
                    .LogWarning(ex, "EnsureExists: AiSettings create-on-absent failed for {Path}", path));
    }

    /// <summary>
    /// The effective settings for a node: the saved <see cref="AiSettings"/> with each EMPTY field
    /// filled from <paramref name="defaults"/> (an empty/absent node behaves as the code defaults).
    /// </summary>
    public static AiSettings Effective(MeshNode? node, AiSettings defaults, JsonSerializerOptions options)
    {
        var settings = node?.Content switch
        {
            AiSettings s => s,
            JsonElement je => TryDeserialize(je, options),
            _ => null,
        };
        if (settings is null)
            return defaults;
        return settings with
        {
            EnabledHarnesses = settings.EnabledHarnesses.IsDefaultOrEmpty ? defaults.EnabledHarnesses : settings.EnabledHarnesses,
            AgentQueries = settings.AgentQueries.IsDefaultOrEmpty ? defaults.AgentQueries : settings.AgentQueries,
            ModelQueries = settings.ModelQueries.IsDefaultOrEmpty ? defaults.ModelQueries : settings.ModelQueries,
        };
    }

    private static AiSettings? TryDeserialize(JsonElement je, JsonSerializerOptions options)
    {
        try { return JsonSerializer.Deserialize<AiSettings>(je.GetRawText(), options); }
        catch { return null; }
    }

    /// <summary>
    /// Seeds an EMPTY <see cref="AiSettings"/> at <c>{user}/_Memex/AiSettings</c> on User onboarding —
    /// DI-free (defaults are resolved lazily by <see cref="Observe"/> / the settings page). Mirrors
    /// <c>ModelProviderSelectionSeedHandler</c>; keeps the composer's read from ever hitting a routing
    /// NotFound for newly-onboarded users.
    /// </summary>
    private sealed class AiSettingsSeedHandler : INodePostCreationHandler
    {
        public string NodeType => UserNodeType.NodeType; // "User"

        public IObservable<System.Reactive.Unit> Handle(MeshNode createdNode, string? createdBy)
            => System.Reactive.Linq.Observable.Empty<System.Reactive.Unit>();

        public IEnumerable<MeshNode> GetAdditionalNodes(MeshNode createdNode)
        {
            var userPath = !string.IsNullOrEmpty(createdNode.Path) ? createdNode.Path : createdNode.Id;
            if (string.IsNullOrEmpty(userPath))
                yield break;

            yield return new MeshNode(NodeId, $"{userPath}/{UserNamespace}")
            {
                NodeType = AiSettingsNodeType.NodeType,
                Name = "AI Settings",
                Content = new AiSettings(),
            };
        }
    }
}
