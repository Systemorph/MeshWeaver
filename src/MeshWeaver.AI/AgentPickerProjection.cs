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
    /// <summary>
    /// Query strings for the AGENT synced subscription. Built-in
    /// <c>namespace:Agent</c> plus an optional <c>scope:selfAndAncestors</c>
    /// query for per-context custom agents.
    /// </summary>
    public static string[] BuildAgentQueries(string? initialContext)
    {
        var queries = new List<string>
        {
            $"namespace:{AgentRootNamespace} nodeType:{AgentNodeType.NodeType}",
        };
        if (!string.IsNullOrEmpty(initialContext))
            queries.Add($"namespace:{initialContext} nodeType:{AgentNodeType.NodeType} scope:selfAndAncestors");
        return queries.ToArray();
    }

    /// <summary>
    /// Query strings for the MODEL synced subscription. Built-in
    /// <c>namespace:Model</c> plus an optional context-scoped query for
    /// per-partition custom Model nodes.
    /// </summary>
    public static string[] BuildModelQueries(string? initialContext)
    {
        var queries = new List<string>
        {
            $"namespace:{LanguageModelNodeType.RootNamespace} nodeType:{LanguageModelNodeType.NodeType}",
        };
        if (!string.IsNullOrEmpty(initialContext))
            queries.Add($"namespace:{initialContext} nodeType:{LanguageModelNodeType.NodeType} scope:selfAndAncestors");
        return queries.ToArray();
    }

    /// <summary>Conventional namespace for built-in agents (matches <c>BuiltInAgentProvider</c>).</summary>
    public const string AgentRootNamespace = "Agent";

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
        IWorkspace workspace, IMessageHub hub, string? initialContext)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.AgentPickerProjection");
        var queries = BuildAgentQueries(initialContext);
        var id = $"chat-agents:{initialContext ?? string.Empty}";
        logger?.LogDebug(
            "[AgentPicker] subscribe agents id={Id} hub={Hub} workspaceHub={WsHub} queries=[{Queries}]",
            id, hub.Address, workspace.Hub.Address,
            string.Join(" | ", queries));
        return workspace.GetQuery(id, queries)
            .Do(snapshot =>
            {
                var nodes = snapshot as IReadOnlyCollection<MeshNode> ?? snapshot.ToList();
                logger?.LogDebug(
                    "[AgentPicker] raw agent snapshot id={Id} count={Count} types=[{Types}]",
                    id, nodes.Count,
                    string.Join(",", nodes.GroupBy(n => n.NodeType ?? "(null)")
                        .Select(g => $"{g.Key}={g.Count()}")));
            })
            .Select(snapshot => ProjectAgents(snapshot, hub.JsonSerializerOptions))
            .Do(agents => logger?.LogDebug(
                "[AgentPicker] projected agents id={Id} count={Count}", id, agents.Count));
    }

    /// <summary>
    /// 🚨 The EXACT pipeline the chat model combobox is bound to. The view
    /// subscribes to this; tests subscribe to this.
    /// Same logging shape as <see cref="ObserveAgents"/>.
    /// </summary>
    public static IObservable<IReadOnlyList<ModelInfo>> ObserveModels(
        IWorkspace workspace, IMessageHub hub, string? initialContext)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.AgentPickerProjection");
        var queries = BuildModelQueries(initialContext);
        var id = $"chat-models:{initialContext ?? string.Empty}";
        logger?.LogDebug(
            "[AgentPicker] subscribe models id={Id} hub={Hub} workspaceHub={WsHub} queries=[{Queries}]",
            id, hub.Address, workspace.Hub.Address,
            string.Join(" | ", queries));
        return workspace.GetQuery(id, queries)
            .Do(snapshot =>
            {
                var nodes = snapshot as IReadOnlyCollection<MeshNode> ?? snapshot.ToList();
                logger?.LogDebug(
                    "[AgentPicker] raw model snapshot id={Id} count={Count} types=[{Types}]",
                    id, nodes.Count,
                    string.Join(",", nodes.GroupBy(n => n.NodeType ?? "(null)")
                        .Select(g => $"{g.Key}={g.Count()}")));
            })
            .Select(snapshot => ProjectModels(snapshot, hub.JsonSerializerOptions))
            .Do(models => logger?.LogDebug(
                "[AgentPicker] projected models id={Id} count={Count}", id, models.Count));
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
