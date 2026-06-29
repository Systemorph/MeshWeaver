using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using MeshWeaver.Mesh;

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
    /// <c>IMeshService.Query</c> directly.
    /// </summary>
    public static IObservable<IReadOnlyList<AgentDisplayInfo>> ObserveAgents(
        IMessageHub hub,
        string? userPath,
        string? spacePath)
        => AgentPickerProjection.ObserveAgents(hub, userPath, spacePath)
            .Select(agents => (IReadOnlyList<AgentDisplayInfo>)OrderByRelevance(agents, spacePath, null));

    /// <summary>
    /// Orders agents by Order then by display name (both sourced from the MeshNode
    /// via <see cref="AgentDisplayInfo"/>).
    /// </summary>
    public static IReadOnlyList<AgentDisplayInfo> OrderByRelevance(
        IEnumerable<AgentDisplayInfo> agents,
        string? contextPath,
        string? nodeTypePath)
    {
        return agents
            .OrderBy(a => a.Order)
            .ThenBy(a => a.Name)
            .ToImmutableList();
    }
}
