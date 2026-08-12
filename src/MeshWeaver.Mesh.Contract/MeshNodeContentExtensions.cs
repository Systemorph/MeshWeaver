using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Bad-data-tolerant reads of <see cref="MeshNode.Content"/> — <see cref="ObjectAsExtensions.As{T}"/>
/// with the node's path in the diagnostics. <b>This is THE way to read a node's content as a typed
/// instance; <c>node.Content is T</c> / <c>as T</c> is the trap-door</b> and yields a silent
/// <c>null</c> whenever the content arrives as untyped JSON, as the as-written DOM, or typed by a
/// different build of the same record. See <see cref="ObjectAsExtensions"/> for why each of those
/// happens in a running mesh, and for the rule that comes first: read where the type is registered
/// — usually by letting the owning hub handle it.
/// </summary>
public static class MeshNodeContentExtensions
{
    /// <summary>
    /// Content as <typeparamref name="T"/>: already-typed → as-is; a degraded
    /// <see cref="JsonElement"/> or <c>JsonNode</c> → deserialized into <typeparamref name="T"/>;
    /// typed with a SAME-NAMED but different CLR type (the same class compiled into two dynamic
    /// node assemblies, or resolved by another hub's registry at the query boundary) → recovered by
    /// a JSON round-trip; typed with a DIFFERENTLY-named type → <c>null</c> (call sites
    /// probe-dispatch on that); otherwise <c>null</c> (logged loud — never silently swallowed).
    /// </summary>
    /// <param name="node">The node whose content to read.</param>
    /// <param name="options">The owning hub's <c>JsonSerializerOptions</c> — the registry behind
    /// them is what resolves the content's <c>$type</c>.</param>
    /// <param name="logger">Optional; without it an unconvertible value still returns null, just
    /// without the diagnosis.</param>
    public static T? ContentAs<T>(this MeshNode? node, JsonSerializerOptions options, ILogger? logger = null)
        where T : class
        // Node path as the log subject: a dynamic node assembly compiles without a namespace, so
        // the bare type name is identical on both sides of a cross-assembly mismatch and the path
        // is the only thing that says WHICH node degraded.
        => node?.Content.As<T>(options, logger, node.Path);
}
