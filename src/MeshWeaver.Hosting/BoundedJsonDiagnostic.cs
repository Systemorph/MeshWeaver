using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.Hosting;

/// <summary>Builds a JSON diagnostic prefix without materializing an entire string payload.</summary>
internal static class BoundedJsonDiagnostic
{
    internal static string Format(object? content, int maxChars)
    {
        var text = new StringBuilder(maxChars);
        var truncated = false;
        try
        {
            if (content is JsonElement element)
                Element(element);
            else if (content is JsonNode node)
                Node(node);
        }
        catch (ObjectDisposedException)
        {
            // The original read fault remains on the warning. Its disposed DOM cannot supply a prefix.
            return "<disposed JSON>";
        }
        return text.ToString() + (truncated ? "… (truncated)" : string.Empty);

        void Append(ReadOnlySpan<char> value)
        {
            if (truncated) return;
            var count = Math.Min(value.Length, maxChars - text.Length);
            text.Append(value[..count]);
            truncated |= count < value.Length;
        }

        void Element(JsonElement value)
        {
            var raw = JsonMarshal.GetRawUtf8Value(value);
            var count = Math.Min(raw.Length, maxChars - text.Length);
            // Do not decode a partial UTF-8 character at the cut.
            while (count > 0 && count < raw.Length && (raw[count] & 0xc0) == 0x80)
                count--;
            Append(Encoding.UTF8.GetString(raw[..count]));
            truncated |= count < raw.Length;
        }

        void String(string value)
        {
            var count = Math.Min(value.Length, maxChars - text.Length);
            // Escaping sees at most the remaining budget, never the whole value.
            Append(JsonSerializer.Serialize(value[..count]));
            truncated |= count < value.Length;
        }

        void Node(JsonNode? value)
        {
            if (truncated || text.Length >= maxChars)
            {
                truncated = true;
                return;
            }
            switch (value)
            {
                case null:
                    Append("null");
                    break;
                case JsonObject obj:
                    Append("{");
                    var firstProperty = true;
                    foreach (var property in obj)
                    {
                        if (truncated || text.Length >= maxChars) { truncated = true; break; }
                        if (!firstProperty) Append(",");
                        firstProperty = false;
                        String(property.Key);
                        Append(":");
                        Node(property.Value);
                    }
                    Append("}");
                    break;
                case JsonArray array:
                    Append("[");
                    var firstItem = true;
                    foreach (var item in array)
                    {
                        if (truncated || text.Length >= maxChars) { truncated = true; break; }
                        if (!firstItem) Append(",");
                        firstItem = false;
                        Node(item);
                    }
                    Append("]");
                    break;
                case JsonValue scalar when scalar.TryGetValue<JsonElement>(out var raw):
                    Element(raw);
                    break;
                case JsonValue scalar when scalar.TryGetValue<string>(out var str):
                    String(str);
                    break;
                case JsonValue scalar when scalar.TryGetValue<bool>(out var boolean):
                    Append(boolean ? "true" : "false");
                    break;
                case JsonValue scalar when scalar.TryGetValue<int>(out var integer):
                    Append(integer.ToString(CultureInfo.InvariantCulture));
                    break;
                case JsonValue scalar when scalar.TryGetValue<long>(out var integer):
                    Append(integer.ToString(CultureInfo.InvariantCulture));
                    break;
                case JsonValue scalar when scalar.TryGetValue<decimal>(out var number):
                    Append(number.ToString(CultureInfo.InvariantCulture));
                    break;
                case JsonValue scalar when scalar.TryGetValue<double>(out var number):
                    Append(number.ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    // A customized JsonValue can run an arbitrary converter. Diagnostics do not run it.
                    Append("\"<value omitted>\"");
                    break;
            }
        }
    }
}
