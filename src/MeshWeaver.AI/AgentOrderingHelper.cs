using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive;

namespace MeshWeaver.AI;

/// <summary>
/// Shared helper for querying and ordering agents by relevance to the current context.
/// Agent list retrieval ALWAYS flows through <see cref="AgentPickerProjection.ObserveAgents"/>
/// → <c>workspace.GetQuery</c> — the synced pipeline that fans out across all
/// static MeshNode providers, dedupes, and gates on the all-Initial event.
/// </summary>
public static class AgentOrderingHelper
{
    /// <summary>
    /// Reactive agent listing. Wraps <see cref="AgentPickerProjection.ObserveAgents"/>
    /// (the canonical <c>workspace.GetQuery</c>-backed synced source) and emits the
    /// agents ordered by <see cref="OrderByRelevance"/>. Every consumer — picker UI,
    /// AgentDetailsArea, AzureClaude driver, tests — subscribes here, never to
    /// <c>IMeshService.ObserveQuery</c> directly.
    /// </summary>
    public static IObservable<IReadOnlyList<AgentDisplayInfo>> ObserveAgents(
        IWorkspace workspace,
        IMessageHub hub,
        string? contextPath,
        string? nodeTypePath)
        => AgentPickerProjection.ObserveAgents(workspace, hub, contextPath, nodeTypePath)
            .Select(agents => (IReadOnlyList<AgentDisplayInfo>)OrderByRelevance(agents, contextPath, nodeTypePath));

    /// <summary>
    /// <b>Test-only legacy shim.</b> Tests in <c>AgentSelectionTest</c> still
    /// mock <see cref="IMeshService.ObserveQuery"/> directly; this preserves
    /// the shape they expect. Production code MUST use <see cref="ObserveAgents"/>
    /// (which goes through <c>workspace.GetQuery</c>). Two queries, both with
    /// <c>nodeType:Agent</c>, varying on path + scope (the same shape the
    /// production picker projection uses).
    /// </summary>
    [Obsolete("Production code must use ObserveAgents(workspace, hub, ...). This shim is preserved only for legacy IMeshService-mocking tests.")]
    public static async Task<IReadOnlyList<AgentDisplayInfo>> QueryAgentsAsync(
        IMeshService? meshQuery,
        string? contextPath,
        string? nodeTypePath)
    {
        var agentsDict = ImmutableDictionary<string, (AgentConfiguration Config, string Path)>.Empty;

        if (meshQuery != null && !string.IsNullOrEmpty(nodeTypePath))
        {
            try
            {
                var query = $"path:{nodeTypePath} nodeType:Agent scope:ancestors";
                var stream = meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
                    .Take(1)
                    .SelectMany(c => c.Items.ToObservable())
                    .ToAsyncEnumerableSequence();
                await foreach (var node in stream)
                {
                    if (node.Content is AgentConfiguration config && !agentsDict.ContainsKey(config.Id))
                        agentsDict = agentsDict.SetItem(config.Id, (config, node.Path ?? ""));
                }
            }
            catch { /* ignore */ }
        }

        if (meshQuery != null)
        {
            try
            {
                var query = string.IsNullOrEmpty(contextPath)
                    ? "nodeType:Agent"
                    : $"path:{contextPath} nodeType:Agent scope:ancestors";
                var stream = meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
                    .Take(1)
                    .SelectMany(c => c.Items.ToObservable())
                    .ToAsyncEnumerableSequence();
                await foreach (var node in stream)
                {
                    if (node.Content is AgentConfiguration config && !agentsDict.ContainsKey(config.Id))
                        agentsDict = agentsDict.SetItem(config.Id, (config, node.Path ?? ""));
                }
            }
            catch { /* ignore */ }
        }

        return agentsDict.Values
            .Select(x => new AgentDisplayInfo
            {
                Name = x.Config.Id,
                Path = x.Path,
                Description = x.Config.Description ?? x.Config.DisplayName ?? x.Config.Id,
                GroupName = x.Config.GroupName,
                Order = x.Config.Order,
                IndentLevel = 0,
                Icon = x.Config.Icon,
                CustomIconSvg = x.Config.CustomIconSvg,
                AgentConfiguration = x.Config
            })
            .ToImmutableList();
    }

    /// <summary>
    /// Gets the NodeType for a given context path.
    /// <para>
    /// <b>Test-only legacy.</b> Production code (e.g. <see cref="AgentChatClient"/>)
    /// MUST read single nodes via <c>hub.GetWorkspace().GetMeshNodeStream(path)</c>
    /// — the per-node MeshNodeReference reducer is authoritative and live; the
    /// catalog read-side index used by <c>QueryAsync</c> lags writes and returns
    /// stale content. See Doc/Architecture/AsynchronousCalls.md.
    /// </para>
    /// </summary>
    public static async Task<string?> GetNodeTypeAsync(IMeshService? meshQuery, string? contextPath)
    {
        if (meshQuery == null || string.IsNullOrEmpty(contextPath))
            return null;

        try
        {
            var stream = meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{contextPath}"))
                .Take(1)
                .SelectMany(c => c.Items.ToObservable())
                .ToAsyncEnumerableSequence();
            await foreach (var node in stream)
            {
                if (!string.IsNullOrEmpty(node.NodeType) && node.NodeType != "Agent" && node.NodeType != "Markdown")
                {
                    return node.NodeType;
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }

    /// <summary>
    /// Orders agents by Order then by DisplayName.
    /// </summary>
    public static IReadOnlyList<AgentDisplayInfo> OrderByRelevance(
        IEnumerable<AgentDisplayInfo> agents,
        string? contextPath,
        string? nodeTypePath)
    {
        return agents
            .OrderBy(a => a.Order)
            .ThenBy(a => a.AgentConfiguration.DisplayName ?? a.Name)
            .ToImmutableList();
    }
}
