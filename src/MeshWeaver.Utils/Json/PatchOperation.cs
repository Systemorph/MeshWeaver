using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeshWeaver.Json;

/// <summary>The RFC 6902 operation verbs.</summary>
[JsonConverter(typeof(OperationTypeJsonConverter))]
public enum OperationType
{
    /// <summary>An operation verb that is not one of the six RFC 6902 verbs.</summary>
    Unknown,
    /// <summary><c>add</c>.</summary>
    Add,
    /// <summary><c>remove</c>.</summary>
    Remove,
    /// <summary><c>replace</c>.</summary>
    Replace,
    /// <summary><c>move</c>.</summary>
    Move,
    /// <summary><c>copy</c>.</summary>
    Copy,
    /// <summary><c>test</c>.</summary>
    Test
}

/// <summary>Reads/writes <see cref="OperationType"/> as its lowercase RFC 6902 verb.</summary>
public sealed class OperationTypeJsonConverter : JsonConverter<OperationType>
{
    /// <inheritdoc />
    public override OperationType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Parse(reader.GetString());

    /// <summary>Maps an RFC 6902 verb to its <see cref="OperationType"/>.</summary>
    public static OperationType Parse(string? verb) => verb switch
    {
        "add" => OperationType.Add,
        "remove" => OperationType.Remove,
        "replace" => OperationType.Replace,
        "move" => OperationType.Move,
        "copy" => OperationType.Copy,
        "test" => OperationType.Test,
        _ => OperationType.Unknown
    };

    /// <summary>Maps an <see cref="OperationType"/> to its RFC 6902 verb.</summary>
    public static string ToVerb(OperationType op) => op switch
    {
        OperationType.Add => "add",
        OperationType.Remove => "remove",
        OperationType.Replace => "replace",
        OperationType.Move => "move",
        OperationType.Copy => "copy",
        OperationType.Test => "test",
        _ => "unknown"
    };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, OperationType value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToVerb(value));
}

/// <summary>A single RFC 6902 JSON Patch operation.</summary>
[JsonConverter(typeof(PatchOperationJsonConverter))]
public sealed class PatchOperation : IEquatable<PatchOperation>
{
    /// <summary>The operation verb.</summary>
    public OperationType Op { get; }

    /// <summary>The source location for <c>move</c>/<c>copy</c>; <see cref="JsonPointer.Empty"/> otherwise.</summary>
    public JsonPointer From { get; }

    /// <summary>The target location.</summary>
    public JsonPointer Path { get; }

    /// <summary>The operand for <c>add</c>/<c>replace</c>/<c>test</c>; <c>null</c> otherwise.</summary>
    public JsonNode? Value { get; }

    private PatchOperation(OperationType op, JsonPointer from, JsonPointer path, JsonNode? value)
    {
        Op = op;
        From = from;
        Path = path;
        // Detach from any document the caller handed us: a JsonNode may only have one parent,
        // and the diff hands us live nodes out of the target document.
        Value = value?.DeepClone();
    }

    /// <summary>Creates an <c>add</c> operation.</summary>
    public static PatchOperation Add(JsonPointer path, JsonNode? value)
        => new(OperationType.Add, JsonPointer.Empty, path, value);

    /// <summary>Creates a <c>remove</c> operation.</summary>
    public static PatchOperation Remove(JsonPointer path)
        => new(OperationType.Remove, JsonPointer.Empty, path, null);

    /// <summary>Creates a <c>replace</c> operation.</summary>
    public static PatchOperation Replace(JsonPointer path, JsonNode? value)
        => new(OperationType.Replace, JsonPointer.Empty, path, value);

    /// <summary>Creates a <c>move</c> operation. 🚨 Note the argument order: <c>from</c> first.</summary>
    /// <param name="from">The location to move the value FROM.</param>
    /// <param name="path">The location to move the value TO.</param>
    public static PatchOperation Move(JsonPointer from, JsonPointer path)
        => new(OperationType.Move, from, path, null);

    /// <summary>Creates a <c>copy</c> operation. 🚨 Note the argument order: <c>from</c> first.</summary>
    /// <param name="from">The location to copy the value FROM.</param>
    /// <param name="path">The location to copy the value TO.</param>
    public static PatchOperation Copy(JsonPointer from, JsonPointer path)
        => new(OperationType.Copy, from, path, null);

    /// <summary>Creates a <c>test</c> operation.</summary>
    public static PatchOperation Test(JsonPointer path, JsonNode? value)
        => new(OperationType.Test, JsonPointer.Empty, path, value);

    /// <inheritdoc />
    public bool Equals(PatchOperation? other) =>
        other is not null
        && Op == other.Op
        && From.Equals(other.From)
        && Path.Equals(other.Path)
        && Value.IsEquivalentTo(other.Value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PatchOperation);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Op, From, Path);

    /// <inheritdoc />
    public override string ToString() =>
        Op is OperationType.Move or OperationType.Copy
            ? $"{OperationTypeJsonConverter.ToVerb(Op)} {From} -> {Path}"
            : $"{OperationTypeJsonConverter.ToVerb(Op)} {Path}";
}

/// <summary>
/// Reads/writes a <see cref="PatchOperation"/> in the RFC 6902 wire shape.
/// </summary>
/// <remarks>
/// 🚨 The property ORDER is part of the contract — <c>op</c>, <c>path</c>, then <c>value</c>
/// (add/replace/test) or <c>from</c> (move/copy), and nothing further for <c>remove</c>. The
/// React, gRPC-web and Python clients are pinned to these exact bytes and are not built by this
/// repo's CI, so a reordering here is a silent break. Covered by JsonPatchWireFormatTest.
/// </remarks>
public sealed class PatchOperationJsonConverter : JsonConverter<PatchOperation>
{
    /// <inheritdoc />
    public override PatchOperation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for a JSON Patch operation");

        var op = OperationType.Unknown;
        JsonPointer? path = null;
        JsonPointer? from = null;
        JsonNode? value = null;
        var hasValue = false;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected a property name");
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "op":
                    op = OperationTypeJsonConverter.Parse(reader.GetString());
                    break;
                case "path":
                    path = ParsePointer(ref reader, "path");
                    break;
                case "from":
                    from = ParsePointer(ref reader, "from");
                    break;
                case "value":
                    value = JsonElement.ParseValue(ref reader).AsNode();
                    hasValue = true;
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (path is null)
            throw new JsonException($"`{op}` operation requires `path`");

        switch (op)
        {
            case OperationType.Add:
                if (!hasValue) throw new JsonException("`add` operation requires `value`");
                return PatchOperation.Add(path.Value, value);
            case OperationType.Remove:
                return PatchOperation.Remove(path.Value);
            case OperationType.Replace:
                if (!hasValue) throw new JsonException("`replace` operation requires `value`");
                return PatchOperation.Replace(path.Value, value);
            case OperationType.Move:
                if (from is null) throw new JsonException("`move` operation requires `from`");
                return PatchOperation.Move(from.Value, path.Value);
            case OperationType.Copy:
                if (from is null) throw new JsonException("`copy` operation requires `from`");
                return PatchOperation.Copy(from.Value, path.Value);
            case OperationType.Test:
                if (!hasValue) throw new JsonException("`test` operation requires `value`");
                return PatchOperation.Test(path.Value, value);
            default:
                throw new JsonException($"Unknown JSON Patch operation `{op}`");
        }
    }

    private static JsonPointer ParsePointer(ref Utf8JsonReader reader, string property)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"`{property}` must be a string");
        // 🚨 PointerParseException, NOT JsonException — a malformed `path` has always surfaced as
        // a pointer-parse failure, so a `catch (JsonException)` around patch deserialization must
        // keep NOT swallowing it.
        return JsonPointer.Parse(reader.GetString()!);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PatchOperation value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("op"u8, OperationTypeJsonConverter.ToVerb(value.Op));
        writer.WriteString("path"u8, value.Path.Text);
        switch (value.Op)
        {
            case OperationType.Add:
            case OperationType.Replace:
            case OperationType.Test:
                writer.WritePropertyName("value"u8);
                if (value.Value is null) writer.WriteNullValue();
                else value.Value.WriteTo(writer, options);
                break;
            case OperationType.Move:
            case OperationType.Copy:
                writer.WriteString("from"u8, value.From.Text);
                break;
            case OperationType.Remove:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.Op, "Unknown JSON Patch operation");
        }
        writer.WriteEndObject();
    }
}
