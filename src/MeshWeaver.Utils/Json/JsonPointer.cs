using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeshWeaver.Json;

/// <summary>
/// An RFC 6901 JSON Pointer.
/// </summary>
/// <remarks>
/// <para>
/// The pointer is stored as its <em>escaped</em> RFC 6901 text (<c>~0</c> for <c>~</c>,
/// <c>~1</c> for <c>/</c>) in a single <see cref="string"/>. That is deliberate: it makes
/// <see cref="ToString"/> and equality allocation-free, and it is the form that goes on the
/// wire, so no round-trip re-encoding can perturb the bytes.
/// </para>
/// <para>
/// 🚨 <see cref="GetSegment"/> therefore returns the segment in its ESCAPED form — callers that
/// need the real property name must decode it (<see cref="JsonPointerSegment.Decode()"/>). The
/// resolution methods (<see cref="Evaluate(JsonElement)"/>, <see cref="TryEvaluate"/>) decode
/// internally, so they match property names correctly.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonPointerJsonConverter))]
public readonly struct JsonPointer : IEquatable<JsonPointer>
{
    /// <summary>The root pointer — the empty string, zero segments.</summary>
    public static readonly JsonPointer Empty = default;

    private readonly string? pointer;

    /// <summary>The number of segments in this pointer.</summary>
    public int SegmentCount { get; }

    private JsonPointer(string pointer, int segmentCount)
    {
        this.pointer = pointer;
        SegmentCount = segmentCount;
    }

    /// <summary>The escaped RFC 6901 text of this pointer (<c>""</c> for the root).</summary>
    public string Text => pointer ?? string.Empty;

    /// <summary>The escaped RFC 6901 text of this pointer.</summary>
    public override string ToString() => Text;

    // ---- construction -------------------------------------------------------------

    /// <summary>Parses an RFC 6901 pointer string.</summary>
    /// <param name="pointer">The pointer text. A leading <c>#</c> marks a URI-fragment form and is URL-decoded.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pointer"/> is null.</exception>
    /// <exception cref="PointerParseException"><paramref name="pointer"/> is not a valid JSON Pointer.</exception>
    public static JsonPointer Parse(string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        // The common case — a plain "/a/b" pointer — is handled inline: validate once and wrap the
        // caller's string with no copy, no out-parameter round trip and no fragment handling.
        if (pointer.Length == 0) return Empty;
        if (pointer[0] == '/')
        {
            if (!Validate(pointer.AsSpan(), out var segmentCount))
                throw new PointerParseException("Invalid JSON Pointer format");
            return new JsonPointer(pointer, segmentCount);
        }
        if (!TryParse(pointer, out var result))
            throw new PointerParseException("Invalid JSON Pointer format");
        return result;
    }

    /// <summary>Parses an RFC 6901 pointer span.</summary>
    /// <exception cref="PointerParseException">The span is not a valid JSON Pointer.</exception>
    public static JsonPointer Parse(ReadOnlySpan<char> pointer)
    {
        if (!TryParse(pointer, out var result))
            throw new PointerParseException("Invalid JSON Pointer format");
        return result;
    }

    /// <summary>Attempts to parse an RFC 6901 pointer string.</summary>
    public static bool TryParse(string? pointer, out JsonPointer result)
    {
        if (string.IsNullOrEmpty(pointer))
        {
            result = Empty;
            return true;
        }

        if (pointer[0] == '#')
        {
            if (pointer.Length == 1)
            {
                result = Empty;
                return true;
            }
            try
            {
                pointer = WebUtility.UrlDecode(pointer[1..]);
            }
            catch
            {
                result = default;
                return false;
            }
        }

        if (!Validate(pointer.AsSpan(), out var segmentCount))
        {
            result = default;
            return false;
        }
        result = new JsonPointer(pointer, segmentCount);
        return true;
    }

    /// <summary>Attempts to parse an RFC 6901 pointer span.</summary>
    public static bool TryParse(ReadOnlySpan<char> pointer, out JsonPointer result)
    {
        if (pointer.Length == 0)
        {
            result = Empty;
            return true;
        }
        if (pointer[0] == '#')
        {
            if (pointer.Length == 1)
            {
                result = Empty;
                return true;
            }
            return TryParse(WebUtility.UrlDecode(pointer[1..].ToString()), out result);
        }
        if (!Validate(pointer, out var segmentCount))
        {
            result = default;
            return false;
        }
        result = new JsonPointer(pointer.ToString(), segmentCount);
        return true;
    }

    private static bool Validate(ReadOnlySpan<char> pointer, out int segmentCount)
    {
        segmentCount = 0;
        if (pointer[0] != '/')
            return false;

        // Count '/' and validate every '~' escape. Vectorised Count handles the overwhelmingly
        // common escape-free pointer without a per-character loop; only a pointer that actually
        // contains '~' pays for the scan.
        var count = pointer.Count('/');
        var tilde = pointer.IndexOf('~');
        while (tilde >= 0)
        {
            if (tilde + 1 >= pointer.Length) return false;
            var next = pointer[tilde + 1];
            if (next != '0' && next != '1') return false;
            var rest = pointer[(tilde + 2)..];
            var relative = rest.IndexOf('~');
            tilde = relative < 0 ? -1 : tilde + 2 + relative;
        }
        segmentCount = count;
        return true;
    }

    /// <summary>Builds a pointer from UNESCAPED segments; each segment is RFC 6901 escaped.</summary>
    public static JsonPointer Create() => Empty;

    /// <inheritdoc cref="Create()"/>
    public static JsonPointer Create(string segment) =>
        new(BuildOne(null, segment), 1);

    /// <inheritdoc cref="Create()"/>
    public static JsonPointer Create(string first, string second) =>
        new(BuildTwo(first, second), 2);

    /// <inheritdoc cref="Create()"/>
    public static JsonPointer Create(params ReadOnlySpan<string> segments)
    {
        if (segments.Length == 0) return Empty;
        if (segments.Length == 1) return Create(segments[0]);
        if (segments.Length == 2) return Create(segments[0], segments[1]);
        var sb = new System.Text.StringBuilder();
        foreach (var s in segments)
        {
            sb.Append('/');
            AppendEscaped(sb, s);
        }
        return new JsonPointer(sb.ToString(), segments.Length);
    }

    /// <summary>Builds a pointer addressing the given array index.</summary>
    public static JsonPointer Create(int index) => new("/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), 1);

    /// <summary>Appends <paramref name="other"/>'s segments to this pointer.</summary>
    public JsonPointer Combine(JsonPointer other)
    {
        if (other.SegmentCount == 0 && other.Text.Length == 0) return this;
        if (SegmentCount == 0 && Text.Length == 0) return other;
        return new JsonPointer(string.Concat(Text, other.Text), SegmentCount + other.SegmentCount);
    }

    /// <summary>Appends one UNESCAPED segment, escaping it per RFC 6901.</summary>
    public JsonPointer Combine(string segment) =>
        new(BuildOne(pointer, segment), SegmentCount + 1);

    /// <summary>Appends an array index as a segment.</summary>
    public JsonPointer Combine(int index)
    {
        var text = Text;
        return new JsonPointer(
            string.Concat(text, "/", index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            SegmentCount + 1);
    }

    // ---- segments -----------------------------------------------------------------

    /// <summary>Gets a segment by index, in its ESCAPED form.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    public JsonPointerSegment GetSegment(int index)
    {
        if (index < 0 || index >= SegmentCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return new JsonPointerSegment(SegmentSpan(Text.AsSpan(), index));
    }

    /// <summary>Gets a segment by index, in its ESCAPED form.</summary>
    public JsonPointerSegment this[int index] => GetSegment(index);

    /// <summary>Attempts to get a segment by index.</summary>
    public bool TryGetSegment(int index, out JsonPointerSegment segment)
    {
        if (index < 0 || index >= SegmentCount)
        {
            segment = default;
            return false;
        }
        segment = GetSegment(index);
        return true;
    }

    /// <summary>
    /// Slices the <paramref name="index"/>-th segment out of an escaped pointer span.
    /// Single forward pass — the pointer is a run of <c>/</c>-prefixed segments.
    /// </summary>
    private static ReadOnlySpan<char> SegmentSpan(ReadOnlySpan<char> text, int index)
    {
        var start = 1;
        for (var seg = 0; ; seg++)
        {
            var rest = text[start..];
            var slash = rest.IndexOf('/');
            var end = slash < 0 ? text.Length : start + slash;
            if (seg == index) return text[start..end];
            start = end + 1;
        }
    }

    // ---- resolution ---------------------------------------------------------------

    /// <summary>Resolves this pointer against <paramref name="element"/>.</summary>
    /// <returns>The referenced element, or <c>null</c> when the pointer does not resolve.</returns>
    public JsonElement? Evaluate(JsonElement element)
    {
        var text = Text;
        if (text.Length == 0) return element;

        var span = text.AsSpan();
        var current = element;
        var start = 1;
        while (start <= span.Length)
        {
            var rest = span[start..];
            var slash = rest.IndexOf('/');
            var end = slash < 0 ? span.Length : start + slash;
            var segment = span[start..end];

            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!TryGetProperty(current, segment, out current))
                    return null;
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                if (!TryGetArrayIndex(segment, current.GetArrayLength(), out var index))
                    return null;
                current = current[index];
            }
            else
            {
                return null;
            }
            start = end + 1;
        }
        return current;
    }

    /// <summary>Resolves this pointer against a <see cref="JsonNode"/> tree.</summary>
    /// <returns><c>true</c> when the pointer resolves; <c>false</c> otherwise.</returns>
    public bool TryEvaluate(JsonNode? root, out JsonNode? result)
    {
        var text = Text;
        if (text.Length == 0)
        {
            result = root;
            return true;
        }

        var span = text.AsSpan();
        var current = root;
        var start = 1;
        while (start <= span.Length)
        {
            var rest = span[start..];
            var slash = rest.IndexOf('/');
            var end = slash < 0 ? span.Length : start + slash;
            var segment = span[start..end];

            switch (current)
            {
                case JsonObject obj:
                    {
                        if (!TryGetProperty(obj, segment, out current))
                        {
                            result = null;
                            return false;
                        }
                        break;
                    }
                case JsonArray arr:
                    {
                        if (!TryGetArrayIndex(segment, arr.Count, out var index))
                        {
                            result = null;
                            return false;
                        }
                        current = arr[index];
                        break;
                    }
                default:
                    result = null;
                    return false;
            }
            start = end + 1;
        }
        result = current;
        return true;
    }

    /// <summary>
    /// Looks a property up by an ESCAPED pointer segment. The fast path (no <c>~</c>) compares
    /// the segment span directly; only an escaped segment pays for a decode allocation.
    /// </summary>
    private static bool TryGetProperty(JsonElement obj, ReadOnlySpan<char> escapedSegment, out JsonElement value)
    {
        if (escapedSegment.IndexOf('~') < 0)
            return obj.TryGetProperty(escapedSegment, out value);
        return obj.TryGetProperty(JsonPointerSegment.Decode(escapedSegment), out value);
    }

    private static bool TryGetProperty(JsonObject obj, ReadOnlySpan<char> escapedSegment, out JsonNode? value)
    {
        var name = escapedSegment.IndexOf('~') < 0
            ? escapedSegment.ToString()
            : JsonPointerSegment.Decode(escapedSegment);
        return obj.TryGetPropertyValue(name, out value);
    }

    /// <summary>
    /// RFC 6901 array-index rules as this codebase has always applied them: no leading zeros,
    /// no sign, and <c>-</c> addresses the LAST element (the json-everything behaviour the
    /// clients were built against, not the strict "one past the end" reading).
    /// </summary>
    private static bool TryGetArrayIndex(ReadOnlySpan<char> segment, int length, out int index)
    {
        index = -1;
        if (segment.Length == 0) return false;
        if (segment.Length == 1 && segment[0] == '0')
        {
            if (length == 0) return false;
            index = 0;
            return true;
        }
        if (segment[0] == '0') return false;
        if (segment.Length == 1 && segment[0] == '-')
        {
            if (length == 0) return false;
            index = length - 1;
            return true;
        }
        if (!int.TryParse(segment, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return false;
        if (parsed < 0 || parsed >= length) return false;
        index = parsed;
        return true;
    }

    // ---- escaping -----------------------------------------------------------------

    /// <summary>RFC 6901 escaping: <c>~</c> → <c>~0</c>, <c>/</c> → <c>~1</c>.</summary>
    public static string Escape(string segment)
    {
        var needed = 0;
        foreach (var c in segment)
            if (c is '~' or '/') needed++;
        if (needed == 0) return segment;

        return string.Create(segment.Length + needed, segment, static (span, source) =>
        {
            var j = 0;
            foreach (var c in source)
            {
                switch (c)
                {
                    case '~': span[j++] = '~'; span[j++] = '0'; break;
                    case '/': span[j++] = '~'; span[j++] = '1'; break;
                    default: span[j++] = c; break;
                }
            }
        });
    }

    private static void AppendEscaped(System.Text.StringBuilder sb, string segment)
    {
        foreach (var c in segment)
        {
            switch (c)
            {
                case '~': sb.Append("~0"); break;
                case '/': sb.Append("~1"); break;
                default: sb.Append(c); break;
            }
        }
    }

    /// <summary>Builds <c>prefix + "/" + escape(segment)</c> in a single allocation.</summary>
    private static string BuildOne(string? prefix, string segment)
    {
        var extra = 0;
        foreach (var c in segment)
            if (c is '~' or '/') extra++;

        var prefixLength = prefix?.Length ?? 0;
        var total = prefixLength + 1 + segment.Length + extra;
        return string.Create(total, (prefix, segment, prefixLength), static (span, state) =>
        {
            var (p, s, pl) = state;
            if (pl > 0) p.AsSpan().CopyTo(span);
            var j = pl;
            span[j++] = '/';
            WriteEscaped(span, ref j, s);
        });
    }

    private static string BuildTwo(string first, string second)
    {
        var extra = 0;
        foreach (var c in first)
            if (c is '~' or '/') extra++;
        foreach (var c in second)
            if (c is '~' or '/') extra++;

        var total = 2 + first.Length + second.Length + extra;
        return string.Create(total, (first, second), static (span, state) =>
        {
            var (a, b) = state;
            var j = 0;
            span[j++] = '/';
            WriteEscaped(span, ref j, a);
            span[j++] = '/';
            WriteEscaped(span, ref j, b);
        });
    }

    private static void WriteEscaped(Span<char> span, ref int j, string source)
    {
        foreach (var c in source)
        {
            switch (c)
            {
                case '~': span[j++] = '~'; span[j++] = '0'; break;
                case '/': span[j++] = '~'; span[j++] = '1'; break;
                default: span[j++] = c; break;
            }
        }
    }

    // ---- equality -----------------------------------------------------------------

    /// <inheritdoc />
    public bool Equals(JsonPointer other) =>
        SegmentCount == other.SegmentCount && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JsonPointer other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    /// <summary>Value equality.</summary>
    public static bool operator ==(JsonPointer left, JsonPointer right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(JsonPointer left, JsonPointer right) => !left.Equals(right);
}

/// <summary>
/// One segment of a <see cref="JsonPointer"/>, in its ESCAPED form, as a zero-allocation view.
/// </summary>
public readonly ref struct JsonPointerSegment
{
    private readonly ReadOnlySpan<char> segment;

    internal JsonPointerSegment(ReadOnlySpan<char> segment) => this.segment = segment;

    /// <summary>The raw (escaped) segment text.</summary>
    public ReadOnlySpan<char> AsSpan() => segment;

    /// <summary>The raw (escaped) segment text — matches the pointer's own encoding.</summary>
    public override string ToString() => segment.ToString();

    /// <summary>The UNESCAPED segment, i.e. the real JSON property name.</summary>
    public string Decode() => Decode(segment);

    /// <summary>The segment parsed as an array index, or <c>null</c> when it is not an integer.</summary>
    public int? ToInt() => int.TryParse(segment, out var result) ? result : null;

    /// <summary>
    /// RFC 6901 unescaping: <c>~1</c> → <c>/</c> and <c>~0</c> → <c>~</c>, in ONE left-to-right
    /// pass. 🚨 A two-pass <c>Replace("~0","~").Replace("~1","/")</c> is WRONG in either order —
    /// <c>~01</c> must decode to <c>~1</c>, but replacing <c>~0</c> first yields <c>~1</c> which
    /// the second pass then turns into <c>/</c>.
    /// </summary>
    public static string Decode(ReadOnlySpan<char> escaped)
    {
        var tilde = escaped.IndexOf('~');
        if (tilde < 0) return escaped.ToString();

        var buffer = escaped.Length <= 256 ? stackalloc char[escaped.Length] : new char[escaped.Length];
        var j = 0;
        for (var i = 0; i < escaped.Length; i++)
        {
            if (escaped[i] == '~' && i + 1 < escaped.Length)
            {
                if (escaped[i + 1] == '0') { buffer[j++] = '~'; i++; continue; }
                if (escaped[i + 1] == '1') { buffer[j++] = '/'; i++; continue; }
            }
            buffer[j++] = escaped[i];
        }
        return new string(buffer[..j]);
    }

    /// <summary>
    /// Compares this segment against a property name, decoding the SEGMENT's <c>~0</c>/<c>~1</c>
    /// escapes as it walks and reading <paramref name="value"/> as-is.
    /// </summary>
    /// <remarks>
    /// 🚨 So an ALREADY-ESCAPED probe does not match: segment <c>a~1b</c> vs value <c>"a~1b"</c> is
    /// <c>false</c> (the segment's escape counts as one character, the probe's as two). Pass the
    /// real property name. This asymmetry is inherited from json-everything and is preserved
    /// deliberately; <c>JsonPointerTest.Segment_ComparesAcrossEscaping</c> pins it.
    /// <para>
    /// A null <paramref name="value"/> returns false rather than throwing — a JSON property name is
    /// never null, so "no match" is the useful answer.
    /// </para>
    /// </remarks>
    public bool Equals(string? value) => value is not null && SegmentEquals(value.AsSpan());

    /// <summary>Compares against an array index.</summary>
    public bool Equals(int value) => SegmentEquals(value.ToString(System.Globalization.CultureInfo.InvariantCulture).AsSpan());

    /// <summary>Compares two segments, tolerating a different escaping of the same name.</summary>
    public bool Equals(JsonPointerSegment other) => SegmentEquals(other.segment);

    private bool SegmentEquals(ReadOnlySpan<char> value)
    {
        if (segment.IndexOf('~') < 0 && value.IndexOf('~') < 0)
            return segment.SequenceEqual(value);

        int i = 0, j = 0;
        while (i < segment.Length && j < value.Length)
        {
            if (segment[i] == '~' && i + 1 < segment.Length && segment[i + 1] is '0' or '1')
            {
                var expected = segment[i + 1] == '0' ? '~' : '/';
                if (value[j] != expected) return false;
                i += 2;
                j++;
            }
            else if (value[j] == '~' && j + 1 < value.Length && value[j + 1] is '0' or '1')
            {
                var expected = value[j + 1] == '0' ? '~' : '/';
                if (segment[i] != expected) return false;
                i++;
                j += 2;
            }
            else
            {
                if (segment[i] != value[j]) return false;
                i++;
                j++;
            }
        }
        return i == segment.Length && j == value.Length;
    }

    /// <summary>
    /// Compares against a boxed property name. A segment is a span view, so anything other than
    /// a <see cref="string"/> cannot be equal to it.
    /// </summary>
    public override bool Equals(object? obj) => obj is string value && SegmentEquals(value.AsSpan());

    /// <summary>Ordinal hash of the escaped segment text.</summary>
    public override int GetHashCode() => string.GetHashCode(segment, StringComparison.Ordinal);

    /// <summary>Segment equality.</summary>
    public static bool operator ==(JsonPointerSegment left, JsonPointerSegment right) => left.Equals(right);

    /// <summary>Segment inequality.</summary>
    public static bool operator !=(JsonPointerSegment left, JsonPointerSegment right) => !left.Equals(right);

    /// <summary>Compares a segment with a property name. A null name never matches.</summary>
    public static bool operator ==(JsonPointerSegment left, string? right) => left.Equals(right);

    /// <summary>Compares a segment with a property name. A null name never matches.</summary>
    public static bool operator !=(JsonPointerSegment left, string? right) => !left.Equals(right);

    /// <summary>Compares a segment with a property name span.</summary>
    public static bool operator ==(JsonPointerSegment left, ReadOnlySpan<char> right) => left.SegmentEquals(right);

    /// <summary>Compares a segment with a property name span.</summary>
    public static bool operator !=(JsonPointerSegment left, ReadOnlySpan<char> right) => !left.SegmentEquals(right);
}

/// <summary>Thrown when a string is not a well-formed RFC 6901 JSON Pointer.</summary>
public class PointerParseException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PointerParseException() { }

    /// <summary>Creates the exception with a message.</summary>
    public PointerParseException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PointerParseException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Serializes a <see cref="JsonPointer"/> as its RFC 6901 string.</summary>
public sealed class JsonPointerJsonConverter : JsonConverter<JsonPointer>
{
    /// <inheritdoc />
    public override JsonPointer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected string");
        return JsonPointer.Parse(reader.GetString()!);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, JsonPointer value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Text);

    /// <inheritdoc />
    public override JsonPointer ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => JsonPointer.Parse(reader.GetString()!);

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] JsonPointer value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Text);
}
