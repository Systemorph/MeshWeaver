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
    /// The default SKILL SOURCES — one template ROW per layer so each resolves (or drops)
    /// independently: the platform <c>Skill</c> catalog, the user's own <c>{user}/Skill</c>, the
    /// current space's partition SUBTREE, and the current node type's partition SUBTREE (skills a
    /// plugin ships next to its types). A skill package adds a further row
    /// (<see cref="MergeSkillSource"/>).
    ///
    /// <para>🚨 These templates ARE what the chat resolves by default, and therefore also what
    /// <c>MeshAgentSkillsSource</c> feeds the agent framework — the two must not diverge, or a user
    /// would see skills an agent does not have. They are pinned equal to
    /// <see cref="AgentPickerProjection.BuildSkillQueries"/> by test.</para>
    ///
    /// <para>🚨 The platform row stays FIRST. It is the only row guaranteed to resolve — every other
    /// targets a partition that may not exist — and demoting it makes slash autocomplete surface
    /// nothing (<c>SkillAutocompleteTest</c>). Row order is NOT the precedence signal: precedence
    /// between layers is resolved from each result's own partition.</para>
    ///
    /// <para>The two middle layers are subtree-scoped so a space or plugin can file a skill next to
    /// the content it describes instead of only in <c>{partition}/Skill</c>.</para>
    /// </summary>
    public static readonly ImmutableArray<string> DefaultSkillQueryTemplates = ImmutableArray.Create(
        $"namespace:{AgentPickerProjection.SkillSubNamespace} nodeType:{SkillNodeType.NodeType}{AgentPickerProjection.RegistryProjection}",
        $"namespace:{UserPathToken}/{AgentPickerProjection.SkillSubNamespace} nodeType:{SkillNodeType.NodeType}{AgentPickerProjection.RegistryProjection}",
        $"path:{CurrentPathToken} scope:descendants nodeType:{SkillNodeType.NodeType}{AgentPickerProjection.RegistryProjection}",
        $"path:{NodeTypePathToken} scope:descendants nodeType:{SkillNodeType.NodeType}{AgentPickerProjection.RegistryProjection}");

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
    /// The default AGENT sources, as tokenized templates — the same set
    /// <see cref="AgentPickerProjection.BuildAgentQueries"/> defines, so there is one definition.
    /// </summary>
    public static ImmutableArray<string> DefaultAgentQueryTemplates { get; } =
        AgentPickerProjection.BuildAgentQueries(UserPathToken, CurrentPathToken).ToImmutableArray();

    /// <summary>
    /// Pure merge for "install this package": appends the package's AGENT source query
    /// (<c>namespace:{sourceNamespace}/Agent nodeType:Agent</c>) to
    /// <see cref="AiSettings.AgentQueries"/>. Mirrors <see cref="MergeSkillSource"/> exactly — an
    /// empty list is seeded with the code defaults FIRST so adding a package never silently drops
    /// the standard sources, and an already-present source is not duplicated.
    ///
    /// <para>This is the piece whose absence made every plugin-shipped agent invisible: a package
    /// wrote its agents into its own partition and nothing ever told the picker to look there.</para>
    /// </summary>
    public static AiSettings MergeAgentSource(AiSettings settings, string sourceNamespace)
    {
        var rows = settings.AgentQueries.IsDefaultOrEmpty
            ? DefaultAgentQueryTemplates
            : settings.AgentQueries;
        var query = $"namespace:{sourceNamespace}/{AgentPickerProjection.AgentSubNamespace} "
                    + $"nodeType:{AgentNodeType.NodeType}{AgentPickerProjection.RegistryProjection}";
        return rows.Contains(query, StringComparer.OrdinalIgnoreCase)
            ? settings with { AgentQueries = rows }
            : settings with { AgentQueries = rows.Add(query) };
    }

    /// <summary>
    /// Pure merge of EVERY registry source a package contributes — agents and skills together, so a
    /// caller cannot register one and forget the other. Idempotent.
    /// </summary>
    /// <param name="settings">The user's current settings.</param>
    /// <param name="partition">The package's partition, e.g. <c>Essentials</c>.</param>
    public static AiSettings MergePackageSources(AiSettings settings, string partition) =>
        MergeSkillSource(
            MergeAgentSource(settings, partition),
            $"{partition}/{AgentPickerProjection.SkillSubNamespace}");

    /// <summary>
    /// Resolves the user's AGENT sources for a context — the read side of
    /// <see cref="MergeAgentSource"/>, whose absence was #901: packages dutifully wrote their
    /// <c>{partition}/Agent</c> rows into <see cref="AiSettings.AgentQueries"/> and nothing ever
    /// read them, so plugin-shipped agents stayed invisible in every picker.
    ///
    /// <para>The canonical base query (<see cref="AgentPickerProjection.BuildAgentQuery"/> —
    /// platform + user + space namespaces) is ALWAYS first: it is the only row guaranteed to
    /// resolve, mirroring the skills rule that the platform row leads. The settings rows follow
    /// (token-substituted; the fused default template dedupes against the base when its tokens
    /// resolve, and drops harmlessly when the context is empty — the base already covers those
    /// layers). Reserved/rogue partitions are nulled so a poisoned context can't break the query.</para>
    /// </summary>
    public static string[] ResolveAgentQueries(
        AiSettings? settings, string? contextPath, string? userPath)
    {
        string? Partition(string? path)
            => AgentPickerProjection.IsReservedPartition(path) ? null : AgentPickerProjection.PartitionOf(path);
        var spacePartition = Partition(contextPath);
        var baseQuery = AgentPickerProjection.BuildAgentQuery(userPath, spacePartition);
        var templates = settings is { AgentQueries.IsDefaultOrEmpty: false }
            ? settings.AgentQueries.AsEnumerable()
            : DefaultAgentQueryTemplates;
        return new[] { baseQuery }
            .Concat(ResolveQueries(templates, spacePartition, null, userPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The LIVE resolved agent queries for a user + context — the reactive form
    /// <see cref="AgentPickerProjection.ObserveAgents"/> consumes (mirrors
    /// <see cref="ObserveSkillQueries"/>): observes the user's <see cref="AiSettings"/>
    /// (defaults when there is no signed-in user) and re-resolves the source rows on change.
    /// </summary>
    public static IObservable<string[]> ObserveAgentQueries(
        IWorkspace workspace, IMessageHub hub, IServiceProvider services,
        string? user, string? contextPath)
        => string.IsNullOrEmpty(user)
            ? Observable.Return(ResolveAgentQueries(null, contextPath, user))
            : Observe(workspace, hub, services, user!)
                .Select(settings => ResolveAgentQueries(settings, contextPath, user))
                .DistinctUntilChanged(qs => string.Join("\n", qs));

    /// <summary>
    /// One-shot form of <see cref="ObserveAgentQueries"/> for click-time surfaces (the
    /// <c>/agent</c> picker dialog): resolves against the FIRST live settings snapshot — not
    /// <see cref="Observe"/>'s immediate defaults emission, which would miss package rows — and
    /// degrades to the defaults-resolved base after <paramref name="timeout"/> so the picker can
    /// never hang on a slow settings read.
    /// </summary>
    public static IObservable<string[]> ResolveAgentQueriesOnce(
        IWorkspace workspace, IMessageHub hub, IServiceProvider services,
        string? user, string? contextPath, TimeSpan? timeout = null)
    {
        if (string.IsNullOrEmpty(user))
            return Observable.Return(ResolveAgentQueries(null, contextPath, user));
        var defaults = BuildDefaults(services);
        return workspace
            .GetQuery($"{NodeType}|{user}", $"path:{PathFor(user!)} nodeType:{NodeType} select:path,id,name,nodeType,content")
            .Take(1)
            .Select(nodes => Effective(
                nodes.FirstOrDefault(n => string.Equals(n.NodeType, NodeType, StringComparison.OrdinalIgnoreCase)),
                defaults, hub.JsonSerializerOptions))
            .Timeout(timeout ?? TimeSpan.FromSeconds(2))
            .Catch<AiSettings, Exception>(_ => Observable.Return(defaults))
            .Select(settings => ResolveAgentQueries(settings, contextPath, user));
    }

    // AddSkillSource (the void, fire-and-forget skill-source installer) was DELETED here (#683):
    // it subscribed to its own write internally, so an install plan could report success before
    // the settings write landed — a prompt uninstall then raced it and the late write resurrected
    // a source for a package whose nodes were gone. Install-time source registration goes through
    // IPartitionInstallHook.OnPartitionInstalled (AiSourcesInstallHook), which returns
    // IObservable<Unit> and is Concat-chained by PackageInstaller.RunInstallHooks, so the install
    // result is not produced until the settings write has landed. Any future registration path must
    // compose that hook chain — never a void method that self-subscribes.
    //
    // 🚨 The original tombstone claimed "it had no callers left". That was WRONG, and the way it was
    // wrong is worth keeping: MeshWeaver.Plugins' Store/Plugin/Source/Localizer.cs called it, for
    // PER-VIEWER (not install-time) registration at course-localization time. A repo-local grep
    // cannot see that — plugin node source is Roslyn-compiled against this assembly from a DIFFERENT
    // REPO, so deleting a public member here can only be proven safe by grepping MeshWeaver.Plugins
    // too. The break is silent until mw-plugin-test:latest is rebuilt, at which point every Store
    // node type fails to compile and, since most modules depend on Store, Plugins CI goes red
    // repo-wide. Before deleting any public API in this assembly, grep the plugins repo.

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
            .GetQuery($"{NodeType}|{user}", $"path:{PathFor(user)} nodeType:{NodeType} select:path,id,name,nodeType,content")
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
            .GetQuery($"{NodeType}|{user}", $"path:{path} nodeType:{NodeType} select:path,id,name,nodeType,content")
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
