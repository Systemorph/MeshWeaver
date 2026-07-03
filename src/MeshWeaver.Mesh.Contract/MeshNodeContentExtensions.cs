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
    /// typed with a DIFFERENT CLR type of the same shape (the same class compiled
    /// into two dynamic node assemblies, or a same-named type resolved by another
    /// hub's registry at the query boundary) → recovered by a JSON round-trip;
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
                // Content is a typed object, but not OUR T: the same class compiled into a different
                // dynamic node assembly (@@-included copies), or a same-short-named type another hub's
                // registry resolved when the node crossed the query/message boundary. The VALUE is
                // perfectly recoverable — round-trip through JSON and bind into the caller's T. The
                // silent null here was the atioz "BalanceSheet dashboards render empty" outage
                // (agentic-pensions#12): 200 fact nodes arrived typed with the fact NodeType's own
                // assembly and the dashboard's loader dropped every one.
                try
                {
                    var element = JsonSerializer.SerializeToElement(node.Content, options);
                    var recovered = element.Deserialize<T>(options);
                    if (recovered is not null)
                    {
                        // Debug, not Warning: this fires per node per read on the cross-assembly path
                        // (a healthy, recoverable shape) — Warning would storm at snapshot size × emission.
                        logger?.LogDebug(
                            "ContentAs<{TargetType}> for {Path}: recovered Content typed as foreign {ActualType} "
                            + "({ActualAssembly}) via JSON round-trip",
                            typeof(T).Name, node.Path, node.Content.GetType().Name,
                            node.Content.GetType().Assembly.GetName().Name);
                        return recovered;
                    }
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    // fall through to the loud log below
                }
                logger?.LogError(
                    "ContentAs<{TargetType}> for {Path}: Content is {ActualType}, not convertible",
                    typeof(T).Name, node.Path, node.Content.GetType().Name);
                return null;
        }
    }
}
