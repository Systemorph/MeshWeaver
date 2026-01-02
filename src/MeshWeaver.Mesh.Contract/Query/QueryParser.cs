namespace MeshWeaver.Mesh.Query;

/// <summary>
/// Parser for GitHub-style query strings.
/// Supports field:value syntax, comparison operators, and reserved qualifiers.
/// </summary>
public partial class QueryParser
{
    // Reserved qualifier names (case-insensitive)
    private static readonly HashSet<string> ReservedQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "scope", "sort", "limit", "source"
    };

    /// <summary>
    /// Parses a GitHub-style query string into a ParsedQuery.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <returns>A parsed query with AST and reserved parameters</returns>
    public ParsedQuery Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ParsedQuery.Empty;

        var tokens = Tokenize(query);
        var (filterTokens, textSearch, scope, orderBy, limit, source) = ExtractReservedQualifiers(tokens);

        // Parse the filter expression from remaining tokens
        QueryNode? filter = null;
        if (filterTokens.Count > 0)
        {
            var position = 0;
            filter = ParseOr(filterTokens, ref position);
        }

        return new ParsedQuery(filter, textSearch, scope, orderBy, limit, source);
    }

    /// <summary>
    /// Tokenizes the input query into a list of tokens.
    /// </summary>
    private List<Token> Tokenize(string input)
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
            var term = TryParseTerm(input, ref i);
            if (term != null)
            {
                tokens.Add(term);
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
            var (condition, length) = ParseFieldValue(field, input, i, isNegated);
            i += length;
            return new Token(TokenType.Comparison, field, condition);
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
    /// </summary>
    private (QueryCondition Condition, int Length) ParseFieldValue(string field, string input, int start, bool isNegated)
    {
        if (start >= input.Length)
            return (new QueryCondition(field, isNegated ? QueryOperator.NotEqual : QueryOperator.Equal, [""]), 0);

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
            var listValues = ParseListValues(input, ref i);
            op = isNegated ? QueryOperator.NotIn : QueryOperator.In;
            return (new QueryCondition(field, op, listValues), i - start);
        }
        else
        {
            // Simple value or wildcard
            op = isNegated ? QueryOperator.NotEqual : QueryOperator.Equal;
        }

        // Parse the value
        var value = ParseSingleValue(input, ref i);

        // Check for wildcard pattern
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

        return (new QueryCondition(field, op, [value]), i - start);
    }

    /// <summary>
    /// Parses values inside parentheses: (A OR B OR C)
    /// </summary>
    private string[] ParseListValues(string input, ref int i)
    {
        var values = new List<string>();

        while (i < input.Length && input[i] != ')')
        {
            // Skip whitespace
            while (i < input.Length && char.IsWhiteSpace(input[i]))
                i++;

            if (i >= input.Length || input[i] == ')')
                break;

            // Skip OR keyword
            if (i + 2 <= input.Length && input.Substring(i, 2).Equals("OR", StringComparison.OrdinalIgnoreCase))
            {
                i += 2;
                continue;
            }

            // Parse a value
            var value = ParseSingleValue(input, ref i);
            if (!string.IsNullOrEmpty(value))
                values.Add(value);
        }

        if (i < input.Length && input[i] == ')')
            i++; // consume )

        return values.ToArray();
    }

    /// <summary>
    /// Parses a single value, handling quotes.
    /// </summary>
    private string ParseSingleValue(string input, ref int i)
    {
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
            return value;
        }

        // Unquoted value - read until whitespace, ), or OR keyword
        var valueStart = i;
        while (i < input.Length)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c) || c == ')' || c == '(')
                break;

            // Check for OR keyword
            if (i + 2 <= input.Length && input.Substring(i, 2).Equals("OR", StringComparison.OrdinalIgnoreCase) &&
                (i + 2 >= input.Length || !char.IsLetterOrDigit(input[i + 2])))
                break;

            i++;
        }

        return input[valueStart..i];
    }

    /// <summary>
    /// Extracts reserved qualifiers (scope, sort, limit, source) from tokens.
    /// Returns remaining filter tokens and extracted values.
    /// </summary>
    private (List<Token> FilterTokens, string? TextSearch, QueryScope Scope, OrderByClause? OrderBy, int? Limit, QuerySource Source)
        ExtractReservedQualifiers(List<Token> tokens)
    {
        var filterTokens = new List<Token>();
        var textSearchParts = new List<string>();
        var scope = QueryScope.Exact;
        OrderByClause? orderBy = null;
        int? limit = null;
        var source = QuerySource.Default;

        foreach (var token in tokens)
        {
            if (token.Type == TokenType.TextSearch)
            {
                textSearchParts.Add(token.Value);
                continue;
            }

            if (token.Type == TokenType.Comparison && token.Condition != null)
            {
                var field = token.Condition.Selector;
                var value = token.Condition.Value;

                if (field.Equals("scope", StringComparison.OrdinalIgnoreCase))
                {
                    scope = value.ToLowerInvariant() switch
                    {
                        "descendants" => QueryScope.Descendants,
                        "ancestors" => QueryScope.Ancestors,
                        "hierarchy" => QueryScope.Hierarchy,
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
                    if (int.TryParse(value, out var limitValue))
                        limit = limitValue;
                    continue;
                }

                if (field.Equals("source", StringComparison.OrdinalIgnoreCase))
                {
                    source = value.ToLowerInvariant() switch
                    {
                        "activity" => QuerySource.Activity,
                        _ => QuerySource.Default
                    };
                    continue;
                }
            }

            filterTokens.Add(token);
        }

        var textSearch = textSearchParts.Count > 0 ? string.Join(" ", textSearchParts) : null;
        return (filterTokens, textSearch, scope, orderBy, limit, source);
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
        char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-' || c == '/';

    private enum TokenType
    {
        Comparison,
        TextSearch,
        Or,
        LeftParen,
        RightParen
    }

    private record Token(TokenType Type, string Value, QueryCondition? Condition = null);
}
