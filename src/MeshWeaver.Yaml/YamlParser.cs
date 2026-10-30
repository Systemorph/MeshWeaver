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
    private ImmutableDictionary<string, string> tagHandles = ImmutableDictionary<string, string>.Empty;
    private int continuationMinColumn;
    private long expansionBudget = MaxExpandedNodes;

    /// <summary>
    /// The SOURCE spelling of every plain scalar whose resolved node reads differently
    /// (<c>00123</c> → 123, <c>0x1F</c> → 31, <c>TRUE</c> → true, <c>.inf</c> → "Infinity"), keyed
    /// by the node's REFERENCE. Local to this one parse: the tree stays core-schema-correct, and
    /// <see cref="TolerantYaml.Deserialize{T}"/> reads this table to bind such a scalar into a
    /// <see cref="string"/> as it was written (#7398).
    /// </summary>
    private ImmutableDictionary<JsonNode, string> spellings =
        ImmutableDictionary.Create<JsonNode, string>(ReferenceEqualityComparer.Instance);

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

    /// <summary>
    /// A deep copy of an anchored node, charged against <see cref="MaxExpandedNodes"/>, and placed
    /// under the collections open at the cursor — so its own depth counts toward
    /// <see cref="MaxDepth"/> as well (aliases of aliases must not build a tree deeper than the
    /// reader would parse).
    /// </summary>
    private JsonNode? Expand(JsonNode? target, Mark mark)
    {
        if (target is null) return null;
        var (size, levels) = Measure(target, expansionBudget + 1);
        if (size > expansionBudget)
            throw ErrorAt($"alias expansion exceeds {MaxExpandedNodes} copied nodes", mark);
        if (depth + levels > MaxDepth)
            throw ErrorAt($"alias expansion nests deeper than {MaxDepth} levels", mark);
        expansionBudget -= size;
        var copy = target.DeepClone();
        if (!spellings.IsEmpty)
            CopySpellings(target, copy);
        return copy;
    }

    /// <summary>
    /// Carries the source spellings of <paramref name="original"/>'s scalars over to the same
    /// positions of its deep copy, so an alias or a merged key binds into a string exactly as the
    /// anchored text was written.
    /// </summary>
    private void CopySpellings(JsonNode original, JsonNode copy)
    {
        switch (original)
        {
            case JsonValue when spellings.TryGetValue(original, out var text):
                spellings = spellings.SetItem(copy, text);
                break;
            case JsonObject map:
                foreach (var (from, to) in map.Zip((JsonObject)copy))
                    if (from.Value is not null && to.Value is not null)
                        CopySpellings(from.Value, to.Value);
                break;
            case JsonArray seq:
                foreach (var (from, to) in seq.Zip((JsonArray)copy))
                    if (from is not null && to is not null)
                        CopySpellings(from, to);
                break;
        }
    }

    /// <summary>
    /// Resolves an untagged PLAIN scalar under the core schema and, when the node reads
    /// differently from <paramref name="text"/>, records the text as the node's source spelling.
    /// </summary>
    private JsonNode? ResolvePlain(string text)
    {
        var node = YamlScalars.Resolve(text);
        if (node is not null && YamlScalars.Text(node) != text)
            spellings = spellings.SetItem(node, text);
        return node;
    }

    /// <summary>The node count (every null slot included — DeepClone allocates it) and collection depth of a tree, counting stops past <paramref name="cap"/>.</summary>
    private static (long Count, int Levels) Measure(JsonNode root, long cap)
    {
        var count = 0L;
        var levels = 0;
        var pending = ImmutableStack.Create((Node: root, Level: 0));
        while (!pending.IsEmpty && count <= cap)
        {
            pending = pending.Pop(out var item);
            count++;
            if (item.Node is not (JsonObject or JsonArray))
                continue;
            var level = item.Level + 1;
            if (level > levels) levels = level;
            var children = item.Node is JsonObject o ? o.Select(kv => kv.Value) : (JsonArray)item.Node;
            foreach (var child in children)
                if (child is not null) pending = pending.Push((child, level));
                else count++;
        }
        return (count, levels);
    }

    /// <summary>Reads every document in <paramref name="text"/>.</summary>
    internal static ImmutableList<JsonNode?> ParseStream(string text, bool strict) =>
        ParseStreamWithSpellings(text, strict).Documents;

    /// <summary>
    /// Reads every document in <paramref name="text"/>, and returns with them the source spelling
    /// of each plain scalar whose resolved node reads differently (keyed by node reference).
    /// </summary>
    internal static (ImmutableList<JsonNode?> Documents, ImmutableDictionary<JsonNode, string> Spellings) ParseStreamWithSpellings(string text, bool strict)
    {
        var parser = new YamlParser(text, strict);
        var documents = parser.ParseDocuments();
        return (documents, parser.spellings);
    }

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
            if (AtEnd) break;
            var sawDirective = ParseDirectives();
            if (AtEnd)
            {
                if (sawDirective && strict)
                    throw Error("a directive must be followed by a '---' document");
                break;
            }

            JsonNode? root;
            anchors = ImmutableDictionary<string, JsonNode?>.Empty;
            if (AtDocumentMarker('-'))
            {
                Advance(3);
                root = ParseBlockNode(-1, sequenceAtParentIndent: false);
            }
            else if (AtDocumentMarker('.'))
            {
                if (sawDirective && strict)
                    throw Error("a directive must be followed by a '---' document");
                EndDocumentMarker();
                continue;
            }
            else
            {
                if (sawDirective && strict)
                    throw Error("a directive must be followed by a '---' document");
                root = ParseNodeAtLineStart(Column, -1, null, sequenceAtParentIndent: false);
            }
            documents = documents.Add(root);

            SkipToNextContent();
            if (AtEnd) break;
            if (AtDocumentMarker('.'))
            {
                EndDocumentMarker();
                tagHandles = ImmutableDictionary<string, string>.Empty;
                continue;
            }
            if (AtDocumentMarker('-'))
            {
                tagHandles = ImmutableDictionary<string, string>.Empty;
                continue;
            }
            throw Error("unexpected content after the end of the document");
        }
        return documents;
    }

    /// <summary>A <c>...</c> marker: only a comment may follow it on its line.</summary>
    private void EndDocumentMarker()
    {
        Advance(3);
        SkipInlineSpaces();
        if (!(Cur is '\n' or '\0' or '#'))
            throw Error("unexpected content after the '...' document end marker");
        SkipToEndOfLine();
    }

    /// <summary>
    /// Reads the directives before a document. <c>%TAG</c> declares a tag handle for that document
    /// (a JSON tree has no use for the tags themselves, but a handle decides whether <c>!!int</c> is
    /// the core <c>int</c>); <c>%YAML</c> is checked in strict mode (one version, at most once).
    /// Unknown directives are reserved and skipped. Returns whether any directive was read.
    /// </summary>
    private bool ParseDirectives()
    {
        var saw = false;
        var yamlSeen = false;
        while (!AtEnd && Column == 0 && Cur == '%')
        {
            var mark = Save();
            Advance();
            var lineEnd = s.IndexOf('\n', pos);
            if (lineEnd < 0) lineEnd = s.Length;
            var content = s[pos..lineEnd];
            var hash = content.IndexOf(" #", StringComparison.Ordinal);
            var tabHash = content.IndexOf("\t#", StringComparison.Ordinal);
            var cut = hash < 0 ? tabHash : tabHash < 0 ? hash : Math.Min(hash, tabHash);
            if (cut >= 0) content = content[..cut];
            var words = content.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var name = words.Length > 0 ? words[0] : string.Empty;
            if (name == "YAML" && strict)
            {
                if (yamlSeen)
                    throw ErrorAt("a document may carry only one %YAML directive", mark);
                if (words.Length != 2 || !System.Text.RegularExpressions.Regex.IsMatch(words[1], @"^[0-9]+\.[0-9]+$"))
                    throw ErrorAt("%YAML takes exactly one version, such as 1.2", mark);
                yamlSeen = true;
            }
            else if (name == "TAG")
            {
                if (words.Length != 3)
                {
                    if (strict) throw ErrorAt("%TAG takes a handle and a prefix", mark);
                }
                else
                    tagHandles = tagHandles.SetItem(words[1], words[2]);
            }
            saw = true;
            SkipToEndOfLine();
            SkipToNextContent();
        }
        return saw;
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
            node = ParseNodeOnFollowingLines(parentIndent, sequenceAtParentIndent, props.Tag, props.Anchor is not null);
        else
            node = ParseInlineNode(parentIndent, props.Tag, props.HasAny);
        return Apply(node, props);
    }

    /// <summary>
    /// Reads a node that starts on a following line. <paramref name="inheritedTag"/> is a tag
    /// written before the line break (<c>key: !!str</c>): the scalar must not be resolved before it
    /// applies, or <c>007</c> would already be the number 7.
    /// </summary>
    private JsonNode? ParseNodeOnFollowingLines(int parentIndent, bool sequenceAtParentIndent, string? inheritedTag, bool inheritedAnchor)
    {
        SkipToNextContent();
        if (AtEnd || AtAnyDocumentMarker)
            return null;
        var col = Column;
        if (col > parentIndent)
            return ParseNodeAtLineStart(col, parentIndent, inheritedTag, sequenceAtParentIndent, inheritedAnchor);
        if (col == parentIndent && sequenceAtParentIndent && IsSequenceEntry)
            return ParseBlockSequence(col);
        // Nothing nested: the value is empty, and the cursor is where the caller sees the next
        // sibling (or the end of its own collection).
        return null;
    }

    /// <summary>Reads a node whose first character is the first content on its line, at column <paramref name="col"/>.</summary>
    private JsonNode? ParseNodeAtLineStart(int col, int parentIndent, string? inheritedTag, bool sequenceAtParentIndent, bool inheritedAnchor = false)
    {
        // A key line — its properties, alias or flow collection belong to the KEY.
        if (ScanKey(pos) != KeyKind.None)
            return ParseBlockMapping(col);
        if (IsSequenceEntry)
            return ParseBlockSequence(Column);
        var props = ParseProperties();
        SkipInlineSpaces();
        // Properties on the line before belong to this same node: it may not get a second anchor
        // or a second tag here (an anchor on one line and a tag on the next is fine).
        if (strict && ((props.Anchor is not null && inheritedAnchor) || (props.Tag is not null && inheritedTag is not null)))
            throw Error("a node can carry only one anchor and one tag");
        var tag = props.Tag ?? inheritedTag;
        JsonNode? node;
        if (props.HasAny && AtLineEndOrComment)
        {
            // Properties alone on a line put the node on the next one — another level of recursion.
            EnterCollection();
            try
            {
                node = ParseNodeOnFollowingLines(parentIndent, sequenceAtParentIndent, tag, inheritedAnchor || props.Anchor is not null);
            }
            finally
            {
                depth--;
            }
        }
        else if (props.HasAny && ScanKey(pos) != KeyKind.None)
            node = ParseBlockMapping(Column);
        else if (IsSequenceEntry)
        {
            if (strict)
                throw Error("a block sequence cannot start on the line of its properties");
            node = ParseBlockSequence(Column);
        }
        else
            node = ParseInlineNode(parentIndent, tag, props.HasAny);
        return Apply(node, props);
    }

    private enum KeyKind { None, Implicit, Explicit }

    /// <summary>
    /// Whether the line from <paramref name="i"/> opens a mapping entry: an explicit <c>? key</c>,
    /// or an implicit key — plain, quoted, an alias or a one-line flow collection, after any
    /// properties — followed by a <c>:</c> that is itself followed by a blank or the line end.
    /// It reads ahead only; nothing is consumed.
    /// </summary>
    private KeyKind ScanKey(int i)
    {
        // Properties: &anchor, !tag, !<verbatim>.
        while (i < s.Length && s[i] is '&' or '!')
        {
            if (s[i] == '!' && i + 1 < s.Length && s[i + 1] == '<')
            {
                var close = s.IndexOf('>', i);
                var eol = s.IndexOf('\n', i);
                if (close < 0 || (eol >= 0 && close > eol)) return KeyKind.None;
                i = close + 1;
            }
            else
                while (i < s.Length && !IsBlankOrEnd(s[i])) i++;
            while (i < s.Length && IsBlank(s[i])) i++;
        }
        if (i >= s.Length) return KeyKind.None;
        var c = s[i];
        if (c is '\n' or '#') return KeyKind.None;
        if (c == '?' && (i + 1 >= s.Length || IsBlankOrEnd(s[i + 1]))) return KeyKind.Explicit;
        if (c == '-' && (i + 1 >= s.Length || IsBlankOrEnd(s[i + 1]))) return KeyKind.None;
        if (c is '|' or '>') return KeyKind.None;
        int after;
        if (c is '"' or '\'')
            after = SkipQuotedOnLine(i);
        else if (c == '*')
        {
            after = i;
            while (after < s.Length && !IsBlankOrEnd(s[after])) after++;
        }
        else if (c is '[' or '{')
            after = SkipFlowOnLine(i);
        else
        {
            for (var j = i; j < s.Length && s[j] != '\n'; j++)
            {
                if (s[j] == ':' && (j + 1 >= s.Length || IsBlankOrEnd(s[j + 1])))
                    return KeyKind.Implicit;
                if (s[j] == '#' && j > i && IsBlank(s[j - 1]))
                    return KeyKind.None;
            }
            return KeyKind.None;
        }
        if (after < 0) return KeyKind.None;
        while (after < s.Length && IsBlank(s[after])) after++;
        return after < s.Length && s[after] == ':' && (after + 1 >= s.Length || IsBlankOrEnd(s[after + 1]))
            ? KeyKind.Implicit
            : KeyKind.None;
    }

    private bool LineIsMappingKey() => ScanKey(pos) != KeyKind.None;

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

    /// <summary>Index just past the flow collection starting at <paramref name="start"/>, or -1 when it does not close on that line.</summary>
    private int SkipFlowOnLine(int start)
    {
        var nesting = 0;
        var i = start;
        while (i < s.Length && s[i] != '\n')
        {
            var c = s[i];
            if (c is '"' or '\'')
            {
                var next = SkipQuotedOnLine(i);
                if (next < 0) return -1;
                i = next;
                continue;
            }
            if (c is '[' or '{') nesting++;
            else if (c is ']' or '}')
            {
                nesting--;
                if (nesting == 0) return i + 1;
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
            string key;
            bool isMergeKey;
            JsonNode? value;
            if (Cur == '?' && IsBlankOrEnd(PeekAt(1)))
            {
                // An explicit entry: '? key' and, on a line of its own at the same indentation, ': value'.
                Advance();
                key = KeyText(ParseCompactNode(indent, sequenceAtParentIndent: true));
                isMergeKey = false;
                SkipToNextContent();
                if (!AtEnd && !AtAnyDocumentMarker && Column == indent && Cur == ':' && IsBlankOrEnd(PeekAt(1)))
                {
                    Advance();
                    value = ParseCompactNode(indent, sequenceAtParentIndent: true);
                }
                else
                    value = null;
            }
            else
            {
                (key, isMergeKey) = ParseMappingKey();
                value = ParseBlockNode(indent, sequenceAtParentIndent: true);
            }
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

    /// <summary>The JSON key a key node becomes: a scalar's text, a collection's JSON text, "" for an empty key.</summary>
    private static string KeyText(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue => YamlScalars.Text(node) ?? string.Empty,
        _ => node.ToJsonString(),
    };

    /// <summary>
    /// Reads an implicit key and its <c>:</c>: properties first (an anchor names the KEY), then a
    /// quoted scalar, an alias, a one-line flow collection or a plain scalar. A plain key keeps its
    /// written text; only an untagged plain <c>&lt;&lt;</c> is a merge key.
    /// </summary>
    private (string Key, bool IsMergeKey) ParseMappingKey()
    {
        var props = ParseProperties();
        SkipInlineSpaces();
        JsonNode? keyNode;
        string key;
        var merge = false;
        switch (Cur)
        {
            case '"':
                key = ParseDoubleQuoted();
                keyNode = JsonValue.Create(key);
                break;
            case '\'':
                key = ParseSingleQuoted();
                keyNode = JsonValue.Create(key);
                break;
            case '*':
                {
                    if (props.HasAny && strict)
                        throw Error("an alias cannot carry properties");
                    var mark = Save();
                    Advance();
                    var name = ReadName();
                    if (!anchors.TryGetValue(name, out var target))
                        throw ErrorAt($"unknown alias '*{name}'", mark);
                    keyNode = Expand(target, mark);
                    key = KeyText(keyNode);
                    break;
                }
            case '[':
            case '{':
                keyNode = ParseFlowNode();
                key = KeyText(keyNode);
                break;
            case ':' when IsBlankOrEnd(PeekAt(1)):
                // An empty key (': value').
                keyNode = null;
                key = string.Empty;
                break;
            default:
                {
                    RefuseReservedPlainStart();
                    var start = pos;
                    while (!AtEnd && !(Cur == ':' && IsBlankOrEnd(PeekAt(1))))
                        Advance();
                    key = s[start..pos].TrimEnd(' ', '\t');
                    keyNode = DefersResolution(props.Tag) ? JsonValue.Create(key) : YamlScalars.Resolve(key);
                    merge = !props.HasAny && key == "<<";
                    break;
                }
        }
        Apply(keyNode, props);
        SkipInlineSpaces();
        if (Cur != ':')
            throw Error("expected ':' after a mapping key");
        Advance();
        return (key, merge);
    }

    private JsonObject ApplyMerges(JsonObject map, ImmutableList<JsonNode?> merges, Mark mark)
    {
        // Explicit keys win; among merged sources, the first that names a key wins.
        foreach (var merge in merges)
        {
            // Every source must be a mapping: a non-mapping element is refused, never filtered out.
            // An empty '<<:' merges nothing in the tolerant reader; strict mode refuses it.
            ImmutableList<JsonObject> sources = merge switch
            {
                null when !strict => [],
                JsonObject single => [single],
                JsonArray many when many.All(m => m is JsonObject) => many.Cast<JsonObject>().ToImmutableList(),
                _ => throw ErrorAt("a '<<' merge key needs a mapping or a sequence of mappings", mark),
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
            sequence.Add(ParseCompactNode(indent, sequenceAtParentIndent: false));

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

    /// <summary>
    /// The node after a <c>-</c>, <c>?</c> or explicit <c>:</c> indicator at <paramref name="indent"/>:
    /// inline it may be a COMPACT collection (<c>- key: v</c>, <c>- - a</c>), otherwise it starts on the
    /// following lines. A block sequence may sit at the indicator's own column after <c>?</c>/<c>:</c>.
    /// </summary>
    private JsonNode? ParseCompactNode(int indent, bool sequenceAtParentIndent)
    {
        SkipInlineSpaces();
        if (!AtLineEndOrComment)
        {
            if (ScanKey(pos) != KeyKind.None)
                return ParseBlockMapping(Column);
            if (IsSequenceEntry)
                return ParseBlockSequence(Column);
        }
        var props = ParseProperties();
        SkipInlineSpaces();
        JsonNode? node;
        if (AtLineEndOrComment)
            node = ParseNodeOnFollowingLines(indent, sequenceAtParentIndent, props.Tag, props.Anchor is not null);
        else if (IsSequenceEntry)
        {
            if (strict)
                throw Error("a block sequence cannot start on the line of its properties");
            node = ParseBlockSequence(Column);
        }
        else
            node = ParseInlineNode(indent, props.Tag, props.HasAny);
        return Apply(node, props);
    }

    // ───────────────────────────── node properties ─────────────────────────────

    /// <summary>A node's anchor and its RESOLVED tag (handles expanded: <c>!!int</c> → <c>tag:yaml.org,2002:int</c>).</summary>
    private readonly record struct Properties(string? Anchor, string? Tag, Mark TagMark)
    {
        public bool HasAny => Anchor is not null || Tag is not null;
    }

    private Properties ParseProperties()
    {
        string? anchor = null, tag = null;
        var tagMark = Save();
        while (true)
        {
            if (Cur == '&')
            {
                if (anchor is not null && strict)
                    throw Error("a node can carry only one anchor");
                Advance();
                anchor = ReadName();
                if (anchor.Length == 0) throw Error("an anchor needs a name");
            }
            else if (Cur == '!')
            {
                if (tag is not null && strict)
                    throw Error("a node can carry only one tag");
                tagMark = Save();
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
                tag = ResolveTag(s[start..pos], tagMark);
            }
            else
                break;
            SkipInlineSpaces();
        }
        return new Properties(anchor, tag, tagMark);
    }

    /// <summary>
    /// Expands a written tag with the document's handles: <c>!</c> alone is the non-specific tag,
    /// <c>!&lt;uri&gt;</c> is verbatim, <c>!!x</c> uses the secondary handle (the core schema
    /// unless a <c>%TAG !!</c> redefines it), <c>!h!x</c> a named handle a <c>%TAG</c> must declare
    /// (strict mode refuses an undeclared one), and <c>!x</c> the primary handle.
    /// </summary>
    private string ResolveTag(string written, Mark mark)
    {
        if (written == "!")
            return "!";
        if (written.StartsWith("!<", StringComparison.Ordinal))
            return written[2..^1];
        if (written.StartsWith("!!", StringComparison.Ordinal))
            return (tagHandles.TryGetValue("!!", out var secondary) ? secondary : CoreTagPrefix) + written[2..];
        var second = written.IndexOf('!', 1);
        if (second > 0)
        {
            var handle = written[..(second + 1)];
            if (tagHandles.TryGetValue(handle, out var prefix))
                return prefix + written[(second + 1)..];
            if (strict)
                throw ErrorAt($"tag handle '{handle}' is not declared by a %TAG directive in this document", mark);
            return written;
        }
        return (tagHandles.TryGetValue("!", out var primary) ? primary : "!") + written[1..];
    }

    private const string CoreTagPrefix = "tag:yaml.org,2002:";

    private string ReadName()
    {
        var start = pos;
        while (!IsBlankOrEnd(Cur) && !IsFlowIndicator(Cur)) Advance();
        return s[start..pos];
    }

    private JsonNode? Apply(JsonNode? node, Properties props)
    {
        if (props.Tag is not null)
            node = ApplyTag(node, props.Tag, props.TagMark);
        if (props.Anchor is not null)
            anchors = anchors.SetItem(props.Anchor, node);
        return node;
    }

    /// <summary>The core-schema type a RESOLVED tag names (<c>tag:yaml.org,2002:int</c> → <c>int</c>), or null for any other tag.</summary>
    private static string? CoreTagName(string tag) =>
        tag.StartsWith(CoreTagPrefix, StringComparison.Ordinal) ? tag[CoreTagPrefix.Length..] : null;

    /// <summary>
    /// Whether a plain scalar under <paramref name="tag"/> must stay UNRESOLVED until the tag
    /// applies: the non-specific <c>!</c> (a string by definition) and every core tag. An
    /// application tag (<c>!Ref</c>) carries nothing a JSON tree can use, so its scalar resolves
    /// exactly as the untagged one would.
    /// </summary>
    private static bool DefersResolution(string? tag) => tag is not null && (tag == "!" || CoreTagName(tag) is not null);

    /// <summary>
    /// Applies a core tag: the scalar is resolved AS the named type and refused, with the tag's
    /// position, when its text is not that type (<c>!!int true</c>) or when a scalar tag sits on a
    /// collection (and the reverse). Application tags and the other core types (binary, timestamp, …)
    /// leave the node as it is.
    /// </summary>
    private JsonNode? ApplyTag(JsonNode? node, string tag, Mark mark)
    {
        var name = CoreTagName(tag);
        if (name is null)
            return node;
        var isCollection = node is JsonObject or JsonArray;
        switch (name)
        {
            case "map":
                return node is JsonObject || node is null ? node ?? new JsonObject() : throw ErrorAt("!!map on a node that is not a mapping", mark);
            case "seq":
                return node is JsonArray || node is null ? node ?? new JsonArray() : throw ErrorAt("!!seq on a node that is not a sequence", mark);
            case "str" or "null" or "bool" or "int" or "float" when isCollection:
                throw ErrorAt($"!!{name} on a collection", mark);
        }
        var text = YamlScalars.Text(node);
        return name switch
        {
            "str" => JsonValue.Create(text ?? string.Empty),
            "null" => text is null || YamlScalars.Resolve(text) is null ? null : throw ErrorAt($"'{text}' is not a null", mark),
            "bool" => text is not null && YamlScalars.Resolve(text) is JsonValue b && b.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? b : throw ErrorAt($"'{text}' is not a boolean", mark),
            "int" => text is not null && YamlScalars.IsInteger(text)
                ? YamlScalars.Resolve(text) : throw ErrorAt($"'{text}' is not an integer", mark),
            "float" => text is not null && YamlScalars.IsFloat(text)
                ? YamlScalars.Resolve(text) : throw ErrorAt($"'{text}' is not a float", mark),
            _ => node,
        };
    }

    // ───────────────────────────── inline nodes ─────────────────────────────

    /// <summary>
    /// Reads a node that starts at the cursor and is NOT a block collection: an alias, a block
    /// scalar, a flow collection, a quoted scalar or a plain scalar. Continuation lines of a
    /// multi-line scalar must be indented deeper than <paramref name="parentIndent"/>.
    /// </summary>
    private JsonNode? ParseInlineNode(int parentIndent, string? tag, bool hasProperties)
    {
        switch (Cur)
        {
            case '*':
                {
                    if (hasProperties && strict)
                        throw Error("an alias cannot carry properties");
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
                    var node = WithContinuationIndent(parentIndent, ParseFlowNode);
                    ExpectLineEnd("a flow collection");
                    return node;
                }
            case '"':
                {
                    var text = WithContinuationIndent(parentIndent, ParseDoubleQuoted);
                    ExpectLineEnd("a quoted scalar");
                    return JsonValue.Create(text);
                }
            case '\'':
                {
                    var text = WithContinuationIndent(parentIndent, ParseSingleQuoted);
                    ExpectLineEnd("a quoted scalar");
                    return JsonValue.Create(text);
                }
            default:
                {
                    if (strict && IsSequenceEntry)
                        throw Error("a block sequence cannot start on the line of its key");
                    RefuseReservedPlainStart();
                    var text = ParsePlainBlock(parentIndent);
                    return DefersResolution(tag) ? JsonValue.Create(text) : ResolvePlain(text);
                }
        }
    }

    /// <summary>
    /// Runs a flow-collection or quoted-scalar reader with its continuation lines required (in strict
    /// mode) to be indented deeper than the enclosing block node, as the grammar requires.
    /// </summary>
    private T WithContinuationIndent<T>(int parentIndent, Func<T> read)
    {
        var saved = continuationMinColumn;
        continuationMinColumn = Math.Max(saved, parentIndent + 1);
        try
        {
            return read();
        }
        finally
        {
            continuationMinColumn = saved;
        }
    }

    /// <summary>The checks at the first content of a continuation line inside a flow collection or a quoted scalar.</summary>
    private void CheckContinuationLine()
    {
        if (Column == 0 && AtAnyDocumentMarker)
            throw Error("a document marker cannot appear inside a flow collection or a quoted scalar");
        if (strict && !AtEnd && Cur != '\n' && Column < continuationMinColumn)
            throw Error("a continuation line must be indented deeper than its block");
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
        if (Cur == '#' && strict && pos > 0 && !IsBlank(s[pos - 1]))
            throw Error("a comment must be separated from the content by a space");
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
        {
            if (strict && !IsBlank(s[pos - 1]))
                throw Error("a comment must be separated from the block scalar header by a space");
            SkipToEndOfLine();
        }
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
                // Indentation is spaces only; a tab after them is CONTENT, even alone on its line.
                // Only a line of spaces is empty.
                if (j < s.Length && s[j] != '\n')
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
                // A tab past the indentation is CONTENT even with nothing after it.
                if (extra.Length > 0 && lastContentEnd < 0 && !extra.Contains('\t'))
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
                    // An escaped line break joins without a space; each EMPTY line after it is
                    // still one line break.
                    Advance();
                    SkipInlineSpaces();
                    while (Cur == '\n')
                    {
                        sb.Append('\n');
                        Advance();
                        SkipInlineSpaces();
                    }
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
        CheckContinuationLine();
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
        var crossedLine = false;
        while (!AtEnd)
        {
            if (IsBlank(Cur))
            {
                Advance();
                continue;
            }
            if (Cur == '\n')
            {
                crossedLine = true;
                Advance();
                continue;
            }
            if (Cur == '#')
            {
                // A comment needs a blank before it; '#' straight after a token is refused in strict
                // mode and read as a comment by the tolerant reader.
                if (strict && pos > 0 && !IsBlankOrEnd(s[pos - 1]))
                    throw Error("a comment must be separated from the content by a space");
                SkipToEndOfLine();
                continue;
            }
            break;
        }
        if (crossedLine)
            CheckContinuationLine();
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
                    if (props.HasAny && strict)
                        throw Error("an alias cannot carry properties");
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
                    // A tagged or anchored EMPTY node ([!!str , z]) has no scalar to check.
                    if (!(props.HasAny && Cur is ',' or ']' or '}' or ':'))
                        RefuseReservedPlainStart();
                    // '-' starts a plain scalar only when a safe character follows ([-, -] is not YAML).
                    if (strict && Cur == '-' && (IsBlankOrEnd(PeekAt(1)) || IsFlowIndicator(PeekAt(1))))
                        throw Error("'-' alone is not a scalar in a flow collection (quote it)");
                    var text = ParsePlainFlow();
                    // An entry with nothing in it ([a,,b], {,}) is not YAML. An empty KEY before ':'
                    // ({: v}) and a tagged or anchored empty node are.
                    if (text.Length == 0 && !props.HasAny && Cur != ':')
                        throw Error("a flow collection entry is empty");
                    if (props.Tag is null)
                        plainText = text;
                    node = DefersResolution(props.Tag) ? JsonValue.Create(text) : ResolvePlain(text);
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

    private bool AtExplicitKeyIndicator => Cur == '?' && (IsBlankOrEnd(PeekAt(1)) || IsFlowIndicator(PeekAt(1)));

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
            var explicitKey = AtExplicitKeyIndicator;
            if (explicitKey)
            {
                Advance();
                SkipFlowWhitespace();
            }
            var keyLine = line;
            var jsonLike = Cur is '"' or '\'' or '[' or '{';
            JsonNode? item = null;
            string? itemText;
            if (explicitKey && Cur is ':' or ',' or ']')
                itemText = string.Empty; // '? ' with an empty key
            else
                item = ParseFlowNode(out itemText);
            SkipFlowWhitespace();
            var pair = Cur == ':' && (explicitKey || jsonLike || IsBlankOrEnd(PeekAt(1)) || IsFlowIndicator(PeekAt(1)));
            if (pair || explicitKey)
            {
                // A single-pair mapping inside a sequence: [a: 1, b: 2]. An implicit key must sit on
                // one line. The pair is a collection level of its own, so its value is read one level
                // deeper.
                if (pair && !explicitKey && strict && line != keyLine)
                    throw Error("an implicit key must be on a single line");
                JsonNode? value = null;
                if (pair)
                {
                    Advance();
                    SkipFlowWhitespace();
                    EnterCollection();
                    try
                    {
                        value = Cur is ',' or ']' ? null : ParseFlowNode();
                    }
                    finally
                    {
                        depth--;
                    }
                }
                item = new JsonObject { [itemText ?? KeyText(item)] = value };
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
            var explicitKey = AtExplicitKeyIndicator;
            if (explicitKey)
            {
                Advance();
                SkipFlowWhitespace();
            }
            var keyMark = Save();
            var plainKey = !(Cur is '"' or '\'' or '[' or '{');
            // A plain key keeps its written text, as in a block mapping: {null: a, '': b} has two keys.
            // An empty key is ': v', or '?' with nothing after it; an empty ENTRY ({,}) is refused.
            string key;
            if (Cur == ':' || (explicitKey && Cur is ',' or '}'))
                key = string.Empty;
            else
            {
                var keyNode = ParseFlowNode(out var keyText);
                key = keyText ?? KeyText(keyNode);
            }
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
                CheckContinuationLine();
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

    /// <summary>Whether <paramref name="text"/> is a core-schema integer (decimal, <c>0o</c> octal, <c>0x</c> hex).</summary>
    internal static bool IsInteger(string text) =>
        DecimalInt().IsMatch(text) || OctalInt().IsMatch(text) || HexInt().IsMatch(text);

    /// <summary>Whether <paramref name="text"/> is a core-schema float (an integer form included, and the <c>.inf</c>/<c>.nan</c> spellings).</summary>
    internal static bool IsFloat(string text) =>
        IsInteger(text) || Float().IsMatch(text)
        || text is ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF" or "-.inf" or "-.Inf" or "-.INF" or ".nan" or ".NaN" or ".NAN";

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
    /// A scalar node's text as the core schema RESOLVED it: a string's value, <c>true</c>/<c>false</c>,
    /// a number's digits (normalised — <c>007</c> reads <c>7</c>, <c>0x1F</c> reads <c>31</c>).
    /// <c>null</c> for a null node; a collection's JSON text otherwise.
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
