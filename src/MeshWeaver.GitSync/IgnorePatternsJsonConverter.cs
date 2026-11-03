using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.GitSync;

/// <summary>
/// Reads <see cref="GitHubSyncConfig.Ignore"/> from the shape it is declared as — a JSON array of
/// patterns — AND from the shape the settings tab once wrote: one JSON string of patterns separated
/// by line breaks or commas (MeshWeaver#7843). Always writes the array.
///
/// <para>🚨 Why the string form must still read. The settings tab's node-content editor offered
/// <c>Ignore</c> as a text field, and a text field stores a string. A string where the record
/// declares <c>string[]</c> fails the WHOLE record's deserialization, so every typed read of the
/// config — the trigger gate, the status line, the periodic reconcile — answered "not configured",
/// while the stored node still carried its repository URL. The editor no longer offers the field
/// (<c>MeshNodeEditorField.FromType</c> skips array and object members), but the configs it already
/// wrote are durable records on running instances: reading them is what restores those Spaces, and
/// the next platform write of the config (every sync records its result through
/// <c>GetMeshNodeStream(path).Update</c>) re-serializes the patterns as an array.</para>
///
/// <para>A blank string reads as unset (the <see cref="SyncIgnore.Default"/> patterns), never as
/// an empty list: clearing a text box is not the deliberate "sync everything" an empty array
/// declares.</para>
/// </summary>
internal sealed class IgnorePatternsJsonConverter : JsonConverter<string[]?>
{
    private static readonly char[] Separators = ['\n', '\r', ','];

    /// <inheritdoc />
    public override string[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                var patterns = (reader.GetString() ?? string.Empty)
                    .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return patterns.Length == 0 ? null : patterns;
            case JsonTokenType.StartArray:
                var list = ImmutableList<string>.Empty;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String)
                        throw new JsonException(
                            $"An ignore pattern must be a string, found {reader.TokenType}.");
                    list = list.Add(reader.GetString()!);
                }
                return list.ToArray();
            default:
                throw new JsonException(
                    $"Ignore patterns must be an array of strings or one string, found {reader.TokenType}.");
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string[]? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        foreach (var pattern in value)
            writer.WriteStringValue(pattern);
        writer.WriteEndArray();
    }
}
