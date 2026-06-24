using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// The binding seam that makes a <b>node-bound DataContext</b> (see
/// <see cref="LayoutAreaReference.MeshNodePrefix"/>) read from and write straight back to a live
/// <see cref="MeshNode"/> via <c>hub.GetMeshNodeStream(path)</c> (the process-wide
/// <c>IMeshNodeStreamCache</c>) — ONE source of truth, no layout-area <c>/data</c> replica and no
/// debounced save-subscription.
///
/// <para>This is the CONTROL-LEVEL binding primitive: it lives next to
/// <see cref="MeshNodeStreamExtensions.GetMeshNodeStream(MeshWeaver.Data.IWorkspace,string)"/> (NOT in
/// the Blazor view layer) so it is a plain static extension testable without a Blazor render host. The
/// GUI seams <c>BlazorView.DataBind</c> (read) and <c>BlazorView.UpdatePointer</c> (write) branch here
/// when the DataContext is node-bound, so every form control inherits node binding for free; the Monaco
/// editor views (<c>MarkdownEditorView</c> / <c>CodeEditorView</c> / <c>NotebookEditorView</c>), which
/// bind their single <c>Value</c> pointer themselves rather than through <c>BlazorView.DataBind</c>,
/// call <see cref="IsNodeBound"/> + <see cref="Bind"/> directly.</para>
///
/// <para><b>Why it must NOT be resolved via the layout <c>Stream</c>:</b> a node-bound pointer is
/// <c>/$meshNode/{base64url(nodePath)}/{c|n}/…</c>. For node <c>AgenticPension</c> the path segment is
/// <c>QWdlbnRpY1BlbnNpb24</c>. <c>LayoutExtensions.GetStream&lt;T&gt;</c> treats the second pointer
/// segment as a JSON-encoded id and calls <c>JsonSerializer.Deserialize&lt;string&gt;(segment)</c> — a
/// bare Base64Url token is not a JSON-quoted string, so it throws <c>"'Q' is an invalid start of a
/// value"</c> and tears down the whole Blazor circuit. Routing through this seam reads the field off the
/// node instead, where the content actually lives.</para>
///
/// <para>Field-pointer resolution against the node JSON is <b>case-insensitive</b>, so a metadata DTO's
/// PascalCase pointer (<c>Name</c>, <c>Description</c>) and a content editor's camelCase pointer
/// (<c>harness</c>, <c>messageContent</c>) both bind without the caller having to know the JSON casing
/// of the target.</para>
/// </summary>
public static class MeshNodeBindingExtensions
{
    /// <summary>
    /// True when <paramref name="dataContext"/> is node-bound (see
    /// <see cref="LayoutAreaReference.MeshNodePrefix"/>) AND <paramref name="reference"/> is a relative
    /// field pointer — i.e. the value lives on the <see cref="MeshNode"/>, not in the layout-area
    /// <c>/data</c> store. The relative-pointer condition matches <c>BlazorView.DataBind</c>: an
    /// absolute (<c>/…</c>) pointer is a layout-area path and is never node-bound.
    /// </summary>
    public static bool IsNodeBound(string? dataContext, JsonPointerReference reference)
        => LayoutAreaReference.TryParseMeshNodeDataContext(dataContext) is not null
           && !reference.Pointer.StartsWith('/');

    /// <summary>
    /// Pure read: evaluates the field at <paramref name="reference"/> (optionally nested under
    /// <paramref name="subPath"/>) against <paramref name="node"/> — its <c>Content</c> JSON when
    /// <paramref name="bindContent"/> is <c>true</c>, otherwise the whole-node JSON — and returns the
    /// value as a <see cref="JsonElement"/> (or <c>null</c> when absent). No stream, no hub: this is the
    /// node-bound read logic in isolation, so it is unit-testable against an in-memory node.
    /// </summary>
    public static JsonElement? ResolveField(
        MeshNode node, bool bindContent, string? subPath, JsonPointerReference reference,
        JsonSerializerOptions options)
        => EvaluateField(BindingRoot(node, bindContent, options), Combine(subPath, reference.Pointer));

    /// <summary>
    /// Live stream of the value at <paramref name="reference"/> on the node at
    /// <paramref name="nodePath"/>. Emits the raw <see cref="JsonElement"/> (or <c>null</c> when the
    /// field is absent) so the caller's existing converter pipeline (<c>Hub.ConvertSingle</c> /
    /// <c>ConversionToValue</c>) deserializes it exactly as it would a <c>/data</c> value. Stays
    /// subscribed for the component lifetime — no <c>.Take(1)</c>.
    /// </summary>
    public static IObservable<object?> Bind(
        IMessageHub hub, string nodePath, bool bindContent, string? subPath, JsonPointerReference reference)
    {
        var options = hub.JsonSerializerOptions;
        var pointer = Combine(subPath, reference.Pointer);
        return hub.GetMeshNodeStream(nodePath)
            .Where(node => node is not null)
            .Select(node => (object?)EvaluateField(BindingRoot(node, bindContent, options), pointer))
            .DistinctUntilChanged(JsonElementValueComparer.Instance);
    }

    /// <summary>
    /// Writes <paramref name="value"/> into the field at <paramref name="reference"/> on the node at
    /// <paramref name="nodePath"/> via a per-field read-modify-write through the node stream. Sets
    /// ONLY the edited field; everything else on the node (and its Content) is preserved. Cold —
    /// subscribes here with explicit error logging (the framework's cold-observable + AccessContext
    /// propagation rules apply on <c>.Subscribe()</c>).
    /// </summary>
    public static void Write(
        IMessageHub hub, ILogger logger, string nodePath, bool bindContent, string? subPath,
        JsonPointerReference reference, object? value, Action<Exception>? onError = null)
    {
        var options = hub.JsonSerializerOptions;
        var valueNode = value is null ? null : JsonSerializer.SerializeToNode(value, options);
        var pointer = Combine(subPath, reference.Pointer);

        hub.GetMeshNodeStream(nodePath)
            .Update(node =>
            {
                if (bindContent)
                {
                    var content = ToJsonObject(node.Content, options) ?? new JsonObject();
                    SetField(content, pointer, valueNode);
                    return node with { Content = JsonSerializer.Deserialize<object>(content.ToJsonString(), options) };
                }

                // Whole-node mode: top-level node fields (Name/Description/Icon/Category/Order) plus
                // an optional nested content/… path. Round-trip the node through JSON, patch the
                // field, deserialize back — so an arbitrary form pointer maps onto the node's own
                // JSON shape without the editor hand-mapping every property.
                var nodeObj = JsonSerializer.SerializeToNode(node, options) as JsonObject ?? new JsonObject();
                SetField(nodeObj, pointer, valueNode);
                return JsonSerializer.Deserialize<MeshNode>(nodeObj.ToJsonString(), options) ?? node;
            })
            .Subscribe(
                _ => { },
                ex =>
                {
                    logger.LogWarning(ex,
                        "MeshNodeBinding: write failed for {Path} field {Field}", nodePath, pointer);
                    // Surface to the caller (the Blazor view pops a modal) instead of swallowing —
                    // a swallowed combobox/selection write is the silent "screen disappears on select".
                    onError?.Invoke(ex);
                });
    }

    /// <summary>Joins an optional content sub-path (e.g. <c>"composer"</c>) with a field pointer
    /// (e.g. <c>"harness"</c>) into a single JSON-pointer-style path (<c>"composer/harness"</c>).</summary>
    private static string Combine(string? subPath, string pointer)
        => string.IsNullOrEmpty(subPath) ? pointer : $"{subPath}/{pointer.TrimStart('/')}";

    /// <summary>The JSON object the form's field pointers resolve against: the node's Content
    /// (content mode) or the whole node (fields mode).</summary>
    private static JsonObject? BindingRoot(MeshNode node, bool bindContent, JsonSerializerOptions options)
        => bindContent
            ? ToJsonObject(node.Content, options)
            : JsonSerializer.SerializeToNode(node, options) as JsonObject;

    private static JsonObject? ToJsonObject(object? content, JsonSerializerOptions options)
    {
        if (content is null) return null;
        try
        {
            return content is JsonElement je
                ? JsonNode.Parse(je.GetRawText()) as JsonObject
                : JsonSerializer.SerializeToNode(content, options) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Evaluates a JSON-pointer-style field path (supports nested <c>a/b</c>) against
    /// <paramref name="root"/>, case-insensitively, returning the value as a <see cref="JsonElement"/>
    /// (or <c>null</c> when any segment is missing). Leading <c>/</c> is tolerated.
    /// </summary>
    private static JsonElement? EvaluateField(JsonObject? root, string pointer)
    {
        if (root is null) return null;
        JsonNode? node = root;
        foreach (var segment in SplitPointer(pointer))
        {
            if (node is not JsonObject obj) return null;
            node = GetCaseInsensitive(obj, segment);
            if (node is null) return null;
        }
        if (ReferenceEquals(node, root)) return null; // empty pointer → no field value
        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }

    /// <summary>Sets the field at <paramref name="pointer"/> on <paramref name="root"/> (creating
    /// intermediate objects), matching an existing key case-insensitively so we patch the SAME key
    /// the node already uses rather than adding a casing-variant duplicate.</summary>
    private static void SetField(JsonObject root, string pointer, JsonNode? value)
    {
        var segments = SplitPointer(pointer).ToArray();
        if (segments.Length == 0) return;
        var obj = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var key = ExistingKey(obj, segments[i]) ?? segments[i];
            if (obj[key] is JsonObject child)
            {
                obj = child;
            }
            else
            {
                var created = new JsonObject();
                obj[key] = created;
                obj = created;
            }
        }
        var last = ExistingKey(obj, segments[^1]) ?? segments[^1];
        obj[last] = value;
    }

    private static IEnumerable<string> SplitPointer(string pointer)
        => (pointer ?? string.Empty)
            .TrimStart('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Replace("~1", "/").Replace("~0", "~"));

    private static JsonNode? GetCaseInsensitive(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var exact))
            return exact;
        foreach (var kvp in obj)
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        return null;
    }

    private static string? ExistingKey(JsonObject obj, string key)
    {
        if (obj.ContainsKey(key)) return key;
        foreach (var kvp in obj)
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                return kvp.Key;
        return null;
    }

    /// <summary>Equality over the boxed <see cref="JsonElement"/>s the binding emits, so an echo of
    /// our own write (or a no-op re-emission) is filtered by <c>DistinctUntilChanged</c> rather than
    /// re-running the setter every node tick.</summary>
    private sealed class JsonElementValueComparer : IEqualityComparer<object?>
    {
        public static readonly JsonElementValueComparer Instance = new();

        public new bool Equals(object? x, object? y)
        {
            if (x is null && y is null) return true;
            if (x is null || y is null) return false;
            if (x is JsonElement xe && y is JsonElement ye)
                return xe.GetRawText() == ye.GetRawText();
            return x.Equals(y);
        }

        public int GetHashCode(object? obj)
            => obj is JsonElement je ? je.GetRawText().GetHashCode() : obj?.GetHashCode() ?? 0;
    }
}
