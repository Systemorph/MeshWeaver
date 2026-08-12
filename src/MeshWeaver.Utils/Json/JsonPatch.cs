using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeshWeaver.Json;

/// <summary>An RFC 6902 JSON Patch document — an ordered list of operations.</summary>
[JsonConverter(typeof(PatchJsonConverter))]
public sealed class JsonPatch : IEquatable<JsonPatch>
{
    /// <summary>The operations, applied in order.</summary>
    public IReadOnlyList<PatchOperation> Operations { get; }

    /// <summary>Creates a patch from operations.</summary>
    public JsonPatch(params PatchOperation[] operations)
        => Operations = operations.ToImmutableArray();

    /// <summary>Creates a patch from operations.</summary>
    public JsonPatch(IEnumerable<PatchOperation> operations)
        => Operations = operations.ToImmutableArray();

    /// <summary>
    /// Applies this patch to <paramref name="source"/>, leaving the input untouched.
    /// </summary>
    /// <remarks>
    /// 🚨 Unlike json-everything's implementation, pointer segments are RFC 6901 <em>decoded</em>
    /// before being used as property names, so a key containing <c>/</c> or <c>~</c> round-trips
    /// correctly. That defect is why <c>JsonSynchronizationStream</c> had to hand-roll its own
    /// applier; this one is correct.
    /// </remarks>
    /// <returns>A result carrying either the patched document or the first failure.</returns>
    public PatchResult Apply(JsonNode? source)
    {
        var current = source?.DeepClone();
        for (var i = 0; i < Operations.Count; i++)
        {
            var error = JsonPatchApplier.Apply(ref current, Operations[i]);
            if (error is not null)
                return new PatchResult(current, error, i);
        }
        return new PatchResult(current, null, Operations.Count);
    }

    /// <inheritdoc />
    public bool Equals(JsonPatch? other) =>
        other is not null && (ReferenceEquals(this, other) || Operations.SequenceEqual(other.Operations));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as JsonPatch);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var op in Operations) hash.Add(op);
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => $"[{string.Join(", ", Operations)}]";
}

/// <summary>The outcome of applying a <see cref="JsonPatch"/>.</summary>
public sealed class PatchResult
{
    internal PatchResult(JsonNode? result, string? error, int operation)
    {
        Result = result;
        Error = error;
        Operation = operation;
    }

    /// <summary>The patched document. Meaningful only when <see cref="IsSuccess"/>.</summary>
    public JsonNode? Result { get; }

    /// <summary>The failure message, or <c>null</c> when the patch applied cleanly.</summary>
    public string? Error { get; }

    /// <summary>The index of the operation that failed, or the operation count on success.</summary>
    public int Operation { get; }

    /// <summary>Whether every operation applied.</summary>
    public bool IsSuccess => Error is null;
}

/// <summary>Serializes a <see cref="JsonPatch"/> as the RFC 6902 operation array.</summary>
public sealed class PatchJsonConverter : JsonConverter<JsonPatch>
{
    /// <inheritdoc />
    public override JsonPatch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected an array of JSON Patch operations");

        var converter = (JsonConverter<PatchOperation>)options.GetConverter(typeof(PatchOperation));
        var operations = ImmutableArray.CreateBuilder<PatchOperation>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            operations.Add(converter.Read(ref reader, typeof(PatchOperation), options)!);
        return new JsonPatch(operations.ToImmutable());
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, JsonPatch value, JsonSerializerOptions options)
    {
        var converter = (JsonConverter<PatchOperation>)options.GetConverter(typeof(PatchOperation));
        writer.WriteStartArray();
        foreach (var op in value.Operations)
            converter.Write(writer, op, options);
        writer.WriteEndArray();
    }
}

/// <summary>The RFC 6902 operation semantics, over a mutable <see cref="JsonNode"/> tree.</summary>
internal static class JsonPatchApplier
{
    /// <summary>Applies one operation in place. Returns null on success, or the failure message.</summary>
    internal static string? Apply(ref JsonNode? source, PatchOperation operation) => operation.Op switch
    {
        OperationType.Add => Add(ref source, operation.Path, operation.Value?.DeepClone()),
        OperationType.Replace => Replace(ref source, operation.Path, operation.Value?.DeepClone()),
        OperationType.Remove => Remove(ref source, operation.Path),
        OperationType.Move => Move(ref source, operation.From, operation.Path),
        OperationType.Copy => Copy(ref source, operation.From, operation.Path),
        OperationType.Test => Test(source, operation.Path, operation.Value),
        _ => $"Unsupported operation `{operation.Op}`."
    };

    private static string? Add(ref JsonNode? source, JsonPointer path, JsonNode? value)
    {
        if (path.SegmentCount == 0)
        {
            source = value;
            return null;
        }
        if (!TryGetParent(source, path, out var parent, out var key))
            return $"Target path `{path}` could not be reached.";

        switch (parent)
        {
            case JsonObject obj:
                obj[key] = value;
                return null;
            case JsonArray arr:
                {
                    int index;
                    if (key == "-") index = arr.Count;
                    else if (!int.TryParse(key, out index))
                        return $"Target path `{path}` could not be reached.";
                    if (index < 0 || index > arr.Count)
                        return "Path indicates an index greater than the bounds of the array";
                    if (index == arr.Count) arr.Add(value);
                    else arr.Insert(index, value);
                    return null;
                }
            default:
                return $"Target path `{path}` could not be reached.";
        }
    }

    private static string? Replace(ref JsonNode? source, JsonPointer path, JsonNode? value)
    {
        if (path.SegmentCount == 0)
        {
            source = value;
            return null;
        }
        if (!path.TryEvaluate(source, out _))
            return $"Target path `{path}` could not be reached.";
        if (!TryGetParent(source, path, out var parent, out var key))
            return $"Target path `{path}` could not be reached.";

        switch (parent)
        {
            case JsonObject obj:
                obj[key] = value;
                return null;
            case JsonArray arr:
                {
                    var index = ResolveExistingIndex(arr, key);
                    if (index < 0) return $"Target path `{path}` could not be reached.";
                    arr[index] = value;
                    return null;
                }
            default:
                return $"Target path `{path}` could not be reached.";
        }
    }

    private static string? Remove(ref JsonNode? source, JsonPointer path)
    {
        if (path.SegmentCount == 0)
            return "Cannot remove root value.";
        if (!path.TryEvaluate(source, out _))
            return $"Target path `{path}` could not be reached.";
        if (!TryGetParent(source, path, out var parent, out var key))
            return $"Target path `{path}` could not be reached.";

        switch (parent)
        {
            case JsonObject obj:
                obj.Remove(key);
                return null;
            case JsonArray arr:
                {
                    var index = ResolveExistingIndex(arr, key);
                    if (index < 0) return $"Target path `{path}` could not be reached.";
                    arr.RemoveAt(index);
                    return null;
                }
            default:
                return $"Target path `{path}` could not be reached.";
        }
    }

    private static string? Move(ref JsonNode? source, JsonPointer from, JsonPointer path)
    {
        if (!from.TryEvaluate(source, out var value))
            return $"Source path `{from}` could not be reached.";
        var detached = value?.DeepClone();
        var error = Remove(ref source, from);
        if (error is not null) return error;
        return Add(ref source, path, detached);
    }

    private static string? Copy(ref JsonNode? source, JsonPointer from, JsonPointer path)
    {
        if (!from.TryEvaluate(source, out var value))
            return $"Source path `{from}` could not be reached.";
        return Add(ref source, path, value?.DeepClone());
    }

    private static string? Test(JsonNode? source, JsonPointer path, JsonNode? expected)
    {
        if (!path.TryEvaluate(source, out var actual))
            return $"Target path `{path}` could not be reached.";
        return actual.IsEquivalentTo(expected)
            ? null
            : $"Value at `{path}` does not match the expected value.";
    }

    /// <summary>
    /// Resolves the container that holds the pointer's LAST segment, and decodes that segment
    /// into the real property name / index text.
    /// </summary>
    private static bool TryGetParent(JsonNode? source, JsonPointer path, out JsonNode? parent, out string key)
    {
        key = path.GetSegment(path.SegmentCount - 1).Decode();
        if (path.SegmentCount == 1)
        {
            parent = source;
            return parent is not null;
        }

        var current = source;
        for (var i = 0; i < path.SegmentCount - 1; i++)
        {
            var segment = path.GetSegment(i);
            switch (current)
            {
                case JsonObject obj:
                    if (!obj.TryGetPropertyValue(segment.Decode(), out current))
                    {
                        parent = null;
                        return false;
                    }
                    break;
                case JsonArray arr:
                    {
                        var index = ResolveExistingIndex(arr, segment.Decode());
                        if (index < 0) { parent = null; return false; }
                        current = arr[index];
                        break;
                    }
                default:
                    parent = null;
                    return false;
            }
        }
        parent = current;
        return parent is not null;
    }

    /// <summary>An existing array index: <c>-</c> is the last element; out-of-range yields -1.</summary>
    private static int ResolveExistingIndex(JsonArray array, string key)
    {
        if (key == "-") return array.Count - 1;
        if (!int.TryParse(key, out var index)) return -1;
        return index >= 0 && index < array.Count ? index : -1;
    }
}
