namespace MeshWeaver.Mesh.Completion;

/// <summary>
/// What the token under the caret of a mesh query IS, grammatically — the one question a query
/// completer has to answer before it can suggest anything. See <see cref="QueryCaret.At"/>.
/// </summary>
public enum QueryCaretKind
{
    /// <summary>Nothing completable here: inside a free-text quote, inside a <c>(A OR B)</c> list, or past the end.</summary>
    None,

    /// <summary>A bare word: either the start of a qualifier (<c>nodeTy</c> → <c>nodeType:</c>) or free text.</summary>
    Keyword,

    /// <summary>The value of a qualifier or node field: <c>scope:desc</c>, <c>sort:last</c>, <c>nodeType:Crm/</c>.</summary>
    Value,

    /// <summary>A content property NAME: <c>content.sta</c> (no colon yet).</summary>
    ContentProperty,

    /// <summary>The value of a content property: <c>content.status:Op</c>.</summary>
    ContentValue,

    /// <summary>An <c>@</c> path reference — completed by the path autocomplete, not by the grammar.</summary>
    Reference,
}

/// <summary>
/// The token under the caret, classified.
/// </summary>
/// <param name="Kind">What the token is.</param>
/// <param name="Field">The qualifier or field a value belongs to (<c>sort</c>, <c>nodeType</c>), or the content
/// property (<c>status</c>, without the <c>content.</c> prefix) for <see cref="QueryCaretKind.ContentValue"/>.
/// Null for the other kinds.</param>
/// <param name="Prefix">What has been typed of the part being completed — the text from
/// <paramref name="ReplaceStart"/> to the caret.</param>
/// <param name="ReplaceStart">Offset in the query text where an accepted completion's insert text starts.</param>
/// <param name="ReplaceEnd">Offset where it ends — the caret.</param>
/// <param name="Negated">The term carries a leading <c>-</c> (an exclusion).</param>
/// <param name="Quoted">The value being typed is inside an open quote (<c>name:"Acme Co</c>).</param>
public sealed record QueryCaretToken(
    QueryCaretKind Kind,
    string? Field,
    string Prefix,
    int ReplaceStart,
    int ReplaceEnd,
    bool Negated = false,
    bool Quoted = false)
{
    /// <summary>
    /// The ordered-comparison operator typed in front of the value (<c>&gt;</c>, <c>&lt;</c>, <c>&gt;=</c>,
    /// <c>&lt;=</c>), or empty. It is NOT part of <see cref="Prefix"/> or of the replaced span: an accepted
    /// value is inserted after it.
    /// </summary>
    public string Operator { get; init; } = "";

    /// <summary>The token for a position with nothing to complete.</summary>
    /// <param name="caret">The caret offset.</param>
    /// <returns>A <see cref="QueryCaretKind.None"/> token anchored at the caret.</returns>
    public static QueryCaretToken Nothing(int caret) => new(QueryCaretKind.None, null, "", caret, caret);
}

/// <summary>
/// Classifies the token under the caret of a mesh query string, using the same lexical rules as
/// <see cref="QueryParser"/>: whitespace separates terms, <c>field:value</c> is a comparison, a leading
/// <c>-</c> negates, a quoted value is literal, <c>a|b</c> is alternation, <c>&gt;=</c>/<c>&lt;=</c>/<c>&gt;</c>/<c>&lt;</c>
/// prefix an ordered value, and <c>content.x</c> names a content property. Pure — no mesh, no I/O.
/// </summary>
public static class QueryCaret
{
    /// <summary>The prefix that addresses a node's content properties in a query (<c>content.status:Open</c>).</summary>
    public const string ContentPrefix = "content.";

    /// <summary>
    /// The token under <paramref name="caret"/> in <paramref name="text"/>.
    /// </summary>
    /// <param name="text">The visible query text.</param>
    /// <param name="caret">The caret offset (0-based; clamped into the text).</param>
    /// <returns>The classified token; <see cref="QueryCaretKind.None"/> when nothing can be completed here.</returns>
    public static QueryCaretToken At(string? text, int caret)
    {
        text ??= "";
        caret = Math.Clamp(caret, 0, text.Length);

        // Lexical state from the start up to the caret: open quote, and paren depth outside quotes.
        char? quote = null;
        var quoteStart = -1;
        var depth = 0;
        for (var i = 0; i < caret; i++)
        {
            var c = text[i];
            if (quote is { } q)
            {
                if (c == q) quote = null;
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                quoteStart = i;
            }
            else if (c == '(') depth++;
            else if (c == ')' && depth > 0) depth--;
        }

        if (quote is not null)
            return InsideQuote(text, caret, quoteStart);

        // Inside a `field:(A OR B …` list: the grammar does not complete list members.
        if (depth > 0 && OpenListBeforeCaret(text, caret))
            return QueryCaretToken.Nothing(caret);

        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] is not '(' and not ')')
            start--;

        var word = text[start..caret];
        var negated = false;
        var bodyStart = start;
        if (word.StartsWith('-'))
        {
            negated = true;
            bodyStart++;
            word = word[1..];
        }

        if (word.StartsWith('@'))
            return new QueryCaretToken(QueryCaretKind.Reference, null, word, bodyStart, caret, negated);

        var colon = word.IndexOf(':');
        if (colon < 0)
        {
            if (word.StartsWith(ContentPrefix, StringComparison.OrdinalIgnoreCase))
                return new QueryCaretToken(QueryCaretKind.ContentProperty, null,
                    word[ContentPrefix.Length..], bodyStart + ContentPrefix.Length, caret, negated);
            return new QueryCaretToken(QueryCaretKind.Keyword, null, word, bodyStart, caret, negated);
        }

        var field = word[..colon];
        if (field.Length == 0)
            return QueryCaretToken.Nothing(caret);

        // Value part: skip an ordered-comparison operator, then complete only the LAST alternative.
        var valueStart = bodyStart + colon + 1;
        var value = word[(colon + 1)..];
        var op = OperatorLength(value);
        var opText = value[..op];
        valueStart += op;
        value = value[op..];
        var bar = value.LastIndexOf('|');
        if (bar >= 0)
        {
            valueStart += bar + 1;
            value = value[(bar + 1)..];
        }
        // `select:` takes a comma list — complete the last entry.
        if (string.Equals(field, QueryGrammar.Select, StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.LastIndexOf(',');
            if (comma >= 0)
            {
                valueStart += comma + 1;
                value = value[(comma + 1)..];
            }
        }

        if (field.StartsWith(ContentPrefix, StringComparison.OrdinalIgnoreCase))
            return new QueryCaretToken(QueryCaretKind.ContentValue, field[ContentPrefix.Length..], value,
                valueStart, caret, negated) { Operator = opText };
        return new QueryCaretToken(QueryCaretKind.Value, field, value, valueStart, caret, negated) { Operator = opText };
    }

    private static QueryCaretToken InsideQuote(string text, int caret, int quoteStart)
    {
        // A quoted VALUE: `field:"…` (also after an operator or a `|`). A quote that opens a free-text
        // phrase has no field and nothing to complete.
        var j = quoteStart - 1;
        while (j >= 0 && text[j] is '|' or '>' or '<' or '=')
            j--;
        if (j < 0 || text[j] != ':')
            return QueryCaretToken.Nothing(caret);
        var fieldEnd = j;
        var fieldStart = fieldEnd;
        while (fieldStart > 0 && !char.IsWhiteSpace(text[fieldStart - 1]) && text[fieldStart - 1] is not '(' and not ')')
            fieldStart--;
        var negated = fieldStart < fieldEnd && text[fieldStart] == '-';
        if (negated) fieldStart++;
        var field = text[fieldStart..fieldEnd];
        if (field.Length == 0)
            return QueryCaretToken.Nothing(caret);
        var prefix = text[(quoteStart + 1)..caret];
        if (field.StartsWith(ContentPrefix, StringComparison.OrdinalIgnoreCase))
            return new QueryCaretToken(QueryCaretKind.ContentValue, field[ContentPrefix.Length..], prefix,
                quoteStart, caret, negated, Quoted: true);
        return new QueryCaretToken(QueryCaretKind.Value, field, prefix, quoteStart, caret, negated, Quoted: true);
    }

    private static bool OpenListBeforeCaret(string text, int caret)
    {
        // Find the innermost unclosed '(' before the caret; it opens a LIST when it directly follows a colon.
        var depth = 0;
        for (var i = caret - 1; i >= 0; i--)
        {
            if (text[i] == ')') depth++;
            else if (text[i] == '(')
            {
                if (depth == 0)
                    return i > 0 && text[i - 1] == ':';
                depth--;
            }
        }
        return false;
    }

    private static int OperatorLength(string value)
        => value.StartsWith(">=", StringComparison.Ordinal) || value.StartsWith("<=", StringComparison.Ordinal) ? 2
            : value.StartsWith('>') || value.StartsWith('<') ? 1
            : 0;
}
