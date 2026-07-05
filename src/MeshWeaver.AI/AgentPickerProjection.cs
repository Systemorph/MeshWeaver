using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Single source of truth for the chat picker's data flow:
/// the synced-query strings the view subscribes to, and the
/// projection that turns the resulting <see cref="MeshNode"/>
/// snapshot into the <see cref="AgentDisplayInfo"/> /
/// <see cref="ModelInfo"/> lists bound to the agent / model
/// combo boxes.
///
/// <para>🚨 The chat view (<c>ThreadChatView.SubscribeToAgentNodes</c> +
/// <c>OnSyncedAgentSnapshot</c>) calls these helpers directly. Tests in
/// <c>AgentPickerProjectionTest</c> drive the same
/// <see cref="MeshWeaver.Graph.SyncedQueryDataSourceExtensions.GetQuery(MeshWeaver.Data.IWorkspace, object, string[])"/>
/// pipe with the strings <see cref="BuildAgentQueries"/> returns and run the
/// snapshot through <see cref="ProjectAgents"/> /
/// <see cref="ProjectModels"/>. If a regression silently empties the
/// dropdowns at runtime, those tests fail too — no parallel
/// reconstruction.</para>
/// </summary>
public static class AgentPickerProjection
{
    /// <summary>Named-query id for the agents synced subscription. Same id everywhere = one shared upstream subscription via the workspace's per-id cache.</summary>
    public const string AgentsQueryId = "Agents";

    /// <summary>Named-query id for the language-models synced subscription.</summary>
    public const string ModelsQueryId = "LanguageModels";

    /// <summary>Conventional namespace for built-in agents (matches <c>BuiltInAgentProvider</c>).</summary>
    public const string AgentRootNamespace = "Agent";

    /// <summary>
    /// The dedicated sub-namespace each partition uses for its OWN agents — <c>{partition}/Agent</c>
    /// (e.g. <c>rbuergi/Agent</c>, <c>AgenticPension/Agent</c>). Platform defaults live in the bare
    /// <see cref="AgentRootNamespace"/> (<c>Agent</c>). One registry, three layers, listed directly.
    /// </summary>
    public const string AgentSubNamespace = "Agent";

    /// <summary>The dedicated sub-namespace each partition uses for its OWN models — <c>{partition}/Model</c>;
    /// platform model defaults live in the bare <c>Model</c> namespace.</summary>
    public const string ModelSubNamespace = "Model";

    /// <summary>
    /// Array form of <see cref="BuildAgentQuery"/> for the <c>hub.GetQuery(id, params string[])</c>
    /// surface — a single-element array carrying THE one canonical agent-registry query.
    /// </summary>
    public static string[] BuildAgentQueries(string? userPath = null, string? spacePath = null)
        => new[] { BuildAgentQuery(userPath, spacePath) };

    /// <summary>
    /// THE single canonical agent-registry query — the one place this string is defined. Agents live
    /// in a dedicated <c>/Agent</c> sub-namespace PER PARTITION; the query lists the relevant ones
    /// DIRECTLY (exact membership, NO graph/ancestor walk):
    /// <list type="bullet">
    ///   <item>platform defaults — namespace <c>Agent</c> (always);</item>
    ///   <item>the current space's — <c>{spacePath}/Agent</c>;</item>
    ///   <item>the chatting/owning user's — <c>{userPath}/Agent</c>.</item>
    /// </list>
    /// Produces e.g. <c>namespace:rbuergi/Agent|AgenticPension/Agent|Agent nodeType:Agent</c>. The
    /// <c>namespace:A|B|C</c> alternation resolves (see <see cref="MeshWeaver.Mesh.QueryParser"/>) to a
    /// single <c>namespace IN (...)</c> exact-membership filter — so the combobox, the <c>/agent</c>
    /// picker and the engine's agent selection all issue exactly this ONE query via <c>hub.GetQuery</c>
    /// (per-user RLS at the caller's portal hub naturally hides private agents from non-owners). Utility
    /// (generator) agents are kept unless <paramref name="excludeUtility"/> — the conversational
    /// surfaces drop them via <see cref="IsUtilityAgent"/>.
    /// </summary>
    public static string BuildAgentQuery(
        string? userPath = null, string? spacePath = null, bool excludeUtility = false)
        => BuildRegistryQuery(AgentNodeType.NodeType, AgentSubNamespace, userPath, spacePath,
            excludeUtility ? $" -content.modelTier:{UtilityModelTier}" : "");

    /// <summary>The dedicated sub-namespace each partition uses for its OWN skills — <c>{partition}/Skill</c>;
    /// platform skill defaults live in the bare <c>Skill</c> namespace. Same registry shape as agents/models.</summary>
    public const string SkillSubNamespace = "Skill";

    /// <summary>Array form of <see cref="BuildSkillQuery"/> for the <c>hub.GetQuery</c> surface.</summary>
    public static string[] BuildSkillQueries(string? userPath = null, string? spacePath = null)
        => new[] { BuildSkillQuery(userPath, spacePath) };

    /// <summary>
    /// THE single canonical skill-registry query — IDENTICAL pattern to agents + models. Skills live in a
    /// dedicated <c>/Skill</c> sub-namespace PER PARTITION (platform <c>Skill</c> + <c>{space}/Skill</c> +
    /// <c>{user}/Skill</c>), listed directly as a <c>namespace:A|B|C</c> exact-membership alternation — one
    /// registry pattern for every public top-level domain (Agent, Model, Skill, …). Produces e.g.
    /// <c>namespace:rbuergi/Skill|AgenticPension/Skill|Skill nodeType:Skill</c>.
    /// </summary>
    public static string BuildSkillQuery(string? userPath = null, string? spacePath = null)
        => BuildRegistryQuery(SkillNodeType.NodeType, SkillSubNamespace, userPath, spacePath, "");

    // Rogue/reserved ROUTE partitions — auto-minted page artifacts (login, welcome, settings, …; mirrors
    // the reserved-schema list in PostgreSqlCrossSchemaQueryProvider). They carry NO read policy and never
    // hold registry nodes, so including one in the namespace IN(...) — e.g. when the chat context resolves
    // to a rogue "login" node — fails the WHOLE query with "lacks Read permission on 'login'" and the
    // picker/autocomplete goes empty. A read-only reserved-word set (allowed static; never written).
    // ImmutableHashSet (not HashSet): a never-written constant lookup must use an immutable type so
    // it satisfies the no-static-mutable-collection rule (NoStaticCollectionsTest / NoStaticState.md)
    // without an allowlist entry — and the Collections Policy mandates Immutable over mutable anyway.
    private static readonly ImmutableHashSet<string> ReservedPartitions =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            "login", "markdown", "onboarding", "welcome", "settings", "storage");

    /// <summary>True when <paramref name="path"/>'s partition (first segment) is a rogue/reserved ROUTE
    /// partition (login, welcome, settings, …) — never a real space, so never a valid thread or registry
    /// namespace. Creating a thread there is denied (no write policy) and tears the side-panel chat down.</summary>
    public static bool IsReservedPartition(string? path)
        => PartitionOf(path) is { } p && ReservedPartitions.Contains(p);

    /// <summary>
    /// Assembles a per-partition registry query: the platform default namespace (<paramref name="sub"/>)
    /// plus the user's and space's own (<c>{partition}/{sub}</c>), listed directly as a
    /// <c>namespace:A|B|C</c> exact-membership alternation. No scope — agents/models are placed in a
    /// flat, well-known namespace per partition, so there is no graph search.
    /// </summary>
    private static string BuildRegistryQuery(
        string nodeType, string sub, string? userPath, string? spacePath, string extra)
    {
        var namespaces = new List<string>();
        void Add(string? partition)
        {
            // Skip empty + rogue/reserved route partitions so a poisoned context can't break the query.
            if (string.IsNullOrEmpty(partition) || ReservedPartitions.Contains(partition)) return;
            var ns = $"{partition}/{sub}";
            if (!namespaces.Contains(ns, StringComparer.OrdinalIgnoreCase))
                namespaces.Add(ns);
        }
        Add(userPath);
        Add(spacePath);
        namespaces.Add(sub); // platform defaults — always present, last
        var nsClause = namespaces.Count > 1
            ? $"namespace:{string.Join("|", namespaces)}"
            : $"namespace:{namespaces[0]}";
        return $"{nsClause} nodeType:{nodeType}{extra}";
    }

    /// <summary>
    /// The signed-in user's HOME partition — the partition whose <c>{user}/Skill</c> (and
    /// <c>{user}/Agent</c>, <c>{user}/_Memex</c>) namespaces the registry surfaces. Resolved from the
    /// hub/circuit identity, preferring the durable <see cref="AccessService.CircuitContext"/> then the
    /// per-request <see cref="AccessService.Context"/>; a leaked <c>system-security</c> or hub-shaped
    /// principal (<c>sync/</c>, <c>mesh/</c>, …) is filtered out — never a real user partition. Returns
    /// <c>null</c> when no real user is set. The SINGLE source of truth for "who is the user" across the
    /// picker, the slash-skill resolver, and the autocomplete — mirrors <c>ThreadChatView.ResolveUserHome</c>
    /// so a user's own agents / models / skills surface the SAME way everywhere.
    /// </summary>
    public static string? ResolveUserHome(MeshWeaver.Messaging.AccessService? accessService)
    {
        if (accessService is null) return null;
        foreach (var candidate in new[] { accessService.CircuitContext?.ObjectId, accessService.Context?.ObjectId })
            if (!string.IsNullOrEmpty(candidate)
                && candidate != MeshWeaver.Mesh.Security.WellKnownUsers.System
                && !MeshWeaver.Messaging.AccessService.LooksLikeHubPrincipal(candidate))
                return candidate;
        return null;
    }

    /// <summary>The partition (top-level path segment) a context path belongs to — the "space" whose
    /// <c>/Agent</c> + <c>/Model</c> namespaces the registry surfaces. <c>AgenticPension/Foo/_Thread/x</c>
    /// → <c>AgenticPension</c>; null/empty → null.</summary>
    public static string? PartitionOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var trimmed = path.Trim('/');
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? trimmed : trimmed[..slash];
    }

    /// <summary>
    /// The (contextPath, nodeTypePath) pair the chat picker MUST feed into
    /// <see cref="BuildAgentQueries"/> / <see cref="BuildModelQueries"/> so that all three
    /// context-scoped queries are issued (built-in + per-context-ancestors +
    /// per-NodeType-namespace). Sourced from the SAME resolved navigation context that
    /// <see cref="AgentChatClient"/> reads at execution time: the context PATH is the
    /// resolved node's main path (<see cref="NavigationContext.PrimaryPath"/>, satellite
    /// segments stripped) and <see cref="NodeTypePath"/> is that node's
    /// <see cref="MeshNode.NodeType"/>. When both are populated the picker surfaces a
    /// Space's own agents/models — the chat-side-panel bug where a frequently-NULL
    /// ambient context collapsed the union to the built-in query only.
    /// </summary>
    public readonly record struct PickerContext(string? ContextPath, string? NodeTypePath);

    /// <summary>
    /// Derives the timing-safe <see cref="PickerContext"/> for the picker from the latest
    /// RESOLVED navigation context (<paramref name="resolved"/>) — the value the chat view
    /// reads off <c>INavigationService.NavigationContext</c> (a ReplaySubject(1), so the last
    /// value replays). The <paramref name="fallbackContextPath"/> (the view's seeded
    /// <c>initialContext</c>) is used only when the navigation context has not yet resolved to
    /// a usable node (still loading, or the bare <c>chat</c> route). This is the single source
    /// of truth for "where does the picker get currentPath + nodeTypePath": both the Blazor
    /// view (<c>ThreadChatView.OpenPicker</c>) and its tests derive args through here, so the
    /// "all 3 queries when context+nodeType resolve" contract can be pinned without a Blazor
    /// harness.
    /// </summary>
    public static PickerContext DerivePickerContext(
        NavigationContext? resolved, string? fallbackContextPath = null)
    {
        // Prefer the RESOLVED nav context. Skip it while it's still null (loading / not-found)
        // or pointing at the bare chat route — those carry no real content node. In that case
        // fall back to the view's already-seeded initialContext so we never collapse to the
        // built-in-only query just because the async resolution hasn't landed yet.
        var usable = resolved is not null
                     && resolved.Path != "chat"
                     && !string.IsNullOrEmpty(resolved.PrimaryPath)
            ? resolved
            : null;

        var contextPath = NormalizeContextPath(usable?.PrimaryPath)
                          ?? NormalizeContextPath(fallbackContextPath);
        // NodeType only comes from a resolved node — the fallback path is just a string, it
        // carries no NodeType. This mirrors AgentChatClient.Initialize: nodeTypePath ??= Context?.Node?.NodeType.
        var nodeTypePath = usable?.Node?.NodeType;
        return new PickerContext(contextPath, nodeTypePath);
    }

    /// <summary>
    /// Strips any trailing satellite segments (segments starting with <c>_</c>, e.g.
    /// <c>_Thread/&lt;slug&gt;</c>, <c>_Comment/&lt;id&gt;</c>) from a context path so the
    /// picker queries reason about the main node, not the satellite. Returns the input
    /// unchanged when null/empty or when no <c>_</c> segment is present. Mirrors the
    /// view's / AgentChatClient's private NormalizeContextPath — kept here so the
    /// picker-context derivation is self-contained and testable.
    /// </summary>
    public static string? NormalizeContextPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].StartsWith('_'))
                return string.Join('/', segments, 0, i);
        }
        return path;
    }

    /// <summary>
    /// 🚨 The EXACT pipeline the chat agent combobox is bound to. The view subscribes to this; tests
    /// subscribe to this. No parallel reconstruction of queries / projection anywhere.
    /// <paramref name="spacePath"/> is the current space partition; <paramref name="userPath"/> the
    /// user's home partition — their <c>/Agent</c> namespaces plus the platform default are listed.
    ///
    /// <para>🚨🚨 <b><paramref name="hub"/> MUST be a hub LOCAL TO THE CALLER'S CONTEXT — the
    /// <b>portal hub</b> (the GUI / Blazor circuit, carrying the user's identity) or the <b>thread
    /// hub</b> (thread execution, carrying the thread OWNER's identity).</b> The query reads the
    /// per-partition <c>{user}/Agent</c> + <c>{space}/Agent</c> namespaces via <c>hub.GetQuery</c>,
    /// whose per-user RLS keys off the hub's AccessContext. Pass a SERVER-SIDE layout-area hub (a
    /// node's per-node hub) and you get the hub principal, NOT the user — RLS strips the
    /// user/space namespaces (empty dropdown) AND the cross-partition subscribe STORMS the portal
    /// (the 2026-06-17 atioz wedge: a server-side combobox in ThreadComposerView). GUI →
    /// <c>BlazorView.Hub</c> (= <c>PortalApplication.Hub</c>); exec → <c>ThreadExecution</c>'s
    /// <c>parentHub</c>. NEVER a <c>LayoutAreaHost.Hub</c> for the per-partition query.</para>
    /// </summary>
    public static IObservable<IReadOnlyList<AgentDisplayInfo>> ObserveAgents(
        IMessageHub hub, string? userPath = null, string? spacePath = null)
        => ObserveSnapshot(hub.GetWorkspace(), hub,
                $"{AgentsQueryId}|u={userPath ?? ""}|s={spacePath ?? ""}",
                BuildAgentQuery(userPath, spacePath))
            .Select(snapshot => ProjectAgents(snapshot, hub.JsonSerializerOptions));

    /// <summary>
    /// 🚨 The EXACT pipeline the chat model combobox is bound to. The view subscribes to this; tests
    /// subscribe to this. Models stay on the <c>Provider</c> catalog shape (providers contain models
    /// + credentials) — the per-partition <c>/Model</c> registry is the next increment.
    /// </summary>
    public static IObservable<IReadOnlyList<ModelInfo>> ObserveModels(
        IWorkspace workspace, IMessageHub hub,
        string? currentPath = null, string? nodeTypePath = null,
        IReadOnlyList<string>? selectedProviderPaths = null,
        string? userPath = null)
        => ObserveSnapshot(workspace, hub,
                BuildModelQueryId(ModelsQueryId, currentPath, nodeTypePath, selectedProviderPaths, userPath),
                BuildModelQueries(currentPath, nodeTypePath, selectedProviderPaths, userPath))
            .Select(snapshot => ProjectModels(snapshot, hub.JsonSerializerOptions));

    /// <summary>Named-query id for the harnesses synced subscription.</summary>
    public const string HarnessesQueryId = "Harnesses";

    /// <summary>Named-query id for the user's MASTER composer ({user}/_Thread/ThreadComposer) read.</summary>
    public const string MasterComposerQueryId = "MasterComposer";

    /// <summary>
    /// The DEFAULT composer selection — resolved purely by ORDER, never hardcoded. For each registry
    /// (agent / model / harness) the default is the node with the LOWEST <c>Order</c> (the <c>Order = -1</c>
    /// convention: "to make something the default, set its order to -1"). No hardcoded agent name, model
    /// id, or harness; nothing is invented when a registry is empty (the field stays null). Reactive +
    /// testable like <see cref="ObserveAgents"/> / <see cref="ObserveModels"/>; the chat view subscribes
    /// to this to seed a new composer.
    /// <para>Utility (generator) agents are excluded — the default is never a background generator.</para>
    /// </summary>
    public static IObservable<ThreadComposer> ObserveDefaultComposer(
        IMessageHub hub, string? userPath = null, string? spacePath = null,
        string? currentPath = null, string? nodeTypePath = null,
        IReadOnlyList<string>? selectedProviderPaths = null)
    {
        var workspace = hub.GetWorkspace();

        var agent = ObserveAgents(hub, userPath, spacePath)
            .Select(list => list
                .Where(a => !IsUtilityAgent(a) && !string.IsNullOrEmpty(a.Path))
                .OrderBy(a => a.Order)
                .Select(a => a.Path)
                .FirstOrDefault());

        var credResolver = hub.ServiceProvider.GetService<ChatClientCredentialResolver>();
        var model = ObserveModels(workspace, hub, currentPath, nodeTypePath, selectedProviderPaths, userPath)
            .Select(list =>
            {
                var ordered = list.Where(m => !string.IsNullOrEmpty(m.Path)).OrderBy(m => m.Order).ToList();
                // Default to the lowest-Order model whose credentials actually RESOLVE — mirrors
                // ChatClientCredentialResolver.ResolveDefaultModelId (the execution-time fallback), so the
                // composer never DEFAULTS to a model with no provider/key configured (e.g. a "glm-5.2"
                // catalog entry whose prices/provider were never entered). That divergence — composer picks
                // lowest-Order, execution picks lowest-Order-that-resolves — is what surfaced as "selected
                // model X unavailable, using default Y". Fall back to the first model so the composer is
                // never left without a selection.
                var resolvable = credResolver == null ? null
                    : ordered.FirstOrDefault(m => credResolver.Resolve(m.Path!) != CredentialResolution.Missing);
                return (resolvable ?? ordered.FirstOrDefault())?.Path;
            });

        var harness = ObserveSnapshot(workspace, hub,
                $"{HarnessesQueryId}|u={userPath ?? ""}|s={spacePath ?? ""}",
                BuildRegistryQuery(HarnessNodeType.NodeType, HarnessNodeType.RootNamespace, userPath, spacePath, ""))
            .Select(snapshot => snapshot
                .Where(n => n.Path != null
                    && string.Equals(n.NodeType, HarnessNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Order ?? 0)
                .Select(n => n.Path)
                .FirstOrDefault());

        // 🎯 The user's MASTER composer ({user}/_Thread/ThreadComposer) is the single source of truth for
        // chat defaults. Any composer WITHOUT its own selection — a new thread, or a namespace that has no
        // standard composer of its own — INHERITS the master's harness/model/agent/effort. The catalog
        // (lowest-Order, resolved above) is ONLY the fallback for a brand-new master the user has never
        // touched. Read via a query (empty-on-absent — no NotFound storm), never a direct GetMeshNodeStream
        // on the maybe-absent exact path; the seed handler normally makes it present.
        var jsonOptions = hub.JsonSerializerOptions;
        var masterLogger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.AgentPickerProjection");
        var master = string.IsNullOrEmpty(userPath)
            ? System.Reactive.Linq.Observable.Return<ThreadComposer?>(null)
            : ObserveSnapshot(workspace, hub,
                    $"{MasterComposerQueryId}|u={userPath}",
                    $"namespace:{userPath}/{ThreadNodeType.ThreadPartition} nodeType:{ThreadComposerNodeType.NodeType}")
                .Select(snapshot => snapshot
                    .Where(n => string.Equals(n.NodeType, ThreadComposerNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                    .Select(n => ThreadComposerNodeType.ComposerOf(n, jsonOptions, masterLogger))
                    .FirstOrDefault(c => c is not null));

        return agent.CombineLatest(model, harness, master,
            (a, m, h, mstr) => new ThreadComposer
            {
                Harness   = FirstNonEmpty(mstr?.Harness, h),
                ModelName = FirstNonEmpty(mstr?.ModelName, m),
                AgentName = FirstNonEmpty(mstr?.AgentName, a),
                Effort    = mstr?.Effort
            });
    }

    /// <summary>First non-empty of the two — master value wins, catalog fallback.</summary>
    private static string? FirstNonEmpty(string? primary, string? fallback)
        => string.IsNullOrEmpty(primary) ? fallback : primary;

    /// <summary>
    /// The model picker queries: the system <c>Provider</c> catalog plus per-context / per-NodeType /
    /// per-user subtrees and any user-selected provider subtree — all <c>nodeType:LanguageModel|ModelProvider</c>,
    /// varying only namespace + scope (the synced-collection all-Initial gating constraint). The
    /// per-partition flat <c>/Model</c> registry is the next increment; credentials live in <c>Provider</c>.
    /// </summary>
    public static string[] BuildModelQueries(
        string? currentPath = null,
        string? nodeTypePath = null,
        IEnumerable<string>? selectedProviderPaths = null,
        string? userPath = null)
    {
        var typeFilter = $"{LanguageModelNodeType.NodeType}|{ModelProviderNodeType.NodeType}";
        var queries = new List<string>
        {
            $"namespace:{ModelProviderNodeType.RootNamespace} nodeType:{typeFilter} scope:descendants",
        };
        // Skip reserved/rogue ROUTE partitions (login, welcome, settings, …): a reserved currentPath/
        // nodeTypePath would make namespace:{login}/Provider read the policy-less reserved partition and
        // fail the WHOLE model query with "lacks Read permission on 'login'" — the picker goes empty.
        // Mirrors BuildRegistryQuery's filter (the agent/skill queries already skip these).
        if (!string.IsNullOrEmpty(currentPath) && !IsReservedPartition(currentPath))
            queries.Add($"namespace:{currentPath}/{ModelProviderNodeType.RootNamespace} nodeType:{typeFilter} scope:descendants");
        if (!string.IsNullOrEmpty(nodeTypePath) && !IsReservedPartition(nodeTypePath))
            queries.Add($"namespace:{nodeTypePath}/{ModelProviderNodeType.RootNamespace} nodeType:{typeFilter} scope:descendants");
        if (!string.IsNullOrEmpty(userPath))
            queries.Add($"namespace:{ModelProviderNodeType.UserNamespacePath(userPath)} nodeType:{typeFilter} scope:descendants");
        if (selectedProviderPaths != null)
            foreach (var path in selectedProviderPaths)
                if (!string.IsNullOrEmpty(path))
                    queries.Add($"namespace:{path} nodeType:{typeFilter} scope:selfAndDescendants");
        return queries.ToArray();
    }

    /// <summary>Context-scoped cache id for the model query (provider selection must be part of the id).</summary>
    private static string BuildModelQueryId(
        string baseId, string? currentPath, string? nodeTypePath,
        IReadOnlyList<string>? selectedProviderPaths, string? userPath)
    {
        var selected = selectedProviderPaths is { Count: > 0 }
            ? string.Join(",", selectedProviderPaths.OrderBy(x => x, StringComparer.Ordinal))
            : "";
        return $"{baseId}|p={currentPath ?? ""}|t={nodeTypePath ?? ""}|s={selected}|u={userPath ?? ""}";
    }

    /// <summary>
    /// Live MeshNode snapshot for a registry query via <c>hub.GetQuery</c> — the centralized,
    /// per-user-RLS surface (one shared upstream subscription per id across the chat view,
    /// AgentChatClient and the driver factory). "hub.GetQuery to be precise": reads run under the
    /// caller hub's identity, so a thread's agents resolve under the thread OWNER.
    /// </summary>
    public static IObservable<IEnumerable<MeshNode>> ObserveSnapshot(
        IWorkspace workspace, IMessageHub hub, string queryId, params string[] queries)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.AgentPickerProjection");
        logger?.LogDebug(
            "[AgentPicker] subscribe id={Id} hub={Hub} queries=[{Queries}]",
            queryId, hub.Address, string.Join(" | ", queries));
        return hub.GetQuery(queryId, queries)
            .Do(snapshot =>
            {
                var nodes = snapshot as IReadOnlyCollection<MeshNode> ?? snapshot.ToList();
                logger?.LogDebug(
                    "[AgentPicker] raw snapshot id={Id} count={Count} types=[{Types}]",
                    queryId, nodes.Count,
                    string.Join(",", nodes.GroupBy(n => n.NodeType ?? "(null)")
                        .Select(g => $"{g.Key}={g.Count()}")));
            });
    }

    /// <summary>
    /// Projects the synced-query snapshot into the agent picker's bound list.
    /// Same shape as <c>ThreadChatView.OnSyncedAgentSnapshot</c>:
    /// Agent-typed nodes only; tolerates <see cref="JsonElement"/> Content
    /// when the receiving hub's typed registry doesn't have
    /// <see cref="AgentConfiguration"/> wired up; sorts by Order then Name.
    /// </summary>
    public static IReadOnlyList<AgentDisplayInfo> ProjectAgents(
        IEnumerable<MeshNode> snapshot, JsonSerializerOptions jsonOptions)
    {
        var byPath = new Dictionary<string, AgentDisplayInfo>(StringComparer.Ordinal);
        foreach (var node in snapshot)
        {
            if (node.Path == null) continue;
            if (!string.Equals(node.NodeType, AgentNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                continue;

            var info = ToAgentDisplayInfo(node, jsonOptions);
            if (info != null)
                byPath[node.Path] = info;
        }

        // Group by harness (GroupName) so the picker shows major categories —
        // Claude Code / GitHub Copilot / MeshWeaver — with each harness's agents
        // contiguous (SimpleDropdown renders a header when the group key changes).
        // Alphabetical group order happens to give Claude Code, GitHub Copilot,
        // MeshWeaver; within a group, Order then Name (Assistant's order:-1 leads MeshWeaver).
        return byPath.Values
            .OrderBy(a => a.GroupName ?? Harnesses.MeshWeaver, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Order)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The utility model tier marks a programmatic generator agent (ThreadNamer,
    /// NodeInitializer, DescriptionWriter) — invoked by services, never a chat participant.</summary>
    public const string UtilityModelTier = "utility";

    /// <summary>
    /// True when the agent is a background GENERATOR (<c>modelTier: utility</c>) — ThreadNamer,
    /// NodeInitializer, DescriptionWriter. These emit structured "Name:/Id:/Svg:" output and are
    /// invoked programmatically (<see cref="IconGenerator"/>, <see cref="DescriptionGenerator"/>),
    /// so they must be hidden from every CONVERSATIONAL surface (the chat agent picker, <c>/agent</c>,
    /// and <c>@</c>-references) — otherwise e.g. ThreadNamer answers a user's "hi" with "Name: …\nId: …".
    ///
    /// <para>The filter is applied at the chat UI (<c>ThreadChatView.OnAgentList</c>), NOT inside
    /// <see cref="ProjectAgents"/>: the generators build their OWN <see cref="AgentChatClient"/> and
    /// <c>SetSelectedAgent("NodeInitializer"/"DescriptionWriter")</c>, so the projection must keep
    /// utility agents for them to resolve.</para>
    /// </summary>
    public static bool IsUtilityAgent(AgentDisplayInfo info) =>
        string.Equals(info.AgentConfiguration?.ModelTier, UtilityModelTier, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Projects the synced-query snapshot into the model picker's bound list.
    /// Mirrors <c>ThreadChatView.OnSyncedAgentSnapshot</c>'s LanguageModel
    /// branch (without the factory-baseline merge — that lives in the
    /// view's <c>RebuildAvailableModels</c>).
    /// </summary>
    public static IReadOnlyList<ModelInfo> ProjectModels(
        IEnumerable<MeshNode> snapshot, JsonSerializerOptions jsonOptions)
    {
        var byPath = new Dictionary<string, ModelInfo>(StringComparer.Ordinal);
        foreach (var node in snapshot)
        {
            if (node.Path == null) continue;
            if (!string.Equals(node.NodeType, LanguageModelNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                continue;

            var info = ToModelInfo(node, jsonOptions);
            if (info != null)
                byPath[node.Path] = info;
        }

        return byPath.Values
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Single MeshNode → AgentDisplayInfo projection. Same Content
    /// switch as the chat view: typed AgentConfiguration first, raw
    /// JsonElement fallback (when the source hub didn't have
    /// AddAITypes applied), null otherwise.
    /// </summary>
    public static AgentDisplayInfo? ToAgentDisplayInfo(
        MeshNode node, JsonSerializerOptions jsonOptions)
    {
        var config = node.Content switch
        {
            AgentConfiguration ac => ac,
            JsonElement je => TryDeserialise<AgentConfiguration>(je, jsonOptions),
            _ => null,
        };
        if (config == null) return null;
        // Display metadata is sourced from the MeshNode (the single source of truth);
        // only agent-specific bits (CustomIconSvg, the config itself) come from Content.
        return new AgentDisplayInfo
        {
            Name = node.Name ?? config.Id,
            Path = node.Path,
            Description = node.Description ?? config.Description ?? "",
            GroupName = node.Category,
            Order = node.Order ?? 0,
            Icon = node.Icon,
            CustomIconSvg = config.CustomIconSvg,
            AgentConfiguration = config,
        };
    }

    /// <summary>
    /// Single MeshNode → ModelInfo projection. JsonElement fallback
    /// covers the same source-hub-typed-registry-mismatch edge case
    /// as <see cref="ToAgentDisplayInfo"/>.
    /// </summary>
    public static ModelInfo? ToModelInfo(
        MeshNode node, JsonSerializerOptions jsonOptions)
    {
        var def = node.Content switch
        {
            ModelDefinition md => md,
            JsonElement je => TryDeserialise<ModelDefinition>(je, jsonOptions),
            _ => null,
        };
        // Carry the node PATH onto the ModelInfo (like ToAgentDisplayInfo does for agents) —
        // a model selection must persist the node path onto the composer's ModelName, not the
        // bare model id, so the MeshNode picker resolves it. Without this the /model selection
        // wrote only the name and the picker couldn't resolve the node (the "dialog breaks" bug).
        return def?.ToModelInfo() is { } info ? info with { Path = node.Path } : null;
    }

    private static T? TryDeserialise<T>(JsonElement je, JsonSerializerOptions jsonOptions) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(je.GetRawText(), jsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
