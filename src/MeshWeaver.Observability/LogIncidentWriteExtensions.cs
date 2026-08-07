using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Observability;

/// <summary>
/// The one way incident content is written.
/// </summary>
internal static class LogIncidentWriteExtensions
{
    /// <summary>
    /// Applies <paramref name="mutate"/> to the incident's <b>live</b> content and writes the
    /// result.
    ///
    /// <para>🚨 The mutation runs INSIDE the update lambda, against the node as it is at write
    /// time — never against a snapshot the caller read earlier. That matters because two writers
    /// are always in play: the ingest path is folding recurrences (occurrences, pods, samples)
    /// while the control plane is moving the lifecycle (status, draft, issue link). A caller that
    /// captured the content, changed one field and wrote the whole object back would diff its stale
    /// snapshot against current and silently revert the other writer's fields. Mutating in-lambda
    /// keeps the emitted RFC 7396 merge patch limited to what actually changed, which is what makes
    /// concurrent writers safe (AGENTS.md → "touch only the fields you intend to change").</para>
    ///
    /// <para>Cold — the caller must subscribe.</para>
    /// </summary>
    public static IObservable<MeshNode> UpdateIncident(
        this IMessageHub hub, string path, Func<LogIncident, LogIncident> mutate, ILogger? logger = null)
        => hub.GetWorkspace().GetMeshNodeStream(path).Update(node =>
        {
            var current = node.ContentAs<LogIncident>(hub.JsonSerializerOptions, logger);
            if (current is null)
            {
                // Nothing to mutate — returning the node unchanged emits an empty patch rather than
                // stamping a default-valued incident over content we could not read.
                logger?.LogWarning("Incident {Path} has no readable content — skipping the update", path);
                return node;
            }
            return node with { Content = mutate(current) };
        });
}
