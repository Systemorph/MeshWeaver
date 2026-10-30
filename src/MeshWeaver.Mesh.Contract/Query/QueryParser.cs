namespace MeshWeaver.Mesh;

/// <summary>
/// Parser for GitHub-style query strings.
/// Supports field:value syntax, comparison operators, and reserved qualifiers.
/// </summary>
public partial class QueryParser
{
    // Reserved qualifier names (case-insensitive)
    // Frozen: it is exposed (ReservedQualifierNames), so it must not be mutable through a downcast.
    private static readonly System.Collections.Frozen.FrozenSet<string> ReservedQualifiers =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            "path", "namespace", "scope", "sort", "limit", "source", "select", "context", "is",
            "partitions", BasePathQualifier
        ], StringComparer.OrdinalIgnoreCase);

    /// <summary>The reserved (single-slot) qualifier names — what a query may state only once. Read by
    /// the catalog app's typed-text guard (<c>CatalogQuery.Anchoring</c>), which pins itself to this.</summary>
    public static IReadOnlySet<string> ReservedQualifierNames => ReservedQualifiers;

    /// <summary>
    /// <c>basePath:X</c> — the query-text spelling of the search tools' <c>basePath</c> argument:
    /// everything UNDER <c>X</c>, i.e. <c>namespace:X scope:descendants</c> unless the query states
    /// its own <c>scope:</c>. It is reserved because the tools advertise the word: before it was,
    /// an agent that wrote it into the query text got a FILTER on a <c>basePath</c> property no
    /// node has, and a clean <c>count: 0</c> for nodes that exist (#6828).
    /// </summary>
    public const string BasePathQualifier = "basePath";

    /// <summary>
    /// Parses a GitHub-style query string into a ParsedQuery.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <returns>A parsed query with AST and reserved parameters</returns>
    public ParsedQuery Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ParsedQuery.Empty;

        // Explicit ORDERED path list: `[a, b, c]`. The "alternatively, specify the exact nodes
        // in order" surface (a Deck's explicit slide manifest, vs. the default
        // `nodeType:Slide scope:subtree`). It populates the SAME Paths list as `path:a|b|c` — so
        // backends push it down as `WHERE path IN (...)` — but ORDER-PRESERVING, so an
        // order-sensitive consumer (deck slide sequence) can present in the listed order. Entries
        // are trimmed; blanks dropped. A bare bracket list is a WHOLE-query form (not composed
        // with other qualifiers).
        if (TryParseBracketPathList(query, out var listPaths))
            return ParsedQuery.Empty with { Paths = listPaths, Path = listPaths[0] };

        var tokens = Tokenize(query);
        var (filterTokens, textSearch, path, scope, orderBy, limit, source, select, context, isMain, paths, isContent, crossPartition) = ExtractReservedQualifiers(tokens);

        // Parse the filter expression from remaining tokens
        QueryNode? filter = null;
        if (filterTokens.Count > 0)
        {
            var position = 0;
            filter = ParseOr(filterTokens, ref position);
        }

        // `is:content` IS a context — the one every browsing surface means. Defaulting it here is
        // what keeps the existing exclusion machinery (per-node ExcludeFromContext, and per-type
        // when the marked node is a NodeType definition, pushed into SQL by the DB providers)
        // working unchanged, instead of these surfaces continuing to claim `context:search`.
        // An explicit context: always wins.
        context ??= isContent == true ? MeshContexts.Content : null;
        return new ParsedQuery(filter, textSearch, path, scope, orderBy, limit, source, select, context, isMain, paths, isContent)
        {
            CrossPartition = crossPartition,
        };
    }

    /// <summary>
    /// <paramref name="query"/> with every <c>{field}:…</c> term removed — found by THIS parser's own
    /// tokenizer, so a quoted value (<c>sort:"x-desc"</c>), a negated term and a value containing
    /// parentheses are cut exactly as they parse, and every other byte (whitespace inside a quoted
    /// literal included) is kept verbatim. One whitespace run next to each removed term goes with it.
    /// </summary>
    /// <param name="query">The query string.</param>
    /// <param name="field">The qualifier to remove (case-insensitive), e.g. <c>sort</c>.</param>
    /// <returns>The query without that qualifier.</returns>
    public string RemoveQualifier(string query, string field)
    {
        if (string.IsNullOrEmpty(query))
            return query;
        var spans = new List<(int Start, int End, Token Token)>();
        Tokenize(query, spans);
        var sb = new System.Text.StringBuilder(query.Length);
        var copied = 0;
        foreach (var (start, end, token) in spans)
        {
            if (token.Type != TokenType.Comparison
                || !string.Equals(token.Value, field, StringComparison.OrdinalIgnoreCase))
                continue;
            // The term plus the whitespace run BEFORE it (or after it, for a leading term).
            var cutStart = start;
            while (cutStart > copied && char.IsWhiteSpace(query[cutStart - 1]))
                cutStart--;
            var cutEnd = end;
            if (cutStart == start || cutStart == 0)
                while (cutEnd < query.Length && char.IsWhiteSpace(query[cutEnd]))
                    cutEnd++;
            sb.Append(query, copied, cutStart - copied);
            copied = cutEnd;
        }
        sb.Append(query, copied, query.Length - copied);
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="query"/> without the EXPRESSION groups that hold nothing — <c>()</c>, <c>( ( ) )</c>
    /// — as THIS parser's tokenizer reads it: a parenthesis inside a term (a list operand
    /// <c>status:(Open OR Won)</c>, a value <c>code:foo()</c>, a quoted literal) is part of that term and
    /// is never touched. An empty group stops <c>ParseAnd</c>, so a term after it would be dropped.
    /// </summary>
    /// <param name="query">The query string.</param>
    /// <returns>The query without empty expression groups.</returns>
    public string RemoveEmptyGroups(string query)
    {
        if (string.IsNullOrEmpty(query))
            return query;
        while (true)
        {
            var spans = new List<(int Start, int End, Token Token)>();
            Tokenize(query, spans);
            var inTerm = new bool[query.Length];
            foreach (var (start, end, _) in spans)
                for (var k = start; k < end && k < query.Length; k++)
                    inTerm[k] = true;

            var cut = -1;
            var cutEnd = -1;
            for (var i = 0; i < query.Length && cut < 0; i++)
            {
                if (inTerm[i] || query[i] != '(')
                    continue;
                var j = i + 1;
                while (j < query.Length && !inTerm[j] && char.IsWhiteSpace(query[j]))
                    j++;
                if (j < query.Length && !inTerm[j] && query[j] == ')')
                    (cut, cutEnd) = (i, j + 1);
            }
            if (cut < 0)
                return query;
            query = query[..cut] + query[cutEnd..];
        }
    }

    /// <summary>
    /// Every FILTER comparison written in <paramref name="query"/> — each <c>field:value</c> term that is
    /// not a reserved qualifier, at any depth, as THIS parser's tokenizer reads it. What a caller
    /// compares a parse against to prove no written term was dropped on the way.
    /// </summary>
    /// <param name="query">The query string.</param>
    /// <returns>The written filter conditions, in order.</returns>
    public IReadOnlyList<QueryCondition> WrittenComparisons(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : Tokenize(query)
                .Where(t => t.Type == TokenType.Comparison && t.Condition is not null
                            && !ReservedQualifiers.Contains(t.Value))
                .Select(t => t.Condition!)
                .ToList();

    /// <summary>
    /// <paramref name="value"/> as a LITERAL query value: quoted, so a space, <c>|</c>, <c>*</c> or
    /// <c>(</c> inside it is data, never syntax (#6634). Double quotes unless the value contains one,
    /// then single quotes.
    /// </summary>
    /// <param name="value">The literal value, e.g. a node path.</param>
    /// <returns>The quoted value.</returns>
    /// <exception cref="ArgumentException">The value contains both quote characters — the query
    /// language has no escape, so it cannot be written as one literal.</exception>
    public static string QuoteLiteral(string value)
    {
        if (!value.Contains('"'))
            return $"\"{value}\"";
        if (!value.Contains('\''))
            return $"'{value}'";
        throw new ArgumentException(
            $"'{value}' contains both quote characters and cannot be written as a query literal.", nameof(value));
    }

    /// <summary>
    /// A node <paramref name="path"/> as ONE query value: the quoted literal (<see cref="QuoteLiteral"/>)
    /// whenever one can hold it. A path carrying BOTH quote characters has no quoted literal (the query
    /// language has no escape), and is then written bare, as it always was: a bare value runs to the next
    /// whitespace and reads a quote inside it as data, so such a path without whitespace still parses
    /// whole. One that also carries whitespace (or starts with a quote) cannot be written at all, and
    /// that is refused by name rather than read as a different query.
    /// </summary>
    /// <param name="path">The node path.</param>
    /// <returns>The value to splice after <c>path:</c> or <c>namespace:</c>.</returns>
    /// <exception cref="ArgumentException">The path has both quote characters and whitespace, or starts with a quote.</exception>
    public static string PathLiteral(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.Contains('"') || !path.Contains('\''))
            return QuoteLiteral(path);
        if (path.Length > 0 && path[0] is not ('"' or '\'') && !path.Any(char.IsWhiteSpace))
            return path;
        throw new ArgumentException(
            $"'{path}' contains both quote characters and whitespace (or starts with a quote), so it cannot be written as one query value.",
            nameof(path));
    }

    /// <summary>
    /// Recognizes a whole-query explicit path list <c>[a, b, c]</c> and returns its ordered,
    /// trimmed, non-blank entries. Returns <c>false</c> (and null) when the query is not a
    /// bracket list or contains no entries.
    /// </summary>
    private static bool TryParseBracketPathList(string query, out IReadOnlyList<string> paths)
    {
        paths = System.Array.Empty<string>();
        var trimmed = query.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[^1] != ']')
            return false;
        var inner = trimmed[1..^1];
        var entries = inner
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
        if (entries.Length == 0)
            return false;
        paths = entries;
        return true;
    }

    /// <summary>
    /// Tokenizes the input query into a list of tokens.
    /// </summary>
    private List<Token> Tokenize(string input) => Tokenize(input, spans: null);

    /// <summary>
    /// <see cref="Tokenize(string)"/>, also recording each TERM's source span
    /// (<c>[Start, End)</c> in <paramref name="input"/>) when <paramref name="spans"/> is given — what
    /// <see cref="RemoveQualifier"/> cuts by, so a qualifier is removed exactly where the parser
    /// read it and every other byte stays as written.
    /// </summary>
    private List<Token> Tokenize(string input, List<(int Start, int End, Token Token)>? spans)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < input.Length)
        {
            // Skip whitespace (but note it for implicit AND)
            if (char.IsWhiteSpace(input[i]))
            {
                i++;
                continue;
            }

            // Parentheses for grouping
            if (input[i] == '(')
            {
                tokens.Add(new Token(TokenType.LeftParen, "("));
                i++;
                continue;
            }

            if (input[i] == ')')
            {
                tokens.Add(new Token(TokenType.RightParen, ")"));
                i++;
                continue;
            }

            // Check for OR keyword
            if (i + 2 <= input.Length)
            {
                var two = input.Substring(i, 2);
                if (two.Equals("OR", StringComparison.OrdinalIgnoreCase) &&
                    (i + 2 >= input.Length || !char.IsLetterOrDigit(input[i + 2])))
                {
                    tokens.Add(new Token(TokenType.Or, "OR"));
                    i += 2;
                    continue;
                }
            }

            // Check for AND keyword (explicit, though space is also AND)
            if (i + 3 <= input.Length)
            {
                var three = input.Substring(i, 3);
                if (three.Equals("AND", StringComparison.OrdinalIgnoreCase) &&
                    (i + 3 >= input.Length || !char.IsLetterOrDigit(input[i + 3])))
                {
                    // Explicit AND - just skip it, space already implies AND
                    i += 3;
                    continue;
                }
            }

            // Try to parse a term (field:value or bare text)
            var termStart = i;
            var term = TryParseTerm(input, ref i);
            if (term != null)
            {
                tokens.Add(term);
                spans?.Add((termStart, i, term));
            }
            else
            {
                // Unknown character, skip
                i++;
            }
        }

        return tokens;
    }

    /// <summary>
    /// Tries to parse a term at the current position.
    /// A term can be: field:value, -field:value, or bare text.
    /// </summary>
    private Token? TryParseTerm(string input, ref int i)
    {
        var start = i;
        var isNegated = false;

        // Check for negation prefix
        if (input[i] == '-')
        {
            isNegated = true;
            i++;
            if (i >= input.Length)
            {
                i = start;
                return null;
            }
        }

        // Check for quoted string (bare text search)
        if (input[i] == '"' || input[i] == '\'')
        {
            var quoteChar = input[i];
            i++;
            var textStart = i;
            while (i < input.Length && input[i] != quoteChar)
                i++;
            var text = input[textStart..i];
            if (i < input.Length)
                i++; // consume closing quote
            return new Token(TokenType.TextSearch, text);
        }

        // Parse field name
        var fieldStart = i;
        while (i < input.Length && IsValidFieldChar(input[i]))
            i++;

        if (i == fieldStart)
        {
            i = start;
            return null;
        }

        var field = input[fieldStart..i];

        // Check if this is a field:value or bare text
        if (i < input.Length && input[i] == ':')
        {
            i++; // consume :
            var (condition, length, literal) = ParseFieldValue(field, input, i, isNegated);
            i += length;
            return new Token(TokenType.Comparison, field, condition, literal);
        }
        else
        {
            // Bare text - treat as text search
            // But if it was negated, it's invalid - reset
            if (isNegated)
            {
                i = start;
                return null;
            }
            return new Token(TokenType.TextSearch, field);
        }
    }

    /// <summary>
    /// Parses the value part after field:.
    /// Handles comparison operators, lists, and wildcard patterns.
    /// <para>🚨 A QUOTED value is LITERAL (#6634): no <c>|</c> alternation and no <c>*</c> wildcard
    /// inside the quotes, so <c>namespace:"a|b"</c> and <c>namespace:"x*"</c> name exactly that
    /// namespace. Alternation BETWEEN quoted values (<c>path:"a b"|"c"</c>) is unchanged — the bar
    /// sits outside the quotes. The third element says the condition carries one such literal, so
    /// the reserved-qualifier pass does not re-read a <c>*</c> in it as a glob.</para>
    /// </summary>
    private (QueryCondition Condition, int Length, bool Literal) ParseFieldValue(string field, string input, int start, bool isNegated)
    {
        // Empty value: end of string or whitespace immediately after colon
        if (start >= input.Length || char.IsWhiteSpace(input[start]))
            return (new QueryCondition(field, isNegated ? QueryOperator.NotEqual : QueryOperator.Equal, [""]), 0, false);

        var i = start;
        QueryOperator op;
        var values = new List<string>();

        // Check for comparison operators (>=, <=, >, <)
        if (i + 2 <= input.Length && input.Substring(i, 2) == ">=")
        {
            op = QueryOperator.GreaterOrEqual;
            i += 2;
        }
        else if (i + 2 <= input.Length && input.Substring(i, 2) == "<=")
        {
            op = QueryOperator.LessOrEqual;
            i += 2;
        }
        else if (input[i] == '>')
        {
            op = QueryOperator.GreaterThan;
            i++;
        }
        else if (input[i] == '<')
        {
            op = QueryOperator.LessThan;
            i++;
        }
        else if (input[i] == '(')
        {
            // List: (A OR B OR C)
            i++; // consume (
            var listValues = ParseListValues(input, ref i, out var allQuoted);
            op = isNegated ? QueryOperator.NotIn : QueryOperator.In;
            // A ONE-value list whose value was quoted (`namespace:("x*")`) is the same literal as
            // `namespace:"x*"` (#6752): carry the flag, or the reserved-qualifier pass re-reads its
            // `*` as a glob — and turns `-namespace:("x*")` into a POSITIVE Like. A multi-value list
            // is exact membership already and needs no flag.
            return (new QueryCondition(field, op, listValues), i - start, listValues.Length == 1 && allQuoted);
        }
        else
        {
            // Simple value or wildcard
            op = isNegated ? QueryOperator.NotEqual : QueryOperator.Equal;
        }

        // Parse the value. At the top level we allow `(` and `)` inside the value
        // so SQL-function calls in sort selectors work — e.g. `sort:length(path)-desc`.
        // The `(A OR B OR C)` list form is detected above (immediately-after-colon),
        // so we never confuse a list with an embedded paren here.
        var value = ParseSingleValue(input, ref i, stopAtParens: false, out var quoted);

        // Quoted `|` alternation — `field:"A B"|"C D"|E`. ParseSingleValue stops a
        // QUOTED value at its closing quote, so a trailing `|` here is the alternation
        // separator (the unquoted form keeps `|` inside the value and is split by the
        // block below). This is what lets path segments containing SPACES take part in
        // the prefix-set query the path resolver builds (`path:"a/b c"|"a"`).
        if ((op == QueryOperator.Equal || op == QueryOperator.NotEqual)
            && i < input.Length && input[i] == '|')
        {
            var altParts = new List<string>();
            if (value.Length > 0) altParts.Add(value);
            while (i < input.Length && input[i] == '|')
            {
                i++; // consume '|'
                var next = ParseSingleValue(input, ref i, stopAtParens: false);
                if (next.Length > 0) altParts.Add(next);
            }
            if (altParts.Count > 1)
            {
                op = isNegated ? QueryOperator.NotIn : QueryOperator.In;
                return (new QueryCondition(field, op, altParts.ToArray()), i - start, false);
            }
        }

        // 🚨 A quoted value is LITERAL (#6634) — skip the grep-style split and the wildcard switch
        // below. Equal/In/NotIn compare literally on every executor; NotEqual does NOT when the
        // value carries a `*` (it reads as the complement of the glob, #6581), so a negated literal
        // with a star travels as a one-value NotIn — the same null-inclusive literal exclusion.
        if (quoted && (op == QueryOperator.Equal || op == QueryOperator.NotEqual))
        {
            if (op == QueryOperator.NotEqual && QueryWildcard.ContainsWildcard(value))
                return (new QueryCondition(field, QueryOperator.NotIn, [value]), i - start, true);
            return (new QueryCondition(field, op, [value]), i - start, true);
        }

        // Grep-style `|` alternation — `field:A|B|C` is equivalent to
        // `field:(A OR B OR C)` but more concise, modelled on `grep -E`'s
        // alternation operator. Pushed down to `IN(...)` by query backends.
        // Only triggers for plain Equal/NotEqual (not for >, <, >=, <=, Like).
        if ((op == QueryOperator.Equal || op == QueryOperator.NotEqual)
            && !value.Contains('*')
            && value.Contains('|'))
        {
            var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                op = isNegated ? QueryOperator.NotIn : QueryOperator.In;
                return (new QueryCondition(field, op, parts), i - start, false);
            }
        }

        // Check for wildcard pattern.
        // 🚨 A NEGATED wildcard (`-field:val*`) stays NotEqual and KEEPS its `*` (#6581): every
        // executor reads a `*` in a NotEqual value as the COMPLEMENT of the glob
        // (QueryWildcard.IsNotLikeMatch; `NOT ILIKE … OR IS NULL` on SQL). There is no NotLike
        // member — QueryOperator is public and widening it breaks every switch that consumes it
        // (policy open-vocabulary-string-constants). Before #6581 the executors compared the
        // pattern LITERALLY, so the negation matched every node, positives included.
        if (!isNegated && (op == QueryOperator.Equal || op == QueryOperator.NotEqual))
        {
            if (value.Contains('*'))
            {
                op = QueryOperator.Like;
            }
        }

        // Apply negation for comparison operators
        if (isNegated && op != QueryOperator.NotEqual && op != QueryOperator.NotIn && op != QueryOperator.Like)
        {
            // For negated comparisons like -price:>100, invert the operator
            op = op switch
            {
                QueryOperator.GreaterThan => QueryOperator.LessOrEqual,
                QueryOperator.LessThan => QueryOperator.GreaterOrEqual,
                QueryOperator.GreaterOrEqual => QueryOperator.LessThan,
                QueryOperator.LessOrEqual => QueryOperator.GreaterThan,
                _ => op
            };
        }

        // A quoted ORDERED operand (`namespace:>"x*"`) is literal too (#6634): the reserved-qualifier
        // pass must not re-read its `*` as a glob and drop the operator.
        return (new QueryCondition(field, op, [value]), i - start, quoted);
    }

    /// <summary>
    /// Parses values inside parentheses: (A OR B OR C)
    /// </summary>
    private string[] ParseListValues(string input, ref int i, out bool allQuoted)
    {
        var values = new List<string>();
        allQuoted = true;

        while (i < input.Length && input[i] != ')')
        {
            // Skip whitespace
            while (i < input.Length && char.IsWhiteSpace(input[i]))
                i++;

            if (i >= input.Length || input[i] == ')')
                break;

            // Skip OR keyword — only a WHOLE word (#6844). Matching the two letters alone ate the
            // head of every value that merely starts with them: `-namespace:(acme/a OR orders)`
            // excluded `acme/a` and `ders`, so the second namespace was never excluded, on every
            // executor (the mangled value is what the parser hands them all).
            if (IsListOrKeyword(input, i))
            {
                i += 2;
                continue;
            }

            // Parse a value
            var value = ParseSingleValue(input, ref i, stopAtParens: true, out var quoted);
            if (!string.IsNullOrEmpty(value))
            {
                values.Add(value);
                allQuoted &= quoted;
            }
        }

        if (i < input.Length && input[i] == ')')
            i++; // consume )

        return values.ToArray();
    }

    /// <summary>
    /// Whether the list separator <c>OR</c> (any case) starts at <paramref name="i"/> as a whole word:
    /// followed by the end of input, whitespace, a parenthesis or a quote. <c>orders</c>,
    /// <c>Oracle/x</c> and <c>or-x</c> are values, never a separator plus a remainder.
    /// </summary>
    private static bool IsListOrKeyword(string input, int i)
    {
        if (i + 2 > input.Length || !input.AsSpan(i, 2).Equals("OR", StringComparison.OrdinalIgnoreCase))
            return false;
        if (i + 2 == input.Length)
            return true;
        var next = input[i + 2];
        return char.IsWhiteSpace(next) || next is '(' or ')' or '"' or '\'';
    }

    /// <summary>
    /// Parses a single value, handling quotes.
    /// <para>
    /// <paramref name="stopAtParens"/> = true (default) — used inside list
    /// expressions <c>field:(A OR B OR C)</c> where each value must end at the
    /// closing <c>)</c>. <paramref name="stopAtParens"/> = false — used at the
    /// top level so SQL-function syntax like <c>length(path)</c> stays inside
    /// the value. The list form is already disambiguated by the
    /// immediately-after-colon <c>(</c> check in <c>ParseFieldValue</c>.
    /// </para>
    /// </summary>
    private string ParseSingleValue(string input, ref int i, bool stopAtParens = true)
        => ParseSingleValue(input, ref i, stopAtParens, out _);

    /// <summary>
    /// <see cref="ParseSingleValue(string, ref int, bool)"/>, also reporting whether the value was
    /// QUOTED — a quoted value is literal (#6634).
    /// </summary>
    private string ParseSingleValue(string input, ref int i, bool stopAtParens, out bool quoted)
    {
        quoted = false;
        // Skip whitespace
        while (i < input.Length && char.IsWhiteSpace(input[i]))
            i++;

        if (i >= input.Length)
            return "";

        // Check for quoted value
        if (input[i] == '"' || input[i] == '\'')
        {
            var quoteChar = input[i];
            i++;
            var start = i;
            while (i < input.Length && input[i] != quoteChar)
                i++;
            var value = input[start..i];
            if (i < input.Length)
                i++; // consume closing quote
            quoted = true;
            return value;
        }

        // Unquoted value - read until whitespace.
        // OR keywords are handled by ParseListValues, not here —
        // detecting OR mid-value causes false positives (e.g. "Operator" → "Operat" + OR).
        // Paren handling: when stopAtParens=true (inside list `field:(A OR B)`),
        // stop at any unescaped paren. When false (top-level value), we still need
        // to stop at the structural CLOSING `)` of an enclosing group like
        // `(a:1 b:2)` — but NOT at the closing `)` of a function call inside the
        // value like `length(path)`. Track depth: balanced internal parens stay
        // in the value; an unbalanced `)` exits.
        var valueStart = i;
        var depth = 0;
        while (i < input.Length)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c))
                break;
            if (stopAtParens && (c == ')' || c == '('))
                break;
            if (!stopAtParens)
            {
                if (c == '(')
                    depth++;
                else if (c == ')')
                {
                    if (depth == 0) break; // structural close of an enclosing group
                    depth--;
                }
            }

            i++;
        }

        return input[valueStart..i];
    }

    /// <summary>
    /// Extracts reserved qualifiers (path, namespace, scope, sort, limit, source) from tokens.
    /// Returns remaining filter tokens and extracted values.
    /// </summary>
    private (List<Token> FilterTokens, string? TextSearch, string? Path, QueryScope Scope, OrderByClause? OrderBy, int? Limit, QuerySource Source, IReadOnlyList<string>? Select, string? Context, bool? IsMain, IReadOnlyList<string>? Paths, bool? IsContent, bool CrossPartition)
        ExtractReservedQualifiers(List<Token> tokens)
    {
        var filterTokens = new List<Token>();
        var textSearchParts = new List<string>();
        string? path = null;
        IReadOnlyList<string>? paths = null;
        var scope = QueryScope.Exact;
        bool explicitScope = false;
        bool namespaceUsed = false;
        // Multi-value `namespace:A|B|C` alternation. Deferred to post-loop processing
        // (the scope: qualifier may appear after the namespace: token) where it becomes a
        // `namespace IN (...)` membership FILTER — expanded to each value's self+ancestor
        // namespaces when scope:selfAndAncestors. This is what lets a SINGLE query union
        // built-in + per-context + per-NodeType + per-user instances (see AgentPickerProjection).
        string[]? namespaceAlternation = null;
        // Where each wildcard `namespace:*/X` LIKE filter landed in filterTokens, so the post-loop
        // pass can widen it to the SUBTREE once `scope:` is known (the qualifier may appear AFTER
        // the namespace token). See WidenWildcardNamespacesToSubtree.
        List<int>? namespaceWildcardIndices = null;
        OrderByClause? orderBy = null;
        int? limit = null;
        var source = QuerySource.Default;
        IReadOnlyList<string>? select = null;
        string? context = null;
        bool? isMain = null;
        bool? isContent = null;
        var crossPartition = false;
        // A positive `basePath:X` anchored the query: with no explicit scope it walks the
        // DESCENDANTS of X, the same as the tools' basePath argument (#6828).
        var basePathAnchored = false;

        foreach (var original in tokens)
        {
            // Location values are resolved BEFORE anything reads them: the `@` reference prefix
            // comes off, and `basePath:` becomes the namespace anchor it means (#6828).
            var token = ResolveLocationToken(original);
            if (token.Type == TokenType.TextSearch)
            {
                textSearchParts.Add(token.Value);
                continue;
            }

            // Every POSITIVE single-location form: an equality, a one-value list `basePath:(X)`, and
            // a wildcard `basePath:acme/*/Contacts` (a LIKE). A wildcard sets no concrete anchor,
            // and the descendant scope still applies — it widens the pattern to the subtree, as
            // `namespace:… scope:descendants` does. A multi-value list stays exact membership.
            if (original.Condition is { } basePathCondition
                && basePathCondition.Selector.Equals(BasePathQualifier, StringComparison.OrdinalIgnoreCase)
                && (basePathCondition.Operator is QueryOperator.Equal or QueryOperator.Like
                    || (basePathCondition.Operator is QueryOperator.In && basePathCondition.Values.Length == 1)))
                basePathAnchored = true;

            if (token.Type == TokenType.Comparison && token.Condition != null)
            {
                var field = token.Condition.Selector;
                var value = token.Condition.Value;

                if (field.Equals("path", StringComparison.OrdinalIgnoreCase))
                {
                    // 🚨 AN ORDERED COMPARISON ON path IS A FILTER, NOT AN ANCHOR.
                    // `path:>"m"` is the keyset half of the standard paging pair
                    // (`sort:path` + `path:>"{cursor}"`). Folding it into the path ANCHOR
                    // discarded the operator entirely, so the query silently became
                    // `path:m` — an exact/scoped anchor at a path that does not exist — and
                    // answered ZERO ROWS with no error, which reads as "the walk is done"
                    // (issue #2186: a cross-partition walk built on it returned page one and
                    // reported success). It also re-routed a mesh-wide query onto a single
                    // partition. Left as a filter token it becomes `n.path > 'm'` against the
                    // real, indexed `path` column and the walk pages properly.
                    // 🚨 AND SO IS A NEGATED ONE (#4296) — for the same reason, one step worse.
                    // `-path:A` / `-path:A|B` says "everywhere EXCEPT A", which is the one
                    // statement that can never be served by pinning to A. The single form fell
                    // through to `path = value` and the alternation set `Paths = Values` beside
                    // `Path = Values[0]`: the operator was dropped and the exclusion became a
                    // positive ANCHOR. `IsSufficientlySpecified()` then counted it as anchored,
                    // `ResolvePinnedPartition` pinned the query to A's schema, and the per-schema
                    // route applied `path IN (A, B)` — so the query returned rows from exactly the
                    // paths it meant to exclude, or nothing, with no error. Left as a filter token
                    // it becomes `n.path <> 'A'` / `n.path NOT IN (...)` against the real, indexed
                    // `path` column, and the query stays UNANCHORED unless something else anchors
                    // it — which is the truth about what it asked for.
                    if (token.Condition.Operator is QueryOperator.GreaterThan
                        or QueryOperator.GreaterOrEqual
                        or QueryOperator.LessThan
                        or QueryOperator.LessOrEqual
                        or QueryOperator.NotEqual
                        or QueryOperator.NotIn)
                    {
                        filterTokens.Add(token);
                        continue;
                    }

                    // Multi-value `path:a|b|c` — `In` carries the alternation list. Backends use
                    // the list to push down `WHERE path IN (...)`. Single-value form sets only
                    // Path. `NotIn` never arrives here: it is a filter, carved out above.
                    if (token.Condition.Operator is QueryOperator.In
                        && token.Condition.Values.Length > 1)
                    {
                        paths = token.Condition.Values;
                        path = token.Condition.Values[0];
                    }
                    else
                    {
                        path = value;
                    }
                    continue;
                }

                if (field.Equals("namespace", StringComparison.OrdinalIgnoreCase))
                {
                    // A QUOTED namespace is literal (#6634): it is the namespace, never a set of
                    // namespaces and never a pattern — even when it carries `|` or `*`.
                    if (token.Literal)
                    {
                        // Only a positive equality anchors the query; every other operator — a
                        // negation or an ordered comparison (`namespace:>"x*"`) — stays a FILTER
                        // with its operator, so nothing is dropped.
                        // A one-value quoted list (`namespace:("x")`, #6752) is the same equality.
                        if (token.Condition!.Operator is QueryOperator.Equal
                            || (token.Condition.Operator is QueryOperator.In && token.Condition.Values.Length == 1))
                        {
                            path = value;
                            namespaceUsed = true;
                        }
                        else
                            filterTokens.Add(new Token(TokenType.Comparison, string.Empty,
                                new QueryCondition("namespace", token.Condition.Operator, [value.Trim('/')]), true));
                        continue;
                    }
                    // Alternation `namespace:A|B|C` (parsed to In/NotIn with >1 values) is an EXACT
                    // MEMBERSHIP constraint across the listed namespaces, not a single base namespace
                    // + scope walk. Deferred to post-loop resolution (a `namespace IN (...)` filter).
                    //
                    // 🚨 A NEGATED alternation `-namespace:A|B` is an EXCLUSION FILTER, never the
                    // namespace constraint (#6828, review of #6837): deferring it with the positive
                    // form cleared the query's anchor and reset its scope in the post-loop pass, so
                    // `namespace:X -namespace:X/A|X/B` lost `X` — refused as unanchored, or, with
                    // `partitions:all`, answered from outside `X`. It is emitted here as the
                    // `namespace NotIn (...)` filter the deferred pass would have produced, and
                    // leaves the anchor and scope alone, like the single `-namespace:X` below.
                    if (token.Condition!.Operator is QueryOperator.NotIn && token.Condition.Values.Length > 1)
                    {
                        filterTokens.Add(new Token(TokenType.Comparison, string.Empty,
                            new QueryCondition("namespace", QueryOperator.NotIn,
                                token.Condition.Values.Select(v => v.Trim('/'))
                                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray())));
                        continue;
                    }
                    if (token.Condition.Operator is QueryOperator.In && token.Condition.Values.Length > 1)
                    {
                        namespaceAlternation = token.Condition.Values;
                        namespaceUsed = true;
                        continue;
                    }
                    // 🚨 A NEGATED single namespace is a FILTER, never an anchor (#6581) — the same
                    // defect #4296 fixed for `-path:`. `-namespace:X` fell through to `path = X`
                    // and `-namespace:*/X` to a POSITIVE `namespace LIKE` filter: the operator was
                    // dropped and the exclusion returned exactly what it was written to exclude.
                    // Left as `namespace NotEqual value` it is evaluated by every backend, a `*` in
                    // the value reading as the complement of the glob (see QueryWildcard).
                    // A one-value NEGATED list `-namespace:(X)` (#6752) is the same exclusion as
                    // `-namespace:X`: it used to skip this arm (it is a NotIn, not a NotEqual) and fall
                    // through to `path = X`, a positive anchor.
                    if (token.Condition!.Operator is QueryOperator.NotEqual
                        || (token.Condition.Operator is QueryOperator.NotIn && token.Condition.Values.Length == 1))
                    {
                        // A negated WILDCARD is widened with its positive twin once `scope:` is
                        // known — to the AND of the two negated patterns, the complement of the
                        // positive's self-or-below OR pair (WidenWildcardNamespacesToSubtree).
                        if (QueryWildcard.ContainsWildcard(value))
                            (namespaceWildcardIndices ??= []).Add(filterTokens.Count);
                        filterTokens.Add(new Token(TokenType.Comparison, string.Empty,
                            new QueryCondition("namespace", QueryOperator.NotEqual, [value.Trim('/')])));
                        continue;
                    }
                    if (QueryWildcard.ContainsWildcard(value))
                    {
                        // Wildcard namespace: add as LIKE filter (e.g., namespace:*/_Thread).
                        // REMEMBER where it landed — `scope:` may still be ahead of us in the token
                        // stream, and a subtree scope widens this single LIKE into a self-or-below
                        // OR pair (WidenWildcardNamespacesToSubtree).
                        //
                        // 🚨 The pattern keeps the user's `*` (issue #1235). It used to be rewritten
                        // to SQL's `%` right here — the ONLY Like filter in the language that did —
                        // which forked the vocabulary: the SQL generators translate `*`→`%` anyway
                        // when they bind the parameter, so the rewrite bought nothing, while every
                        // in-memory evaluator (which speaks `*`) silently matched NOTHING against
                        // the `%` form. See QueryWildcard for the full vocabulary contract.
                        (namespaceWildcardIndices ??= []).Add(filterTokens.Count);
                        filterTokens.Add(new Token(TokenType.Comparison, string.Empty,
                            new QueryCondition("namespace", QueryOperator.Like, [value])));
                    }
                    else
                    {
                        path = value;
                        namespaceUsed = true;
                    }
                    continue;
                }

                if (field.Equals("scope", StringComparison.OrdinalIgnoreCase))
                {
                    explicitScope = true;
                    scope = value.ToLowerInvariant() switch
                    {
                        "exact" => QueryScope.Exact,
                        "children" => QueryScope.Children,
                        "descendants" => QueryScope.Descendants,
                        "ancestors" => QueryScope.Ancestors,
                        "hierarchy" => QueryScope.Hierarchy,
                        "subtree" => QueryScope.Subtree,
                        "ancestorsandself" => QueryScope.AncestorsAndSelf,
                        "selfandancestors" => QueryScope.AncestorsAndSelf,
                        "myselfandancestors" => QueryScope.AncestorsAndSelf, // legacy alias
                        "selfanddescendants" => QueryScope.Subtree,           // alias for symmetry with selfAndAncestors
                        "nextlevel" => QueryScope.NextLevel,
                        "populated" => QueryScope.NextLevel,                   // reads as "next populated level"
                        _ => QueryScope.Exact
                    };
                    continue;
                }

                if (field.Equals("sort", StringComparison.OrdinalIgnoreCase))
                {
                    // Format: sort:field-desc or sort:field-asc or sort:field
                    var dashIdx = value.LastIndexOf('-');
                    if (dashIdx > 0)
                    {
                        var prop = value[..dashIdx];
                        var dir = value[(dashIdx + 1)..];
                        orderBy = new OrderByClause(prop, dir.Equals("desc", StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        orderBy = new OrderByClause(value, false);
                    }
                    continue;
                }

                if (field.Equals("limit", StringComparison.OrdinalIgnoreCase))
                {
                    // `limit:all` is the query-string spelling of MeshQueryRequest.Complete() —
                    // this read is an ENUMERATION, so every match must come back. It exists for the
                    // readers that never see a MeshQueryRequest (the synced-query cache builds its
                    // own), where the absence of a limit is silently the cross-schema fan-out's
                    // 50-row page.
                    if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
                        limit = Services.MeshQueryRequest.NoLimit;
                    else if (int.TryParse(value, out var limitValue))
                        limit = limitValue;
                    continue;
                }

                if (field.Equals("source", StringComparison.OrdinalIgnoreCase))
                {
                    source = value.ToLowerInvariant() switch
                    {
                        "activity" => QuerySource.Activity,
                        "accessed" => QuerySource.Accessed,
                        _ => QuerySource.Default
                    };
                    continue;
                }

                if (field.Equals("select", StringComparison.OrdinalIgnoreCase))
                {
                    select = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    continue;
                }

                if (field.Equals("context", StringComparison.OrdinalIgnoreCase))
                {
                    context = value;
                    continue;
                }

                // 🚨 `partitions:all` — the EXPLICIT cross-partition request, and the only thing
                // that makes an otherwise unanchored query legal. It is a FLAG rather than a path
                // value on purpose: `path:*` reads like the same statement and is a trap, because
                // partition resolvers take a path's first segment as a schema NAME — `path:*` pins
                // to a partition literally called `*`, the statement dies on
                // `relation "*.threads" does not exist`, and the query returns EMPTY instead of
                // failing. A flag cannot be mistaken for a partition.
                if (field.Equals("partitions", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
                        crossPartition = true;
                    continue;
                }

                if (field.Equals("is", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Equals("main", StringComparison.OrdinalIgnoreCase))
                        isMain = true;
                    // is:content — "I am listing content for a person, so do not hand me
                    // registration nodes". The surface states its own need on its own query.
                    else if (value.Equals("content", StringComparison.OrdinalIgnoreCase))
                        isContent = true;
                    continue;
                }
            }

            filterTokens.Add(token);
        }

        // When namespace: is used without explicit scope, default to Children (exact namespace)
        // This makes namespace:X behave like "search items directly in folder X" (not recursive)
        // Use scope:descendants explicitly for recursive search
        // `basePath:X` means the whole subtree under X, as the tools' basePath argument does — for a
        // wildcard too, which sets no concrete anchor (namespaceUsed stays false) but is widened to
        // the subtree below once the scope is known.
        if (basePathAnchored && !explicitScope)
        {
            scope = QueryScope.Descendants;
        }
        else if (namespaceUsed && !explicitScope)
        {
            scope = QueryScope.Children;
        }

        // `namespace:X` names a NAMESPACE, never the node AT path X — that node's own namespace is
        // its PARENT path (a user root `rbuergi` has namespace ""), so it can never satisfy a
        // namespace:X constraint. Subtree (= self + descendants) on the namespace-derived path
        // would leak the self node into the results (the user node showed up in its own
        // `source:activity namespace:{user} scope:subtree` activity feed), so for namespace-derived
        // paths subtree degrades to descendants. `path:X scope:subtree` keeps self, as before.
        if (namespaceUsed && scope == QueryScope.Subtree)
        {
            scope = QueryScope.Descendants;
        }

        // A WILDCARD namespace carries its own scope, because it never produced a Path to walk.
        if (namespaceWildcardIndices is not null
            && scope is QueryScope.Subtree or QueryScope.Descendants)
        {
            WidenWildcardNamespacesToSubtree(filterTokens, namespaceWildcardIndices);
        }

        // Resolve a deferred `namespace:A|B|C` alternation into a `namespace IN (...)` membership
        // FILTER. With scope:selfAndAncestors each value expands to itself + every ancestor
        // namespace, so a node whose namespace is the value OR any ancestor matches (closest-wins
        // is the caller's concern, as with AccessAssignment). Without that scope it's exact
        // membership across the listed namespaces. Emitting a plain filter means every backend
        // (Postgres `LOWER(n.namespace) IN`, in-memory QueryEvaluator, the static-node provider)
        // evaluates it identically — no scope-clause surgery — and the path/scope is cleared so no
        // path-based walk runs on top. Single `namespace:X` is left untouched (path + scope walk).
        if (namespaceAlternation is { Length: > 1 })
        {
            // `namespace:A|B|C` is EXACT membership across the listed namespaces — a single
            // `namespace IN (A, B, C)` filter, no ancestor/descendant graph walk. This is what the
            // agent / model registry uses to list the platform + space + user namespaces directly
            // (e.g. `namespace:{user}/Agent|{space}/Agent|Agent`). The alternation IS the namespace
            // constraint, so the single-namespace path/scope is dropped — no walk on top.
            var distinct = namespaceAlternation.Select(v => v.Trim('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            filterTokens.Add(new Token(TokenType.Comparison, string.Empty,
                new QueryCondition("namespace",
                    QueryOperator.In, distinct)));
            path = null;
            scope = QueryScope.Exact;
        }

        var textSearch = textSearchParts.Count > 0 ? string.Join(" ", textSearchParts) : null;
        return (filterTokens, textSearch, path, scope, orderBy, limit, source, select, context, isMain, paths, isContent, crossPartition);
    }

    /// <summary>
    /// Resolves a location token (<c>path:</c>, <c>namespace:</c>, <c>basePath:</c>) to the form
    /// every executor compares against stored paths. Returns <paramref name="token"/> itself when
    /// there is nothing to resolve.
    ///
    /// <para><b>The <c>@</c> reference prefix comes off</b> (#6828). <c>@X</c> / <c>@/X</c> is how a
    /// path is written in markdown links, autocomplete and every tool argument, and the tools strip
    /// it there (<c>MeshOperations.ResolvePath</c>). Written INTO the query text it was kept, so
    /// <c>namespace:@acme name:"Jane Doe"</c> anchored at a partition literally named <c>@acme</c>
    /// — no rows, no error, and a <c>count: 0</c> that read as "this person does not exist". No
    /// mesh path segment begins with <c>@</c> (an address's partition may CONTAIN one, never lead
    /// with one), so the strip cannot change what a well-formed query meant.</para>
    ///
    /// <para><b><c>basePath:</c> becomes <c>namespace:</c></b> with the same operator and values;
    /// <see cref="ExtractReservedQualifiers"/> widens a positive one to the descendants.</para>
    /// </summary>
    private static Token ResolveLocationToken(Token token)
    {
        if (token.Type != TokenType.Comparison || token.Condition is not { } condition)
            return token;
        var isBasePath = condition.Selector.Equals(BasePathQualifier, StringComparison.OrdinalIgnoreCase);
        if (!isBasePath
            && !condition.Selector.Equals("path", StringComparison.OrdinalIgnoreCase)
            && !condition.Selector.Equals("namespace", StringComparison.OrdinalIgnoreCase))
            return token;
        if (!isBasePath && !condition.Values.Any(v => v.StartsWith('@')))
            return token;
        return token with
        {
            Condition = condition with
            {
                Selector = isBasePath ? "namespace" : condition.Selector,
                Values = condition.Values.Select(StripReferencePrefix).ToArray(),
            },
        };
    }

    /// <summary>
    /// Strips the <c>@</c> reference prefix from one location value: <c>@acme/x</c> and
    /// <c>@/acme/x</c> are both <c>acme/x</c>. A value without the prefix is returned unchanged.
    /// Shared with the permission fold (<c>PermissionEvaluator</c>), which takes the same spelling
    /// at its entry so a reference-form path is never read as a partition named <c>@</c> (#7090).
    /// </summary>
    internal static string StripReferencePrefix(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '@')
            return value;
        return value[1..].TrimStart('/');
    }

    /// <summary>
    /// Widens every wildcard <c>namespace:*/X</c> LIKE filter to cover the whole SUBTREE:
    /// <c>(namespace LIKE '*/X' OR namespace LIKE '*/X/*')</c> — in the AST's own <c>*</c>
    /// vocabulary; each backend spells those as its dialect requires (<c>%</c> on SQL).
    ///
    /// <para><b>Why this exists (issue #1216).</b> A concrete <c>namespace:A/B</c> becomes a
    /// <see cref="ParsedQuery.Path"/> plus a <see cref="QueryScope"/>, and every backend walks that
    /// path — so <c>scope:subtree</c> genuinely reaches nodes nested any number of levels below.
    /// A WILDCARD namespace cannot become a Path (there is no path to walk: <c>*</c> spans
    /// partitions), so it is emitted as a <c>namespace LIKE</c> FILTER instead — and a filter is all
    /// the backend ever sees. <c>scope:subtree</c> was therefore silently DROPPED for the wildcard
    /// form: <c>namespace:*/Source scope:subtree</c> matched only rows whose namespace ENDS in
    /// <c>/Source</c>, i.e. the Code nodes sitting DIRECTLY in some <c>…/Source</c> folder, and
    /// nothing one level deeper. No error, no warning — just a strictly narrower answer than the
    /// query asked for.</para>
    ///
    /// <para>That is what stalled the memex-cloud rollout on 2026-08-11. The batch bake's global
    /// source fetch (<c>NodeTypeBatchBake.GlobalCodeQueries</c>) is exactly this shape, and it is the
    /// ONLY way to reach the per-schema <c>code</c> satellite table across partitions. Four NodeTypes
    /// whose sources include a cross-partition <c>shared=@Claims/SampleData/Source/Fixtures</c> —
    /// one level below <c>/Source</c> — had that source silently missing from the global fetch, so
    /// the in-memory matcher selected a PARTIAL source set and Roslyn produced a perfectly
    /// convincing <c>CS0103: The name 'MtplClaimFixtures' does not exist</c>. The bake gate read the
    /// false CompileError as an image regression and refused readiness. A partial set is worse than
    /// an empty one: the emptiness invariant added in #1220 cannot see it, because those types have
    /// plenty of OTHER sources.</para>
    ///
    /// <para>Widening in the PARSER (rather than in any one caller's query text) is what makes the
    /// fix hold for every backend at once — Postgres, Snowflake, Cosmos and the in-memory
    /// evaluator all consume the same <see cref="ParsedQuery.Filter"/> — and it is what stops the
    /// next author of a <c>namespace:*/X scope:subtree</c> query from inheriting the same silent
    /// truncation.</para>
    /// </summary>
    /// <param name="filterTokens">The filter token stream being assembled; edited in place.</param>
    /// <param name="indices">Positions of the single-LIKE wildcard namespace tokens to widen.</param>
    private static void WidenWildcardNamespacesToSubtree(List<Token> filterTokens, List<int> indices)
    {
        // Descending, so an earlier index is still valid after a later one has been expanded.
        foreach (var index in indices.OrderByDescending(i => i))
        {
            if (filterTokens[index].Condition is not { } self || self.Value.Length == 0)
                continue;
            var pattern = self.Value;
            var negated = self.Operator == QueryOperator.NotEqual;
            // Same vocabulary as the pattern being widened: `*`, not SQL's `%` (issue #1235). The
            // widened form carries TWO wildcards (`*/Source/*`), which is exactly the shape both
            // in-memory matchers used to mishandle by splitting on the first one only.
            var below = new QueryCondition(
                self.Selector, self.Operator,
                [$"{pattern.TrimEnd('/')}/{QueryWildcard.Wildcard}"]);
            filterTokens.RemoveAt(index);
            // A NEGATED wildcard (`-namespace:*/X`, #6581) is the complement of the positive's
            // `(self OR below)` — De Morgan: `NOT self AND NOT below`, two adjacent comparisons.
            if (negated)
            {
                filterTokens.InsertRange(index,
                [
                    new Token(TokenType.Comparison, string.Empty, self),
                    new Token(TokenType.Comparison, string.Empty, below),
                ]);
                continue;
            }
            filterTokens.InsertRange(index,
            [
                new Token(TokenType.LeftParen, "("),
                new Token(TokenType.Comparison, string.Empty, self),
                new Token(TokenType.Or, "OR"),
                new Token(TokenType.Comparison, string.Empty, below),
                new Token(TokenType.RightParen, ")"),
            ]);
        }
    }

    /// <summary>
    /// Parses OR expressions (lowest precedence).
    /// </summary>
    private QueryNode? ParseOr(List<Token> tokens, ref int position)
    {
        var left = ParseAnd(tokens, ref position);
        if (left == null)
            return null;

        var orChildren = new List<QueryNode> { left };

        while (position < tokens.Count && tokens[position].Type == TokenType.Or)
        {
            position++; // consume OR
            var right = ParseAnd(tokens, ref position);
            if (right != null)
                orChildren.Add(right);
        }

        return orChildren.Count == 1 ? orChildren[0] : new QueryOr(orChildren);
    }

    /// <summary>
    /// Parses AND expressions (implicit via space, higher precedence than OR).
    /// </summary>
    private QueryNode? ParseAnd(List<Token> tokens, ref int position)
    {
        var left = ParsePrimary(tokens, ref position);
        if (left == null)
            return null;

        var andChildren = new List<QueryNode> { left };

        // Implicit AND: consecutive comparisons without OR between them
        while (position < tokens.Count &&
               tokens[position].Type != TokenType.Or &&
               tokens[position].Type != TokenType.RightParen)
        {
            var right = ParsePrimary(tokens, ref position);
            if (right != null)
                andChildren.Add(right);
            else
                break;
        }

        return andChildren.Count == 1 ? andChildren[0] : new QueryAnd(andChildren);
    }

    /// <summary>
    /// Parses primary expressions (comparisons and groups).
    /// </summary>
    private QueryNode? ParsePrimary(List<Token> tokens, ref int position)
    {
        if (position >= tokens.Count)
            return null;

        var token = tokens[position];

        if (token.Type == TokenType.LeftParen)
        {
            position++; // consume (
            var inner = ParseOr(tokens, ref position);
            if (position < tokens.Count && tokens[position].Type == TokenType.RightParen)
                position++; // consume )
            return inner;
        }

        if (token.Type == TokenType.Comparison && token.Condition != null)
        {
            position++;
            return new QueryComparison(token.Condition);
        }

        return null;
    }

    private static bool IsValidFieldChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-' || c == '/' || c == '$';

    private enum TokenType
    {
        Comparison,
        TextSearch,
        Or,
        LeftParen,
        RightParen
    }

    /// <param name="Literal">The condition's value was QUOTED and is literal (#6634) — no wildcard, no alternation.</param>
    private record Token(TokenType Type, string Value, QueryCondition? Condition = null, bool Literal = false);
}
