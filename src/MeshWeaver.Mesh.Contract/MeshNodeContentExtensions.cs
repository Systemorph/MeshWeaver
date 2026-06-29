using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Bad-data-tolerant reads of <see cref="MeshNode.Content"/>. The polymorphic
/// converter degrades an unknown/incompatible <c>$type</c> to a raw
/// <see cref="JsonElement"/> instead of throwing; this recovers it into the known
/// target type at the read site (a stale <c>$type</c> is just an ignored member).
/// </summary>
public static class MeshNodeContentExtensions
{
    /// <summary>
    /// Content as <typeparamref name="T"/>: already-typed → as-is; a degraded
    /// <see cref="JsonElement"/> → deserialized into <typeparamref name="T"/>;
    /// otherwise <c>null</c> (logged loud — never silently swallowed).
    /// </summary>
    public static T? ContentAs<T>(this MeshNode? node, JsonSerializerOptions options, ILogger? logger = null)
        where T : class
    {
        switch (node?.Content)
        {
            case null:
                return null;
            case T typed:
                return typed;
            case JsonElement je:
                try
                {
                    return je.Deserialize<T>(options);
                }
                catch (JsonException ex)
                {
                    // Don't rethrow (a throw on read faults the node), don't swallow:
                    // log loud with the path + raw JSON so the corruption is visible.
                    logger?.LogError(ex,
                        "ContentAs<{TargetType}> could not recover Content for {Path}: {RawJson}",
                        typeof(T).Name, node.Path, je.GetRawText());
                    return null;
                }
            default:
                logger?.LogError(
                    "ContentAs<{TargetType}> for {Path}: Content is {ActualType}, not convertible",
                    typeof(T).Name, node.Path, node.Content.GetType().Name);
                return null;
        }
    }
}
