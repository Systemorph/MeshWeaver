using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeshWeaver.Yaml;

/// <summary>
/// Writes a <see cref="JsonNode"/> tree as block-style YAML: two-space indentation, sequences
/// under a mapping key NOT indented (<c>key:</c> then <c>- item</c> at the key's column), strings
/// plain where that reads back as the same string, single-quoted where it would not, double-quoted
/// only when an escape is needed, and multi-line strings as literal block scalars.
///
/// <para>Every string it writes reads back to the same value through <see cref="YamlParser"/>
/// AND through a strict YAML 1.1 reader: scalars a YAML 1.1 reader would resolve to a boolean or a
/// number (<c>yes</c>, <c>off</c>, <c>1_000</c>, <c>1:20</c>) are quoted as well.</para>
/// </summary>
internal static partial class YamlEmitter
{
    private const int IndentStep = 2;

    internal static string Emit(JsonNode? node)
    {
        var sb = new StringBuilder();
        switch (node)
        {
            case JsonObject { Count: > 0 } map:
                EmitMapping(sb, map, 0, firstLineInline: false);
                break;
            case JsonArray { Count: > 0 } sequence:
                EmitSequence(sb, sequence, 0);
                break;
            default:
                // A bare scalar (or an empty collection) is the whole document.
                EmitValue(sb, node, -IndentStep, isMappingValue: false, leadingSpace: false);
                break;
        }
        return sb.ToString();
    }

    private static void EmitMapping(StringBuilder sb, JsonObject map, int indent, bool firstLineInline)
    {
        var first = true;
        foreach (var (key, value) in map)
        {
            if (!(first && firstLineInline))
                sb.Append(' ', indent);
            first = false;
            sb.Append(Key(key)).Append(':');
            EmitValue(sb, value, indent, isMappingValue: true, leadingSpace: true);
        }
    }

    private static void EmitSequence(StringBuilder sb, JsonArray sequence, int indent)
    {
        foreach (var item in sequence)
        {
            sb.Append(' ', indent).Append('-');
            switch (item)
            {
                case JsonObject { Count: > 0 } map:
                    sb.Append(' ');
                    EmitMapping(sb, map, indent + IndentStep, firstLineInline: true);
                    break;
                case JsonArray { Count: > 0 } nested:
                    sb.Append('\n');
                    EmitSequence(sb, nested, indent + IndentStep);
                    break;
                default:
                    EmitValue(sb, item, indent, isMappingValue: false, leadingSpace: true);
                    break;
            }
        }
    }

    /// <summary>Writes the value that follows a <c>key:</c> or a <c>-</c> indicator at <paramref name="indent"/>, ending the line.</summary>
    private static void EmitValue(StringBuilder sb, JsonNode? value, int indent, bool isMappingValue, bool leadingSpace)
    {
        var space = leadingSpace ? " " : string.Empty;
        switch (value)
        {
            case JsonObject { Count: > 0 } map:
                sb.Append('\n');
                EmitMapping(sb, map, indent + IndentStep, firstLineInline: false);
                return;
            case JsonArray { Count: > 0 } sequence:
                sb.Append('\n');
                EmitSequence(sb, sequence, isMappingValue ? indent : indent + IndentStep);
                return;
            case JsonObject:
                sb.Append(space).Append("{}\n");
                return;
            case JsonArray:
                sb.Append(space).Append("[]\n");
                return;
            case null:
                sb.Append(space).Append("null\n");
                return;
        }

        var v = (JsonValue)value;
        switch (v.GetValueKind())
        {
            case JsonValueKind.String:
                var text = v.GetValue<string>();
                if (IsLiteralBlockCandidate(text))
                    EmitLiteralBlock(sb, text, indent + IndentStep, space);
                else
                    sb.Append(space).Append(Scalar(text)).Append('\n');
                return;
            case JsonValueKind.True:
                sb.Append(space).Append("true\n");
                return;
            case JsonValueKind.False:
                sb.Append(space).Append("false\n");
                return;
            case JsonValueKind.Null:
                sb.Append(space).Append("null\n");
                return;
            default:
                sb.Append(space).Append(v.ToJsonString()).Append('\n');
                return;
        }
    }

    // ───────────────────────────── strings ─────────────────────────────

    /// <summary>
    /// A multi-line string reads best as a literal block — but only when every line survives one:
    /// no control characters, no whitespace-only line (a block scalar cannot tell one from an empty
    /// line) and at most one trailing line break (clip or strip chomping; a <c>+</c> block's
    /// trailing blank lines would not survive a caller trimming the document).
    /// </summary>
    private static bool IsLiteralBlockCandidate(string text)
    {
        if (!text.Contains('\n') || text.EndsWith("\n\n", StringComparison.Ordinal))
            return false;
        // A block needs at least one content line: "\n" alone would write an empty block, which
        // reads back as "".
        if (text.Trim('\n').Length == 0)
            return false;
        foreach (var c in text)
            if (c != '\n' && c != '\t' && NeedsEscape(c))
                return false;
        foreach (var l in text.TrimEnd('\n').Split('\n'))
            if (l.Length > 0 && l.Trim(' ', '\t').Length == 0)
                return false;
        return true;
    }

    private static void EmitLiteralBlock(StringBuilder sb, string text, int contentIndent, string space)
    {
        var body = text.EndsWith('\n') ? text[..^1] : text;
        var lines = body.Split('\n');
        var firstContent = lines.FirstOrDefault(l => l.Length > 0);
        sb.Append(space).Append('|');
        if (firstContent is not null && firstContent[0] is ' ' or '\t')
            sb.Append(IndentStep);
        if (!text.EndsWith('\n'))
            sb.Append('-');
        sb.Append('\n');
        foreach (var l in lines)
        {
            if (l.Length > 0)
                sb.Append(' ', Math.Max(contentIndent, IndentStep)).Append(l);
            sb.Append('\n');
        }
    }

    private static string Key(string key) => Scalar(key);

    /// <summary>A single-line scalar: plain when that reads back as the same string, otherwise quoted.</summary>
    private static string Scalar(string text)
    {
        if (IsPlainSafe(text))
            return text;
        if (text.All(c => !NeedsEscape(c) && c != '\n' && c != '\t'))
            return "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        return DoubleQuoted(text);
    }

    private static bool NeedsEscape(char c) =>
        char.IsControl(c) || c is '\uFEFF' or '\u2028' or '\u2029' or '\u0085';

    private static string DoubleQuoted(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    if (NeedsEscape(c))
                        sb.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    [GeneratedRegex(@"^(y|Y|yes|Yes|YES|n|N|no|No|NO|on|On|ON|off|Off|OFF)$")]
    private static partial Regex Yaml11Boolean();

    [GeneratedRegex(@"^[-+]?([0-9][0-9_]*)?(\.[0-9_]*)?([eE][-+]?[0-9]+)?$|^[-+]?[0-9][0-9_]*(:[0-5]?[0-9])+(\.[0-9_]*)?$|^[-+]?0b[01_]+$|^[-+]?0[0-7_]+$|^=$|^<<$")]
    private static partial Regex Yaml11NumberLike();

    private static bool IsPlainSafe(string text)
    {
        if (text.Length == 0)
            return false;
        if (IsBlankChar(text[0]) || IsBlankChar(text[^1]))
            return false;
        var first = text[0];
        if (first is '?' or ':' or ',' or '[' or ']' or '{' or '}' or '#' or '&' or '*' or '!' or '|' or '>'
            or '\'' or '"' or '%' or '@' or '`')
            return false;
        if (first == '-' && (text.Length == 1 || IsBlankChar(text[1]) || text.StartsWith("---", StringComparison.Ordinal)))
            return false;
        if (text.StartsWith("...", StringComparison.Ordinal))
            return false;
        foreach (var c in text)
            if (c is '\n' or '\t' || NeedsEscape(c))
                return false;
        if (text.Contains(": ", StringComparison.Ordinal) || text.Contains(" #", StringComparison.Ordinal) || text.EndsWith(':'))
            return false;
        // It must read back as the same STRING — not null, a boolean or a number — in YAML 1.2
        // and in YAML 1.1 alike.
        if (YamlScalars.Resolve(text) is not JsonValue resolved || resolved.GetValueKind() != JsonValueKind.String
            || resolved.GetValue<string>() != text)
            return false;
        if (Yaml11Boolean().IsMatch(text) || Yaml11NumberLike().IsMatch(text))
            return false;
        return true;
    }

    private static bool IsBlankChar(char c) => c is ' ' or '\t';
}
