using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.Json;

/// <summary>Diffing and applying RFC 6902 patches.</summary>
public static class PatchExtensions
{
    /// <summary>
    /// Produces the RFC 6902 patch that transforms <paramref name="original"/> into
    /// <paramref name="target"/>, by serializing both and diffing the JSON.
    /// </summary>
    public static JsonPatch CreatePatch<TOriginal, TTarget>(
        this TOriginal original, TTarget target, JsonSerializerOptions? options = null)
    {
        var originalNode = JsonSerializer.SerializeToNode(original, options);
        var targetNode = JsonSerializer.SerializeToNode(target, options);
        return originalNode.CreatePatch(targetNode);
    }

    /// <summary>
    /// Produces the RFC 6902 patch that transforms <paramref name="original"/> into
    /// <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// <para>The walk is deliberately the simple structural one, NOT an LCS/Myers diff:</para>
    /// <list type="bullet">
    /// <item>objects — the original's properties in order (recurse when present in the target,
    /// else <c>remove</c>), then the target's new properties as <c>add</c>;</item>
    /// <item>arrays — a whole-array <c>replace</c> when exactly one side is empty; otherwise a
    /// positional walk, ascending with trailing <c>add</c>s when growing, descending with
    /// trailing <c>remove</c>s when shrinking;</item>
    /// <item>anything else — a single <c>replace</c> when not equivalent.</item>
    /// </list>
    /// <para>
    /// 🚨 That shape is the CONTRACT, not an implementation detail. The emitted bytes are what the
    /// React, gRPC-web and Python clients consume, and those clients are not built by this repo's
    /// CI — a "smarter" diff that emits <c>move</c>s or a different op order would be a silent
    /// wire break. <c>JsonPatchWireFormatTest</c> pins it against fixtures captured from the
    /// implementation these clients were built against.
    /// </para>
    /// </remarks>
    public static JsonPatch CreatePatch(this JsonNode? original, JsonNode? target)
    {
        var operations = new List<PatchOperation>();
        Diff(operations, original, target, JsonPointer.Empty);
        return new JsonPatch(operations);
    }

    /// <summary>Applies <paramref name="patch"/> to <paramref name="obj"/> via JSON round-trip.</summary>
    /// <exception cref="InvalidOperationException">The patch could not be applied.</exception>
    public static T? Apply<T>(this JsonPatch patch, T obj, JsonSerializerOptions? options = null)
        => patch.Apply<T, T>(obj, options);

    /// <summary>Applies <paramref name="patch"/> to <paramref name="obj"/> and deserializes the result.</summary>
    /// <exception cref="InvalidOperationException">The patch could not be applied.</exception>
    public static TTarget? Apply<TOriginal, TTarget>(this JsonPatch patch, TOriginal obj, JsonSerializerOptions? options = null)
    {
        var source = JsonSerializer.SerializeToNode(obj, options);
        var result = patch.Apply(source);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"{result.Error} Operation: {result.Operation}");
        return result.Result.Deserialize<TTarget>(options);
    }

    private static void Diff(List<PatchOperation> patch, JsonNode? original, JsonNode? target, JsonPointer path)
    {
        if (original is JsonObject originalObject && target is JsonObject targetObject)
            DiffObject(originalObject, targetObject, patch, path);
        else if (original is JsonArray originalArray && target is JsonArray targetArray)
            DiffArray(originalArray, targetArray, patch, path);
        else if (!original.IsEquivalentTo(target))
            patch.Add(PatchOperation.Replace(path, target));
    }

    private static void DiffObject(JsonObject original, JsonObject target, List<PatchOperation> patch, JsonPointer path)
    {
        foreach (var (key, value) in original)
        {
            if (target.TryGetPropertyValue(key, out var targetValue))
                Diff(patch, value, targetValue, path.Combine(key));
            else
                patch.Add(PatchOperation.Remove(path.Combine(key)));
        }

        foreach (var (key, value) in target)
        {
            if (!original.ContainsKey(key))
                patch.Add(PatchOperation.Add(path.Combine(key), value));
        }
    }

    private static void DiffArray(JsonArray original, JsonArray target, List<PatchOperation> patch, JsonPointer path)
    {
        if (original.Count == 0 ^ target.Count == 0)
        {
            patch.Add(PatchOperation.Replace(path, target));
            return;
        }

        if (target.Count >= original.Count)
        {
            for (var i = 0; i < target.Count; i++)
            {
                if (i < original.Count)
                    Diff(patch, original[i], target[i], path.Combine(i));
                else
                    patch.Add(PatchOperation.Add(path.Combine(i), target[i]));
            }
            return;
        }

        for (var i = original.Count - 1; i >= 0; i--)
        {
            if (i < target.Count)
                Diff(patch, original[i], target[i], path.Combine(i));
            else
                patch.Add(PatchOperation.Remove(path.Combine(i)));
        }
    }
}
