using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeshWeaver.Yaml;

/// <summary>
/// A hand-written recursive-descent YAML reader that produces a <see cref="JsonNode"/> tree:
/// mapping → <see cref="JsonObject"/>, sequence → <see cref="JsonArray"/>, scalar →
/// <see cref="JsonValue"/> resolved with the YAML 1.2 core schema (null, booleans, integers,
/// floats; everything else, and every quoted scalar, a string).
///
/// <para>One instance reads one text and is discarded; its state is instance fields only.</para>
/// </summary>
internal sealed partial class YamlParser
{
    private readonly string s;
    private readonly bool strict;
    private int pos;
    private int line = 1;
    private int lineStart;
    private ImmutableDictionary<string, JsonNode?> anchors = ImmutableDictionary<string, JsonNode?>.Empty;
    private int depth;
    private long expansionBudget = MaxExpandedNodes;

    /// <summary>
    /// The deepest a collection may nest. The reader recurses per level, so an unbounded depth is an
    /// uncatchable stack overflow on hostile input; 64 is also System.Text.Json's default
    /// <c>MaxDepth</c>, so nothing deeper could be bound anyway.
    /// </summary>
    internal const int MaxDepth = 64;

    /// <summary>
    /// How many nodes alias and merge-key expansion may COPY in one text. Each alias is a deep copy,
    /// so anchors that alias earlier anchors grow exponentially ("billion laughs"); the budget turns
    /// that into a positioned refusal instead of an exhausted process.
    /// </summary>
    internal const long MaxExpandedNodes = 100_000;

    private YamlParser(string text, bool strict)
    {
        // Normalise line breaks once, so every rule below speaks only of '\n'.
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length > 0 && normalized[0] == '\uFEFF')
            normalized = normalized[1..];
        // '\0' is the cursor's end-of-input sentinel, so a literal NUL would read as an end that
        // never advances. YAML does not allow it in a stream; refuse it with its position.
        var nul = normalized.IndexOf('\0');
        if (nul >= 0)
        {
            var lastBreak = normalized.LastIndexOf('\n', Math.Max(nul - 1, 0));
            if (nul == 0) lastBreak = -1;
            var lineNumber = 1 + normalized.AsSpan(0, nul).Count('\n');
            throw new YamlParseException("a NUL character is not allowed in YAML text", lineNumber, nul - lastBreak);
        }
        s = normalized;
        this.strict = strict;
    }

    private void EnterCollection()
    {
        if (++depth > MaxDepth)
            throw Error($"collections nest deeper than {MaxDepth} levels");
    }

    /// <summary>A deep copy of an anchored node, charged against <see cref="MaxExpandedNodes"/>.</summary>
    private JsonNode? Expand(JsonNode? target, Mark mark)
    {
        if (target is null) return null;
        var size = CountNodes(target, expansionBudget + 1);
        if (size > expansionBudget)
            throw ErrorAt($"alias expansion exceeds {MaxExpandedNodes} copied nodes", mark);
        expansionBudget -= size;
        return target.DeepClone();
    }

    private static long CountNodes(JsonNode root, long cap)
    {
        var count = 0L;
        var pending = ImmutableStack.Create(root);
        while (!pending.IsEmpty && count <= cap)
        {
            pending = pending.Pop(out var node);
            count++;
            switch (node)
            {
                // A null child still costs the slot DeepClone allocates for it.
                case JsonObject o:
                    foreach (var (_, child) in o)
                        if (child is not null) pending = pending.Push(child);
                        else count++;
                    break;
                case JsonArray a:
                    foreach (var child in a)
                        if (child is not null) pending = pending.Push(child);
                        else count++;
                    break;
            }
        }
        return count;
    }

    /// <summary>Reads every document in <paramref name="text"/>.</summary>
    internal static ImmutableList<JsonNode?> ParseStream(string text, bool strict) =>
        new YamlParser(text, strict).ParseDocuments();

    // ───────────────────────────── cursor ─────────────────────────────

    private char Cur => pos < s.Length ? s[pos] : '\0';

    private char PeekAt(int offset) => pos + offset < s.Length ? s[pos + offset] : '\0';

    private bool AtEnd => pos >= s.Length;

    private int Column => pos - lineStart;

    private void Advance()
    {
        if (pos >= s.Length) return;
        if (s[pos] == '\n')
        {
            line++;
            lineStart = pos + 1;
        }
        pos++;
    }

    private void Advance(int count)
    {
        for (var i = 0; i < count; i++) Advance();
    }

    private readonly record struct Mark(int Pos, int Line, int LineStart);

    private Mark Save() => new(pos, line, lineStart);

    private void Restore(Mark mark)
    {
        pos = mark.Pos;
        line = mark.Line;
        lineStart = mark.LineStart;
    }

    private YamlParseException Error(string reason) => new(reason, line, Column + 1);

    private static YamlParseException ErrorAt(string reason, Mark mark) =>
        new(reason, mark.Line, mark.Pos - mark.LineStart + 1);

    private static bool IsBlank(char c) => c is ' ' or '\t';

    private static bool IsBlankOrEnd(char c) => c is ' ' or '\t' or '\n' or '\0';

    private static bool IsFlowIndicator(char c) => c is ',' or '[' or ']' or '{' or '}';

    private bool AtLineEndOrComment => Cur is '\n' or '\0' or '#';

    private void SkipInlineSpaces()
    {
        while (IsBlank(Cur)) Advance();
    }

    private void SkipToEndOfLine()
    {
        while (!AtEnd && Cur != '\n') Advance();
    }

    /// <summary>
    /// Moves past spaces, comments and line breaks to the next content character. A tab in the
    /// INDENTATION of a content line is refused: YAML forbids it, and there is no single reading
    /// of how far a tab indents.
    /// </summary>
    private void SkipToNextContent()
    {
        while (!AtEnd)
        {
            var atLineStart = pos == lineStart;
            var sawTab = false;
            while (IsBlank(Cur))
            {
                if (Cur == '\t') sawTab = true;
                Advance();
            }
            if (Cur == '#')
            {
                SkipToEndOfLine();
                continue;
            }
            if (Cur == '\n')
            {
                Advance();
                continue;
            }
            if (atLineStart && sawTab && !AtEnd)
                throw Error("a tab character is not allowed in indentation");
            return;
        }
    }

    private bool AtDocumentMarker(char marker) =>
        Column == 0 && PeekAt(0) == marker && PeekAt(1) == marker && PeekAt(2) == marker
        && IsBlankOrEnd(PeekAt(3));

    private bool AtAnyDocumentMarker => AtDocumentMarker('-') || AtDocumentMarker('.');

    private bool IsSequenceEntry => Cur == '-' && IsBlankOrEnd(PeekAt(1));

    // ───────────────────────────── documents ─────────────────────────────

    private ImmutableList<JsonNode?> ParseDocuments()
    {
        var documents = ImmutableList<JsonNode?>.Empty;
        while (true)
        {
            SkipToNextContent();
            // Directives (%YAML, %TAG) carry nothing a JSON tree can use.
            while (!AtEnd && Column == 0 && Cur == '%')
            {
                SkipToEndOfLine();
                SkipToNextContent();
            }
            if (AtEnd) break;

            JsonNode? root;
            if (AtDocumentMarker('-'))
            {
                Advance(3);
                anchors = ImmutableDictionary<string, JsonNode?>.Empty;
                root = ParseBlockNode(-1, sequenceAtParentIndent: false);
            }
            else if (AtDocumentMarker('.'))
            {
                Advance(3);
                SkipToEndOfLine();
                continue;
            }
            else
            {
                root = ParseNodeAtLineStart(Column, -1);
            }
            documents = documents.Add(root);

            SkipToNextContent();
            if (AtEnd) break;
            if (AtDocumentMarker('.'))
            {
                Advance(3);
                SkipToEndOfLine();
                continue;
            }
            if (AtDocumentMarker('-'))
                continue;
            throw Error("unexpected content after the end of the document");
        }
        return documents;
    }

    // ───────────────────────────── block structure ─────────────────────────────

    /// <summary>
    /// Reads the node that follows an indicator (<c>key:</c>, <c>---</c>): either inline on the
    /// same line, or on the following lines indented deeper than <paramref name="parentIndent"/>.
    /// A block sequence may sit AT the parent's indentation when it is a mapping value.
    /// </summary>
    private JsonNode? ParseBlockNode(int parentIndent, bool sequenceAtParentIndent)
    {
        SkipInlineSpaces();
        var props = ParseProperties();
        SkipInlineSpaces();
        JsonNode? node;
        if (AtLineEndOrComment)
            node = ParseNodeOnFollowingLines(parentIndent, sequenceAtParentIndent, props.Tag);
        else
            node = ParseInlineNode(parentIndent, props.Tag);
        return Apply(node, props);
    }

    /// <summary>
    /// Reads a node that starts on a following line. <paramref name="inheritedTag"/> is a tag
    /// written before the line break (<c>key: !!str</c>): the scalar must not be resolved before it
    /// applies, or <c>007</c> would already be the number 7.
    /// </summary>
    private JsonNode? ParseNodeOnFollowingLines(int parentIndent, bool sequenceAtParentIndent, string? inheritedTag)
    {
        SkipToNextContent();
        if (AtEnd || AtAnyDocumentMarker)
            return null;
        var col = Column;
        if (col > parentIndent)
            return ParseNodeAtLineStart(col, parentIndent, inheritedTag);
        if (col == parentIndent && sequenceAtParentIndent && IsSequenceEntry)
            return ParseBlockSequence(col);
        // Nothing nested: the value is empty, and the cursor is where the caller sees the next
        // sibling (or the end of its own collection).
        return null;
    }

    /// <summary>Reads a node whose first character is the first content on its line, at column <paramref name="col"/>.</summary>
    private JsonNode? ParseNodeAtLineStart(int col, int parentIndent, string? inheritedTag = null)
    {
        var props = ParseProperties();
        SkipInlineSpaces();
        var tag = props.Tag ?? inheritedTag;
        JsonNode? node;
        if (props.HasAny && AtLineEndOrComment)
        {
            // Properties alone on a line put the node on the next one — another level of recursion.
            EnterCollection();
            try
            {
                node = ParseNodeOnFollowingLines(parentIndent, sequenceAtParentIndent: false, tag);
            }
            finally
            {
                depth--;
            }
        }
        else if (IsSequenceEntry)
            node = ParseBlockSequence(Column);
        else if (LineIsMappingKey())
            node = ParseBlockMapping(col);
        else
            node = ParseInlineNode(parentIndent, tag);
        return Apply(node, props);
    }

    /// <summary>
    /// Whether the text from the cursor to the end of the line is <c>key: …</c> — a quoted or
    /// plain key followed by a <c>:</c> that is itself followed by a blank or the line end.
    /// </summary>
    private bool LineIsMappingKey()
    {
        var i = pos;
        if (i >= s.Length) return false;
        var c = s[i];
        if (c is '"' or '\'')
        {
            i = SkipQuotedOnLine(i);
            if (i < 0) return false;
            while (i < s.Length && IsBlank(s[i])) i++;
            return i < s.Length && s[i] == ':' && (i + 1 >= s.Length || IsBlankOrEnd(s[i + 1]));
        }
        if (c is '[' or '{' or '#' or '|' or '>' or '*' or '&' or '!')
            return false;
        if (c == '?' && (i + 1 >= s.Length || IsBlankOrEnd(s[i + 1])))
            throw Error("explicit '?' mapping keys are not supported");
        for (; i < s.Length && s[i] != '\n'; i++)
        {
            if (s[i] == ':' && (i + 1 >= s.Length || IsBlankOrEnd(s[i + 1])))
                return true;
            if (s[i] == '#' && i > pos && IsBlank(s[i - 1]))
                return false;
        }
        return false;
    }

    /// <summary>Index just past the quoted scalar starting at <paramref name="start"/>, or -1 when it does not close on that line.</summary>
    private int SkipQuotedOnLine(int start)
    {
        var quote = s[start];
        var i = start + 1;
        while (i < s.Length && s[i] != '\n')
        {
            if (quote == '"' && s[i] == '\\')
            {
                i += 2;
                continue;
            }
            if (s[i] == quote)
            {
                if (quote == '\'' && i + 1 < s.Length && s[i + 1] == '\'')
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return -1;
    }

    private JsonObject ParseBlockMapping(int indent)
    {
        EnterCollection();
        try
        {
            return ParseBlockMappingEntries(indent);
        }
        finally
        {
            depth--;
        }
    }

    private JsonObject ParseBlockMappingEntries(int indent)
    {
        var start = Save();
        var map = new JsonObject();
        var merges = ImmutableList<JsonNode?>.Empty;
        while (true)
        {
            var keyMark = Save();
            var (key, isMergeKey) = ParseMappingKey();
            var value = ParseBlockNode(indent, sequenceAtParentIndent: true);
            if (isMergeKey)
                merges = merges.Add(value);
            else
            {
                // A duplicate keeps its LAST value, replaced in place (as System.Text.Json does).
                if (strict && map.ContainsKey(key))
                    throw ErrorAt($"duplicate mapping key '{key}'", keyMark);
                map[key] = value;
            }

            SkipToNextContent();
            if (AtEnd || AtAnyDocumentMarker)
                break;
            var col = Column;
            if (col < indent)
                break;
            if (col > indent)
                throw Error("bad indentation of a mapping entry");
            if (IsSequenceEntry)
                throw Error("a sequence entry is not allowed between the entries of a mapping");
            if (!LineIsMappingKey())
                throw Error("expected a 'key: value' mapping entry");
        }
        return ApplyMerges(map, merges, start);
    }

    private (string Key, bool IsMergeKey) ParseMappingKey()
    {
        string key;
        var plain = false;
        if (Cur == '"')
            key = ParseDoubleQuoted();
        else if (Cur == '\'')
            key = ParseSingleQuoted();
        else
        {
            var start = pos;
            while (!AtEnd && !(Cur == ':' && IsBlankOrEnd(PeekAt(1))))
                Advance();
            key = s[start..pos].TrimEnd(' ', '\t');
            plain = true;
        }
        SkipInlineSpaces();
        if (Cur != ':')
            throw Error("expected ':' after a mapping key");
        Advance();
        return (key, plain && key == "<<");
    }

    private JsonObject ApplyMerges(JsonObject map, ImmutableList<JsonNode?> merges, Mark mark)
    {
        // Explicit keys win; among merged sources, the first that names a key wins.
        foreach (var merge in merges)
        {
            ImmutableList<JsonObject> sources = merge switch
            {
                null => [],
                JsonObject single => [single],
                JsonArray many => many.OfType<JsonObject>().ToImmutableList(),
                _ => throw Error("a '<<' merge key needs a mapping or a sequence of mappings"),
            };
            foreach (var source in sources)
                foreach (var (k, v) in source)
                    if (!map.ContainsKey(k))
                        map[k] = Expand(v, mark);
        }
        return map;
    }

    private JsonArray ParseBlockSequence(int indent)
    {
        EnterCollection();
        try
        {
            return ParseBlockSequenceEntries(indent);
        }
        finally
        {
            depth--;
        }
    }

    private JsonArray ParseBlockSequenceEntries(int indent)
    {
        var sequence = new JsonArray();
        while (true)
        {
            Advance(); // '-'
            sequence.Add(ParseSequenceItem(indent));

            SkipToNextContent();
            if (AtEnd || AtAnyDocumentMarker)
                break;
            var col = Column;
            if (col < indent)
                break;
            if (col > indent)
                throw Error("bad indentation of a sequence entry");
            if (!IsSequenceEntry)
                break;
        }
        return sequence;
    }

    private JsonNode? ParseSequenceItem(int indent)
    {
        SkipInlineSpaces();
        var col = Column;
        var props = ParseProperties();
        SkipInlineSpaces();
        JsonNode? node;
        if (AtLineEndOrComment)
            node = ParseNodeOnFollowingLines(indent, sequenceAtParentIndent: false, props.Tag);
        else if (IsSequenceEntry)
            node = ParseBlockSequence(Column);
        else if (LineIsMappingKey())
            node = ParseBlockMapping(props.HasAny ? col : Column);
        else
            node = ParseInlineNode(indent, props.Tag);
        return Apply(node, props);
    }

    // ───────────────────────────── node properties ─────────────────────────────

    private readonly record struct Properties(string? Anchor, string? Tag)
    {
        public bool HasAny => Anchor is not null || Tag is not null;
    }

    private Properties ParseProperties()
    {
        string? anchor = null, tag = null;
        while (true)
        {
            if (Cur == '&')
            {
                Advance();
                anchor = ReadName();
                if (anchor.Length == 0) throw Error("an anchor needs a name");
            }
            else if (Cur == '!')
            {
                var start = pos;
                if (PeekAt(1) == '<')
                {
                    // A verbatim tag (!<tag:yaml.org,2002:str>) may carry flow indicators; it ends at '>'.
                    while (!AtEnd && Cur != '>' && Cur != '\n') Advance();
                    if (Cur != '>')
                        throw Error("unterminated verbatim tag");
                    Advance();
                }
                else
                    while (!IsBlankOrEnd(Cur) && !IsFlowIndicator(Cur)) Advance();
                tag = s[start..pos];
            }
            else
                break;
            SkipInlineSpaces();
        }
        return new Properties(anchor, tag);
    }

    private string ReadName()
    {
        var start = pos;
        while (!IsBlankOrEnd(Cur) && !IsFlowIndicator(Cur)) Advance();
        return s[start..pos];
    }

    private JsonNode? Apply(JsonNode? node, Properties props)
    {
        if (props.Tag is not null)
            node = ApplyTag(node, props.Tag);
        if (props.Anchor is not null)
            anchors = anchors.SetItem(props.Anchor, node);
        return node;
    }

    private static JsonNode? ApplyTag(JsonNode? node, string tag)
    {
        var name = tag switch
        {
            _ when tag.StartsWith("!!", StringComparison.Ordinal) => tag[2..],
            _ when tag.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal) => tag["tag:yaml.org,2002:".Length..],
            _ when tag.StartsWith("!<tag:yaml.org,2002:", StringComparison.Ordinal) => tag["!<tag:yaml.org,2002:".Length..].TrimEnd('>'),
            _ => null,
        };
        if (name is null || node is JsonObject or JsonArray)
            return node; // application tags carry nothing a JSON tree can use
        var text = YamlScalars.Text(node);
        return name switch
        {
            "str" => JsonValue.Create(text ?? string.Empty),
            "null" => null,
            "bool" or "int" or "float" when text is not null => YamlScalars.Resolve(text) ?? node,
            _ => node,
        };
    }

    // ───────────────────────────── inline nodes ─────────────────────────────

    /// <summary>
    /// Reads a node that starts at the cursor and is NOT a block collection: an alias, a block
    /// scalar, a flow collection, a quoted scalar or a plain scalar. Continuation lines of a
    /// multi-line scalar must be indented deeper than <paramref name="parentIndent"/>.
    /// </summary>
    private JsonNode? ParseInlineNode(int parentIndent, string? tag)
    {
        switch (Cur)
        {
            case '*':
                {
                    var mark = Save();
                    Advance();
                    var name = ReadName();
                    if (!anchors.TryGetValue(name, out var target))
                        throw ErrorAt($"unknown alias '*{name}'", mark);
                    ExpectLineEnd("an alias");
                    return Expand(target, mark);
                }
            case '|':
            case '>':
                return JsonValue.Create(ParseBlockScalar(parentIndent));
            case '[':
            case '{':
                {
                    var node = ParseFlowNode();
                    ExpectLineEnd("a flow collection");
                    return node;
                }
            case '"':
                {
                    var text = ParseDoubleQuoted();
                    ExpectLineEnd("a quoted scalar");
                    return JsonValue.Create(text);
                }
            case '\'':
                {
                    var text = ParseSingleQuoted();
                    ExpectLineEnd("a quoted scalar");
                    return JsonValue.Create(text);
                }
            default:
                {
                    RefuseReservedPlainStart();
                    var text = ParsePlainBlock(parentIndent);
                    return tag is null ? YamlScalars.Resolve(text) : JsonValue.Create(text);
                }
        }
    }

    /// <summary>
    /// <c>@</c> and the backtick are reserved, <c>%</c> is the directive indicator and <c>, ] }</c>
    /// are flow indicators: none may START a plain scalar (YAML 1.2 ns-plain-first). Strict mode
    /// refuses them; the tolerant reader reads the text as written.
    /// </summary>
    private void RefuseReservedPlainStart()
    {
        if (strict && Cur is '@' or '`' or '%' or ',' or ']' or '}')
            throw Error($"a plain scalar cannot start with '{Cur}' (quote the value)");
    }

    private void ExpectLineEnd(string after)
    {
        SkipInlineSpaces();
        if (!AtLineEndOrComment)
            throw Error($"unexpected content after {after}");
    }

    /// <summary>
    /// A plain scalar in block context. It ends at a comment (<c> #</c>) or at a line that is not
    /// indented deeper than <paramref name="parentIndent"/>; continuation lines fold into one
    /// space, blank lines into line breaks. 🚨 TOLERANT: a <c>": "</c> inside the value is text,
    /// because a mapping cannot start in the middle of a value line — the only other reading is a
    /// refusal. <see cref="YamlParseOptions.Strict"/> refuses it as the YAML grammar does.
    /// </summary>
    private string ParsePlainBlock(int parentIndent)
    {
        var sb = new StringBuilder();
        var endedByComment = ReadPlainSegment(sb);
        while (!endedByComment)
        {
            var before = Save();
            if (Cur != '\n')
                break;
            Advance();
            var emptyLines = 0;
            while (true)
            {
                var lineMark = Save();
                SkipInlineSpaces();
                if (Cur == '\n')
                {
                    emptyLines++;
                    Advance();
                    continue;
                }
                Restore(lineMark);
                break;
            }
            var indentStart = pos;
            while (Cur == ' ') Advance();
            var indent = pos - indentStart;
            SkipInlineSpaces();
            if (AtEnd || indent <= parentIndent || Cur == '#' || (indent == 0 && AtAnyDocumentMarkerAt(indentStart)))
            {
                Restore(before);
                break;
            }
            var segment = new StringBuilder();
            endedByComment = ReadPlainSegment(segment);
            sb.Append(emptyLines == 0 ? " " : new string('\n', emptyLines));
            sb.Append(segment);
        }
        return sb.ToString();
    }

    private bool AtAnyDocumentMarkerAt(int index) =>
        index + 2 < s.Length
        && ((s[index] == '-' && s[index + 1] == '-' && s[index + 2] == '-')
            || (s[index] == '.' && s[index + 1] == '.' && s[index + 2] == '.'))
        && (index + 3 >= s.Length || IsBlankOrEnd(s[index + 3]));

    /// <summary>Reads one line of a plain scalar into <paramref name="sb"/>, trimmed; returns whether a comment ended it.</summary>
    private bool ReadPlainSegment(StringBuilder sb)
    {
        var start = pos;
        var endedByComment = false;
        while (!AtEnd && Cur != '\n')
        {
            if (Cur == '#' && pos > start && IsBlank(s[pos - 1]))
            {
                endedByComment = true;
                break;
            }
            if (strict && Cur == ':' && IsBlankOrEnd(PeekAt(1)))
                throw Error("mapping values are not allowed in a plain scalar (quote the value)");
            Advance();
        }
        sb.Append(s.AsSpan(start, pos - start).TrimEnd(" \t"));
        if (endedByComment)
            SkipToEndOfLine();
        return endedByComment;
    }

    // ───────────────────────────── block scalars ─────────────────────────────

    private string ParseBlockScalar(int parentIndent)
    {
        var headerMark = Save();
        var literal = Cur == '|';
        Advance();
        var chomp = '\0';
        var explicitIndent = 0;
        for (var k = 0; k < 2; k++)
        {
            if (Cur is '-' or '+' && chomp == '\0')
            {
                chomp = Cur;
                Advance();
            }
            else if (Cur is >= '1' and <= '9' && explicitIndent == 0)
            {
                explicitIndent = Cur - '0';
                Advance();
            }
        }
        SkipInlineSpaces();
        if (Cur == '#')
            SkipToEndOfLine();
        if (!(Cur is '\n' or '\0'))
            throw ErrorAt("invalid block scalar header", headerMark);
        Advance();

        int contentIndent;
        if (explicitIndent > 0)
            contentIndent = (parentIndent < 0 ? 0 : parentIndent) + explicitIndent;
        else
        {
            // Auto-detect: the indentation of the first non-empty line.
            contentIndent = -1;
            for (var i = pos; i < s.Length;)
            {
                var j = i;
                while (j < s.Length && s[j] == ' ') j++;
                // Indentation is spaces only; a tab after them is CONTENT (a line of blanks is empty).
                var k = j;
                while (k < s.Length && IsBlank(s[k])) k++;
                if (k < s.Length && s[k] != '\n')
                {
                    contentIndent = j - i;
                    break;
                }
                while (j < s.Length && s[j] != '\n') j++;
                i = j + 1;
            }
            if (contentIndent <= parentIndent || contentIndent < 0)
                contentIndent = int.MaxValue; // empty scalar: no line belongs to it
        }

        var contentStart = pos;
        var lastContentEnd = -1;
        var lines = ImmutableList<string>.Empty;
        var leadingMark = Save();
        while (!AtEnd)
        {
            var lineMark = Save();
            var spaces = 0;
            while (Cur == ' ' && spaces < contentIndent) { Advance(); spaces++; }
            var restStart = pos;
            while (IsBlank(Cur)) Advance();
            if (Cur is '\n' or '\0')
            {
                // A line of blanks belongs to the scalar whatever its indentation. Up to the content
                // indentation it is EMPTY; whitespace past it is CONTENT (YAML 1.2 l-empty vs
                // l-nb-literal-text) — except before the first content line, where a longer blank
                // line is an error in the grammar and read as empty by the tolerant reader.
                var extra = spaces < contentIndent ? string.Empty : s[restStart..pos];
                if (extra.Length > 0 && lastContentEnd < 0)
                {
                    if (strict)
                        throw ErrorAt("a leading empty line of a block scalar is indented past its content", leadingMark);
                    extra = string.Empty;
                }
                lines = lines.Add(extra);
                if (extra.Length > 0)
                    lastContentEnd = pos;
                if (Cur == '\n') Advance();
                continue;
            }
            if (spaces < contentIndent || (spaces == 0 && AtAnyDocumentMarkerAt(lineMark.Pos)))
            {
                Restore(lineMark);
                break;
            }
            pos = restStart;
            var textStart = pos;
            SkipToEndOfLine();
            lines = lines.Add(s[textStart..pos]);
            lastContentEnd = pos;
            if (Cur == '\n') Advance();
        }

        // Trailing empty lines are governed by chomping, never by the body.
        var trailing = 0;
        while (trailing < lines.Count && lines[lines.Count - 1 - trailing].Length == 0)
            trailing++;
        var body = lines.Take(lines.Count - trailing).ToImmutableList();
        // The cursor now sits at the start of the line that ended the scalar, so the caller sees it.

        // Chomping governs the line breaks that ACTUALLY followed the content — clip keeps one,
        // keep keeps them all — so a scalar ending at the end of the text without a break gains none.
        var breaksFrom = body.Count == 0 || lastContentEnd < 0 ? contentStart : lastContentEnd;
        var breaks = breaksFrom >= pos ? 0 : s.AsSpan(breaksFrom, pos - breaksFrom).Count('\n');
        var text = literal ? string.Join('\n', body) : Fold(body);
        if (body.Count == 0)
            return chomp == '+' ? new string('\n', breaks) : string.Empty;
        return chomp switch
        {
            '-' => text,
            '+' => text + new string('\n', breaks),
            _ => breaks > 0 ? text + "\n" : text,
        };
    }

    private static string Fold(ImmutableList<string> lines)
    {
        var sb = new StringBuilder();
        var pendingEmpty = 0;
        var seenContent = false;
        var previousMoreIndented = false;
        foreach (var l in lines)
        {
            if (l.Length == 0)
            {
                pendingEmpty++;
                continue;
            }
            var moreIndented = l[0] is ' ' or '\t';
            if (!seenContent)
                sb.Append('\n', pendingEmpty);
            else if (!previousMoreIndented && !moreIndented)
                sb.Append(pendingEmpty == 0 ? " " : new string('\n', pendingEmpty));
            else
                sb.Append('\n', pendingEmpty + 1);
            sb.Append(l);
            seenContent = true;
            previousMoreIndented = moreIndented;
            pendingEmpty = 0;
        }
        return sb.ToString();
    }

    // ───────────────────────────── quoted scalars ─────────────────────────────

    private string ParseDoubleQuoted()
    {
        var open = Save();
        Advance();
        var sb = new StringBuilder();
        while (true)
        {
            if (AtEnd)
                throw ErrorAt("unterminated double-quoted scalar", open);
            var c = Cur;
            if (c == '"')
            {
                Advance();
                return sb.ToString();
            }
            if (c == '\\')
            {
                Advance();
                var e = Cur;
                if (e == '\n')
                {
                    Advance();
                    SkipInlineSpaces();
                    continue;
                }
                Advance();
                switch (e)
                {
                    case '0': sb.Append('\0'); break;
                    case 'a': sb.Append('\a'); break;
                    case 'b': sb.Append('\b'); break;
                    case 't': case '\t': sb.Append('\t'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'v': sb.Append('\v'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'e': sb.Append('\u001B'); break;
                    case ' ': sb.Append(' '); break;
                    case '"': sb.Append('"'); break;
                    case '/': sb.Append('/'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'N': sb.Append('\u0085'); break;
                    case '_': sb.Append('\u00A0'); break;
                    case 'L': sb.Append('\u2028'); break;
                    case 'P': sb.Append('\u2029'); break;
                    case 'x': sb.Append((char)ReadHexEscape(2)); break;
                    case 'u': sb.Append(ReadUtf16Escape()); break;
                    case 'U': sb.Append(CodePoint(ReadHexEscape(8))); break;
                    default:
                        if (strict)
                            throw Error($"unknown escape sequence '\\{e}'");
                        sb.Append('\\').Append(e);
                        break;
                }
                continue;
            }
            if (IsBlank(c) || c == '\n')
            {
                if (FoldQuotedWhitespace(sb))
                    continue;
            }
            sb.Append(c);
            Advance();
        }
    }

    private string ParseSingleQuoted()
    {
        var open = Save();
        Advance();
        var sb = new StringBuilder();
        while (true)
        {
            if (AtEnd)
                throw ErrorAt("unterminated single-quoted scalar", open);
            var c = Cur;
            if (c == '\'')
            {
                if (PeekAt(1) == '\'')
                {
                    sb.Append('\'');
                    Advance(2);
                    continue;
                }
                Advance();
                return sb.ToString();
            }
            if (IsBlank(c) || c == '\n')
            {
                if (FoldQuotedWhitespace(sb))
                    continue;
            }
            sb.Append(c);
            Advance();
        }
    }

    /// <summary>
    /// Folds a line break inside a quoted scalar: trailing and leading whitespace around it is
    /// dropped, a single break becomes a space, and each blank line becomes one line break.
    /// Returns <c>false</c> when the whitespace at the cursor does not end a line (it is content).
    /// </summary>
    private bool FoldQuotedWhitespace(StringBuilder sb)
    {
        var j = pos;
        while (j < s.Length && IsBlank(s[j])) j++;
        if (j >= s.Length || s[j] != '\n')
        {
            // Inner whitespace is content: take the whole run ONCE, so a long run is read in one
            // pass rather than re-scanned from every character of it.
            sb.Append(s, pos, j - pos);
            while (pos < j) Advance();
            return true;
        }
        while (pos < j) Advance();
        Advance(); // '\n'
        var emptyLines = 0;
        while (true)
        {
            SkipInlineSpaces();
            if (Cur == '\n')
            {
                emptyLines++;
                Advance();
                continue;
            }
            break;
        }
        sb.Append(emptyLines == 0 ? " " : new string('\n', emptyLines));
        return true;
    }

    private long ReadHexEscape(int digits)
    {
        var start = pos;
        for (var k = 0; k < digits; k++)
        {
            if (!Uri.IsHexDigit(Cur))
                throw Error($"invalid escape: expected {digits} hexadecimal digits");
            Advance();
        }
        return long.Parse(s.AsSpan(start, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>A Unicode scalar value as text; anything else (a surrogate, beyond U+10FFFF) is refused with its position.</summary>
    private string CodePoint(long code)
    {
        if (code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF)
            throw Error($"escape U+{code:X} is not a Unicode scalar value");
        return char.ConvertFromUtf32((int)code);
    }

    /// <summary>
    /// <c>\uXXXX</c>. A high surrogate followed directly by a <c>\u</c> low surrogate is the
    /// JSON spelling of one character and is read as that pair; a lone surrogate is refused.
    /// </summary>
    private string ReadUtf16Escape()
    {
        var code = ReadHexEscape(4);
        if (code is >= 0xD800 and <= 0xDBFF && Cur == '\\' && PeekAt(1) == 'u')
        {
            var mark = Save();
            Advance(2);
            var low = ReadHexEscape(4);
            if (low is >= 0xDC00 and <= 0xDFFF)
                return new string([(char)code, (char)low]);
            Restore(mark);
        }
        return CodePoint(code);
    }

    // ───────────────────────────── flow collections ─────────────────────────────

    private void SkipFlowWhitespace()
    {
        while (!AtEnd)
        {
            if (IsBlank(Cur) || Cur == '\n')
            {
                Advance();
                continue;
            }
            if (Cur == '#' && (pos == 0 || IsBlankOrEnd(s[pos - 1])))
            {
                SkipToEndOfLine();
                continue;
            }
            break;
        }
    }

    private JsonNode? ParseFlowNode() => ParseFlowNode(out _);

    /// <summary>Reads a flow node; <paramref name="plainText"/> is the written text when it was an untagged plain scalar (a key keeps that text).</summary>
    private JsonNode? ParseFlowNode(out string? plainText)
    {
        plainText = null;
        var props = ParseProperties();
        SkipFlowWhitespace();
        JsonNode? node;
        switch (Cur)
        {
            case '[':
                node = ParseFlowSequence();
                break;
            case '{':
                node = ParseFlowMapping();
                break;
            case '"':
                node = JsonValue.Create(ParseDoubleQuoted());
                break;
            case '\'':
                node = JsonValue.Create(ParseSingleQuoted());
                break;
            case '*':
                {
                    var mark = Save();
                    Advance();
                    var name = ReadName();
                    if (!anchors.TryGetValue(name, out var target))
                        throw ErrorAt($"unknown alias '*{name}'", mark);
                    node = Expand(target, mark);
                    break;
                }
            default:
                {
                    RefuseReservedPlainStart();
                    var text = ParsePlainFlow();
                    // An entry with nothing in it ([a,,b], {,}) is not YAML. An empty KEY before ':'
                    // ({: v}) and a tagged or anchored empty node are.
                    if (text.Length == 0 && !props.HasAny && Cur != ':')
                        throw Error("a flow collection entry is empty");
                    if (props.Tag is null)
                        plainText = text;
                    node = props.Tag is null ? YamlScalars.Resolve(text) : JsonValue.Create(text);
                    break;
                }
        }
        return Apply(node, props);
    }

    private JsonArray ParseFlowSequence()
    {
        EnterCollection();
        try
        {
            return ParseFlowSequenceEntries();
        }
        finally
        {
            depth--;
        }
    }

    private JsonArray ParseFlowSequenceEntries()
    {
        var open = Save();
        Advance(); // '['
        var sequence = new JsonArray();
        while (true)
        {
            SkipFlowWhitespace();
            if (AtEnd) throw ErrorAt("unterminated flow sequence", open);
            if (Cur == ']')
            {
                Advance();
                return sequence;
            }
            var jsonLike = Cur is '"' or '\'' or '[' or '{';
            var item = ParseFlowNode(out var itemText);
            SkipFlowWhitespace();
            if (Cur == ':' && (jsonLike || IsBlankOrEnd(PeekAt(1)) || IsFlowIndicator(PeekAt(1))))
            {
                // A single-pair mapping inside a sequence: [a: 1, b: 2].
                Advance();
                SkipFlowWhitespace();
                var value = Cur is ',' or ']' ? null : ParseFlowNode();
                item = new JsonObject { [itemText ?? FlowKey(item)] = value };
                SkipFlowWhitespace();
            }
            sequence.Add(item);
            if (Cur == ',')
            {
                Advance();
                continue;
            }
            if (Cur == ']')
            {
                Advance();
                return sequence;
            }
            throw AtEnd ? ErrorAt("unterminated flow sequence", open) : Error("expected ',' or ']' in a flow sequence");
        }
    }

    private JsonObject ParseFlowMapping()
    {
        EnterCollection();
        try
        {
            return ParseFlowMappingEntries();
        }
        finally
        {
            depth--;
        }
    }

    private JsonObject ParseFlowMappingEntries()
    {
        var open = Save();
        Advance(); // '{'
        var map = new JsonObject();
        var merges = ImmutableList<JsonNode?>.Empty;
        while (true)
        {
            SkipFlowWhitespace();
            if (AtEnd) throw ErrorAt("unterminated flow mapping", open);
            if (Cur == '}')
            {
                Advance();
                return ApplyMerges(map, merges, open);
            }
            var keyMark = Save();
            var plainKey = !(Cur is '"' or '\'' or '[' or '{');
            // A plain key keeps its written text, as in a block mapping: {null: a, '': b} has two keys.
            var keyNode = ParseFlowNode(out var keyText);
            var key = keyText ?? FlowKey(keyNode);
            SkipFlowWhitespace();
            JsonNode? value = null;
            if (Cur == ':')
            {
                Advance();
                SkipFlowWhitespace();
                if (!(Cur is ',' or '}'))
                    value = ParseFlowNode();
                SkipFlowWhitespace();
            }
            if (plainKey && key == "<<")
                merges = merges.Add(value);
            else
            {
                // A duplicate keeps its LAST value, replaced in place (as System.Text.Json does).
                if (strict && map.ContainsKey(key))
                    throw ErrorAt($"duplicate mapping key '{key}'", keyMark);
                map[key] = value;
            }
            if (Cur == ',')
            {
                Advance();
                continue;
            }
            if (Cur == '}')
            {
                Advance();
                return ApplyMerges(map, merges, open);
            }
            throw AtEnd ? ErrorAt("unterminated flow mapping", open) : Error("expected ',' or '}' in a flow mapping");
        }
    }

    private static string FlowKey(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue => YamlScalars.Text(node) ?? string.Empty,
        _ => node.ToJsonString(),
    };

    /// <summary>A plain scalar inside a flow collection: it also ends at <c>, [ ] { }</c> and folds line breaks.</summary>
    private string ParsePlainFlow()
    {
        var sb = new StringBuilder();
        var start = pos;
        while (!AtEnd)
        {
            var c = Cur;
            if (IsFlowIndicator(c))
                break;
            if (c == ':' && (IsBlankOrEnd(PeekAt(1)) || IsFlowIndicator(PeekAt(1))))
                break;
            if (c == '#' && pos > start && IsBlankOrEnd(s[pos - 1]))
                break;
            if (c == '\n')
            {
                // Fold: trim, then continue on the next line unless it ends the scalar.
                var before = Save();
                var emptyLines = 0;
                Advance();
                while (true)
                {
                    SkipInlineSpaces();
                    if (Cur == '\n') { emptyLines++; Advance(); continue; }
                    break;
                }
                if (AtEnd || IsFlowIndicator(Cur) || Cur == '#'
                    || (Cur == ':' && (IsBlankOrEnd(PeekAt(1)) || IsFlowIndicator(PeekAt(1)))))
                {
                    Restore(before);
                    break;
                }
                TrimEndBlanks(sb);
                sb.Append(emptyLines == 0 ? " " : new string('\n', emptyLines));
                start = pos;
                continue;
            }
            sb.Append(c);
            Advance();
        }
        TrimEndBlanks(sb);
        return sb.ToString();
    }

    private static void TrimEndBlanks(StringBuilder sb)
    {
        while (sb.Length > 0 && IsBlank(sb[^1])) sb.Length--;
    }
}

/// <summary>Scalar resolution (YAML 1.2 core schema) and the reverse: a scalar node's YAML text.</summary>
internal static partial class YamlScalars
{
    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex DecimalInt();

    [GeneratedRegex(@"^0o[0-7]+$")]
    private static partial Regex OctalInt();

    [GeneratedRegex(@"^0x[0-9a-fA-F]+$")]
    private static partial Regex HexInt();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex Float();

    /// <summary>Resolves a PLAIN scalar's text to a node: null, a boolean, a number, or a string.</summary>
    internal static JsonNode? Resolve(string text)
    {
        switch (text)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
            case ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF":
                return JsonValue.Create("Infinity");
            case "-.inf" or "-.Inf" or "-.INF":
                return JsonValue.Create("-Infinity");
            case ".nan" or ".NaN" or ".NAN":
                return JsonValue.Create("NaN");
        }
        if (text.Length > 0 && (char.IsAsciiDigit(text[0]) || text[0] is '-' or '+' or '.'))
        {
            if (DecimalInt().IsMatch(text))
                return Number(NormalizeInteger(text));
            if (OctalInt().IsMatch(text))
                return Number(ParseRadix(text[2..], 8).ToString(CultureInfo.InvariantCulture));
            if (HexInt().IsMatch(text))
                return Number(ParseRadix(text[2..], 16).ToString(CultureInfo.InvariantCulture));
            if (Float().IsMatch(text))
                return Number(NormalizeFloat(text));
        }
        return JsonValue.Create(text);
    }

    /// <summary>A number node that keeps its digits exactly as written (no double round-trip).</summary>
    private static JsonNode Number(string json) => JsonNode.Parse(json)!;

    private static string NormalizeInteger(string text)
    {
        var negative = text[0] == '-';
        var digits = text.TrimStart('-', '+').TrimStart('0');
        if (digits.Length == 0) digits = "0";
        return negative ? "-" + digits : digits;
    }

    private static string NormalizeFloat(string text)
    {
        var negative = text[0] == '-';
        var body = text.TrimStart('-', '+');
        var exponentAt = body.IndexOfAny(['e', 'E']);
        var mantissa = exponentAt < 0 ? body : body[..exponentAt];
        var exponent = exponentAt < 0 ? string.Empty : body[exponentAt..];
        var dot = mantissa.IndexOf('.');
        var integerPart = dot < 0 ? mantissa : mantissa[..dot];
        var fraction = dot < 0 ? null : mantissa[(dot + 1)..];
        integerPart = integerPart.TrimStart('0');
        if (integerPart.Length == 0) integerPart = "0";
        var normalized = integerPart + (fraction is null ? string.Empty : "." + (fraction.Length == 0 ? "0" : fraction)) + exponent;
        return negative ? "-" + normalized : normalized;
    }

    private static BigInteger ParseRadix(string digits, int radix)
    {
        var value = BigInteger.Zero;
        foreach (var c in digits)
            value = value * radix + Convert.ToInt32(c.ToString(), 16);
        return value;
    }

    /// <summary>
    /// A scalar node's text as YAML wrote it: a string's value, <c>true</c>/<c>false</c>, a
    /// number's digits. <c>null</c> for a null node; a collection's JSON text otherwise.
    /// </summary>
    internal static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v => v.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            System.Text.Json.JsonValueKind.Null => null,
            _ => v.ToJsonString(),
        },
        _ => node.ToJsonString(),
    };
}
