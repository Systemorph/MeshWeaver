using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.AI;

/// <summary>
/// Writes the JSON of a TOOL ANSWER — the string an MCP client or an agent round reads back from
/// <see cref="MeshOperations"/> — with every character a reader would see in the source written as
/// itself.
///
/// <para>🚨 <b>Why this exists (MeshWeaver.Plugins#2804).</b> The hub's serializer options carry
/// System.Text.Json's DEFAULT encoder, which is HTML-safe: it writes <c>+</c>, <c>&lt;</c>,
/// <c>&gt;</c>, <c>&amp;</c>, <c>'</c>, <c>`</c>, <c>"</c> and every non-ASCII character as a
/// <c>\uXXXX</c> escape. That is valid JSON and harmless on the wire, but a language model reads the
/// TEXT. A pull request's unified diff therefore reached the reviewer as
/// <c>\n\u002B        runs \u002B= json.loads(out or \u0022{}\u0022)</c> — the diff marker and the
/// operator spelled identically — and three review rounds on Plugins#2461 independently reported the
/// file's only two <c>+=</c> lines as plain assignments, one of them as a BLOCKING finding that held
/// the PR. The same encoding sits under further rebutted blockers on core (<c>failures +=</c>,
/// <c>names +=</c> read as overwrites; <c>/// &lt;summary&gt;</c> read as a stray <c>&lt;/summary&gt;</c>).</para>
///
/// <para>The relaxed encoder is the right one here because a tool answer is never embedded in
/// HTML: it is a string handed to a model or printed in a terminal. It still escapes what JSON
/// requires (<c>"</c>, <c>\</c>, control characters), so the answer parses exactly as before — only
/// the spelling of the characters changes. The hub's options are used for everything else
/// (converters, naming, <c>$type</c>), so the SHAPE of every answer is unchanged.</para>
/// </summary>
public static class ToolAnswerJson
{
    /// <summary>Serializes <paramref name="value"/> with <paramref name="options"/>'s converters and
    /// naming, written with the relaxed encoder.</summary>
    /// <typeparam name="T">The declared type, exactly as <c>JsonSerializer.Serialize&lt;T&gt;</c> reads it.</typeparam>
    /// <param name="value">The answer to write.</param>
    /// <param name="options">The serializer options that decide the answer's shape (usually the hub's).</param>
    /// <returns>The JSON text, with source characters written as themselves.</returns>
    public static string Serialize<T>(T value, JsonSerializerOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions(options)))
            JsonSerializer.Serialize(writer, value, options);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Writes an already-built JSON DOM with the relaxed encoder — the counterpart of
    /// <see cref="JsonNode.ToJsonString(JsonSerializerOptions?)"/>, whose default is the HTML-safe encoder.</summary>
    /// <param name="node">The DOM to write.</param>
    /// <param name="options">Optional options; only their indentation and depth are read.</param>
    /// <returns>The JSON text, with source characters written as themselves.</returns>
    public static string Write(JsonNode node, JsonSerializerOptions? options = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions(options)))
            node.WriteTo(writer, options);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static JsonWriterOptions WriterOptions(JsonSerializerOptions? options) => new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = options?.WriteIndented ?? false,
        MaxDepth = options?.MaxDepth ?? 0,
    };
}
