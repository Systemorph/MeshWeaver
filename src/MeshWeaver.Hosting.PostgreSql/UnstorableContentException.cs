using System.Text;
using System.Text.Json;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// A node's serialized content holds a value PostgreSQL's <c>jsonb</c> type cannot represent, so the
/// write can never succeed — no retry, no re-encoding and no wider column changes that.
///
/// <para>Today there is exactly one such value: <b>U+0000</b> (NUL). <c>jsonb</c> stores decoded text
/// and PostgreSQL's text types cannot hold a NUL byte, so the server rejects it outright with
/// <c>22P05: unsupported Unicode escape sequence</c>. JSON itself permits the character and
/// <see cref="JsonSerializer"/> emits it happily — as the six-character Unicode escape for U+0000 —
/// so the value survives every layer above the database and only dies at the very last one.</para>
///
/// <para><b>Why this type exists (#1449).</b> Left to Npgsql the failure surfaces as a bare
/// <c>22P05</c> whose DETAIL is redacted by connection policy ("may contain sensitive data") — it
/// names neither the node, nor the field, nor the character. Worse, the same statement shape is used
/// by the BATCH write path, where one poisoned node fails the whole batch and the log names none of
/// its members. This exception is thrown BEFORE the round-trip, from the one place both paths share,
/// and carries the node path plus the exact property inside the content that holds the NUL.</para>
///
/// <para>It is deliberately NOT transient: writes are never auto-retried, and neither
/// <c>PostgreSqlStorageAdapter.IsTransientConnectionFault</c> nor
/// <c>MeshNodeStreamCache.IsTransientDatabaseFailure</c> matches it. The content has to change.</para>
/// </summary>
public sealed class UnstorableContentException : InvalidOperationException
{
    /// <summary>
    /// The character PostgreSQL's <c>jsonb</c> cannot represent. Written as a cast rather than an
    /// escape so this file never contains a literal NUL — the very mistake that produced #1449.
    /// </summary>
    public const char Nul = (char)0;

    /// <summary>
    /// The tail of the JSON escape for <see cref="Nul"/>, i.e. everything after the backslash.
    /// <see cref="JsonSerializer"/> always escapes control characters, so this — not a raw
    /// <see cref="Nul"/> — is what the serialized payload actually contains.
    /// </summary>
    private const string NulEscapeTail = "u0000";

    private const char Backslash = (char)92;

    /// <summary>Path of the node whose content could not be stored.</summary>
    public string NodePath { get; }

    /// <summary>
    /// Dotted path of the property inside the node's content that holds the offending character
    /// (<c>"$"</c> when the content serializes to a bare string, <c>null</c> when the content could
    /// not be re-parsed to locate it).
    /// </summary>
    public string? ContentProperty { get; }

    /// <summary>How many offending characters the serialized content holds.</summary>
    public int OccurrenceCount { get; }

    private UnstorableContentException(
        string message, string nodePath, string? contentProperty, int occurrenceCount)
        : base(message)
    {
        NodePath = nodePath;
        ContentProperty = contentProperty;
        OccurrenceCount = occurrenceCount;
    }

    /// <summary>
    /// Does <paramref name="contentJson"/> hold a value <c>jsonb</c> cannot store?
    ///
    /// <para>Two-stage on purpose, because this runs on EVERY write: a vectorized
    /// <see cref="string.IndexOf(string, int, StringComparison)"/> for the escape's tail rejects
    /// virtually all real payloads outright, and only a hit pays for the backslash-parity check that
    /// tells a genuine escape from the literal six-character text (which decodes to something jsonb
    /// stores perfectly well, and must NOT be refused).</para>
    /// </summary>
    public static bool IsUnstorable(string? contentJson)
    {
        if (string.IsNullOrEmpty(contentJson))
            return false;

        // A raw NUL should be impossible — JSON requires control characters to be escaped — but a
        // non-conforming encoder would produce one, and it is just as unstorable.
        if (contentJson.Contains(Nul))
            return true;

        return FirstNulEscapeIndex(contentJson, 0) >= 0;
    }

    /// <summary>
    /// Index of the backslash starting the next genuine U+0000 escape at or after
    /// <paramref name="from"/>, or <c>-1</c>.
    /// </summary>
    private static int FirstNulEscapeIndex(string json, int from)
    {
        for (var i = json.IndexOf(NulEscapeTail, from, StringComparison.OrdinalIgnoreCase);
             i >= 0;
             i = json.IndexOf(NulEscapeTail, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (i > 0 && IsEscapeIntroducer(json, i - 1))
                return i - 1;
        }
        return -1;
    }

    /// <summary>
    /// True when the backslash at <paramref name="index"/> INTRODUCES an escape rather than being an
    /// escaped backslash itself — i.e. the run of backslashes ending there has odd length. Without
    /// this, the literal text "backslash-u-0-0-0-0" (serialized as a doubled backslash) would be
    /// misread as a NUL and a perfectly storable node refused.
    /// </summary>
    private static bool IsEscapeIntroducer(string json, int index)
    {
        if (index < 0 || json[index] != Backslash)
            return false;
        var run = 0;
        for (var i = index; i >= 0 && json[i] == Backslash; i--)
            run++;
        return run % 2 == 1;
    }

    /// <summary>
    /// Builds the exception for a content payload containing U+0000, locating the offending property
    /// so the message says exactly what to fix. Failure path only — it may re-parse the payload.
    /// </summary>
    /// <param name="nodePath">Path of the node being written.</param>
    /// <param name="contentJson">The serialized content, already known to contain U+0000.</param>
    public static UnstorableContentException NulInContent(string nodePath, string contentJson)
    {
        var (count, property) = Describe(contentJson);
        var where = property is null
            ? "the offending property could not be located (the content did not re-parse as JSON)"
            : property == "$"
                ? "the content itself is the offending string"
                : $"first occurrence in content property '{property}'";

        return new UnstorableContentException(
            $"Node '{nodePath}' cannot be persisted: its serialized content contains {count} NUL "
            + $"character(s) (U+0000), which PostgreSQL's jsonb type cannot represent — {where}. "
            + "JSON permits the character but jsonb stores DECODED text, and PostgreSQL text cannot "
            + "hold a NUL byte, so the server rejects it with 22P05 (unsupported Unicode escape "
            + "sequence). This is a property of the DATA, not of its size and not of the column: no "
            + "widening makes it storable, so the NUL has to go at the source. A literal NUL typed "
            + "into a source file is the usual cause — write a key separator as an explicit escape "
            + "(U+001F UNIT SEPARATOR) instead.",
            nodePath, property, count);
    }

    /// <summary>
    /// Counts the offending characters and locates the first one's property path. Prefers the parsed
    /// DOM (exact, and it decodes escapes for us); falls back to counting escapes textually when the
    /// payload does not re-parse.
    /// </summary>
    private static (int Count, string? Property) Describe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var count = 0;
            var property = Walk(doc.RootElement, new StringBuilder(), ref count);
            if (count > 0)
                return (count, property);
        }
        catch (JsonException)
        {
            // fall through to the textual count
        }

        var textual = 0;
        for (var i = FirstNulEscapeIndex(json, 0); i >= 0; i = FirstNulEscapeIndex(json, i + 2))
            textual++;
        foreach (var c in json)
            if (c == Nul)
                textual++;
        return (textual, null);
    }

    // Recursion is bounded by JsonDocument's own 64-level depth limit, so this cannot stack-overflow
    // on a hostile payload — a deeper document fails to Parse above and reports "could not locate".
    private static string? Walk(JsonElement element, StringBuilder path, ref int count)
    {
        string? first = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var length = path.Length;
                    if (length > 0)
                        path.Append('.');
                    path.Append(property.Name);
                    // A NUL can sit in the property NAME too — jsonb rejects that identically.
                    var nameHits = CountNul(property.Name);
                    if (nameHits > 0)
                    {
                        count += nameHits;
                        first ??= path.ToString();
                    }
                    // Always recurse (the count must be complete); keep only the FIRST location.
                    var hit = Walk(property.Value, path, ref count);
                    first ??= hit;
                    path.Length = length;
                }
                return first;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var length = path.Length;
                    path.Append('[').Append(index).Append(']');
                    var hit = Walk(item, path, ref count);
                    first ??= hit;
                    path.Length = length;
                    index++;
                }
                return first;

            case JsonValueKind.String:
                var hits = CountNul(element.GetString());
                if (hits == 0)
                    return null;
                count += hits;
                return path.Length == 0 ? "$" : path.ToString();

            default:
                return null;
        }
    }

    private static int CountNul(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;
        var count = 0;
        foreach (var c in value)
            if (c == Nul)
                count++;
        return count;
    }
}
