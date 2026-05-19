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
/// pipe with the strings <see cref="BuildQueries"/> returns and run the
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
    /// THE single source of truth for both agent and model picker query
    /// strings. Three queries per kind, all carrying the same
    /// <c>nodeType:</c> filter, varying only on namespace + scope:
    /// <list type="number">
    ///   <item>built-in: <c>namespace:Agent / namespace:Model</c></item>
    ///   <item>per-context: <c>namespace:{currentPath} scope:selfAndAncestors</c></item>
    ///   <item>per-NodeType: <c>namespace:{nodeTypePath} scope:selfAndAncestors</c></item>
    /// </list>
    ///
    /// <para>Every consumer (chat picker UI, AgentChatClient, AzureClaude
    /// driver factory) calls <see cref="BuildAgentQueries"/> or
    /// <see cref="BuildModelQueries"/> — never inline a query string. Drop
    /// any localized definition you find.</para>
    /// </summary>
    public static string[] BuildAgentQueries(string? currentPath = null, string? nodeTypePath = null)
        => BuildQueries(AgentNodeType.NodeType, AgentRootNamespace, currentPath, nodeTypePath);

    /// <inheritdoc cref="BuildAgentQueries" />
    /// <remarks>
    /// Follows the canonical picker-query shape (see
    /// <see cref="MeshWeaver.Documentation.Data.Architecture.SyncedMeshNodeQueries">SyncedMeshNodeQueries</see>):
    /// every query carries the SAME <c>nodeType:</c> filter, varying
    /// only on namespace + scope. Mixed-filter multi-query is what trips
    /// the synced-collection's all-Initial gating.
    /// <list type="number">
    ///   <item>System catalog under <c>_Provider/</c> — both
    ///         <c>nodeType:ModelProvider</c> (at <c>_Provider/{name}</c>)
    ///         AND the LanguageModel children (at
    ///         <c>_Provider/{name}/{modelId}</c>) surface via
    ///         <c>scope:descendants</c>.</item>
    ///   <item>Per-context: <c>{currentPath}/_Provider</c> subtree.</item>
    ///   <item>Per-NodeType: <c>{nodeTypePath}/_Provider</c> subtree.</item>
    /// </list>
    /// </remarks>
    public static string[] BuildModelQueries(string? currentPath = null, string? nodeTypePath = null)
    {
        var typeFilter = $"{LanguageModelNodeType.NodeType}|{ModelProviderNodeType.NodeType}";
        var queries = new List<string>
        {
            $"namespace:{ModelProviderNodeType.RootNamespace} nodeType:{typeFilter} scope:descendants",
        };
        if (!string.IsNullOrEmpty(currentPath))
            queries.Add($"namespace:{currentPath}/{ModelProviderNodeType.RootNamespace} nodeType:{typeFilter} scope:descendants");
        if (!string.IsNullOrEmpty(nodeTypePath))
            queries.Add($"namespace:{nodeTypePath}/{ModelProviderNodeType.RootNamespace} nodeType:{typeFilter} scope:descendants");
        return queries.ToArray();
    }

    private static string[] BuildQueries(string nodeType, string rootNamespace, string? currentPath, string? nodeTypePath)
    {
        var queries = new List<string>
        {
            $"namespace:{rootNamespace} nodeType:{nodeType}",
        };
        if (!string.IsNullOrEmpty(currentPath))
            queries.Add($"namespace:{currentPath} nodeType:{nodeType} scope:selfAndAncestors");
        if (!string.IsNullOrEmpty(nodeTypePath))
            queries.Add($"namespace:{nodeTypePath} nodeType:{nodeType} scope:selfAndAncestors");
        return queries.ToArray();
    }

    /// <summary>
    /// 🚨 The EXACT pipeline the chat agent combobox is bound to. The view
    /// subscribes to this; tests subscribe to this. No parallel
    /// reconstruction of queries / projection in either place.
    ///
    /// <para>Every link in the chain logs into channel
    /// <c>MeshWeaver.AI.AgentPickerProjection</c>: subscribe, raw-snapshot
    /// count, projected count. If the dropdown is empty in production,
    /// open Aspire dashboard → memex-portal-distributed → Logs and grep
    /// for <c>[AgentPicker]</c>: that tells you whether the query
    /// returned 0 nodes (provider/scope problem), or nodes were returned
    /// but the projection dropped them (Content shape problem), or
    /// subscription never fired (workspace null / circuit lifecycle).</para>
    /// </summary>
    public static IObservable<IReadOnlyList<AgentDisplayInfo>> ObserveAgents(
        IWorkspace workspace, IMessageHub hub,
        string? currentPath = null, string? nodeTypePath = null)
        => ObserveSnapshot(workspace, hub,
                BuildQueryId(AgentsQueryId, currentPath, nodeTypePath),
                BuildAgentQueries(currentPath, nodeTypePath))
            .Select(snapshot => ProjectAgents(snapshot, hub.JsonSerializerOptions));

    /// <summary>
    /// 🚨 The EXACT pipeline the chat model combobox is bound to. The view
    /// subscribes to this; tests subscribe to this.
    /// Same logging shape as <see cref="ObserveAgents"/>.
    /// </summary>
    public static IObservable<IReadOnlyList<ModelInfo>> ObserveModels(
        IWorkspace workspace, IMessageHub hub,
        string? currentPath = null, string? nodeTypePath = null)
        => ObserveSnapshot(workspace, hub,
                BuildQueryId(ModelsQueryId, currentPath, nodeTypePath),
                BuildModelQueries(currentPath, nodeTypePath))
            .Select(snapshot => ProjectModels(snapshot, hub.JsonSerializerOptions));

    /// <summary>
    /// The workspace.GetQuery registry caches by id — first call wins, every
    /// subsequent call with the same id but different queries returns the cached
    /// observable. So agent / model queries that vary by context (currentPath +
    /// nodeTypePath) MUST use a context-scoped id; otherwise re-init after
    /// SetContext returns the stale snapshot from the first subscribe and
    /// NodeType-defined agents stay invisible.
    /// </summary>
    private static string BuildQueryId(string baseId, string? currentPath, string? nodeTypePath) =>
        $"{baseId}|p={currentPath ?? ""}|t={nodeTypePath ?? ""}";

    /// <summary>
    /// Live MeshNode snapshot for one of the named picker queries — single
    /// <see cref="MeshWeaver.Graph.SyncedQueryDataSourceExtensions.GetQuery(MeshWeaver.Data.IWorkspace, object, string[])"/>
    /// call so the workspace cache shares the same upstream subscription
    /// across every consumer (chat view, AgentChatClient, AzureClaude
    /// driver factory). The mesh query engine unions the queries internally.
    /// </summary>
    public static IObservable<IEnumerable<MeshNode>> ObserveSnapshot(
        IWorkspace workspace, IMessageHub hub, string queryId, params string[] queries)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.AgentPickerProjection");
        logger?.LogDebug(
            "[AgentPicker] subscribe id={Id} hub={Hub} workspaceHub={WsHub} queries=[{Queries}]",
            queryId, hub.Address, workspace.Hub.Address,
            string.Join(" | ", queries));
        return workspace.GetQuery(queryId, queries)
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

        return byPath.Values
            .OrderBy(a => a.Order)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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
        return new AgentDisplayInfo
        {
            Name = config.DisplayName ?? config.Id,
            Path = node.Path,
            Description = config.Description ?? "",
            GroupName = config.GroupName,
            Order = config.Order,
            Icon = config.Icon,
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
        return def?.ToModelInfo();
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
