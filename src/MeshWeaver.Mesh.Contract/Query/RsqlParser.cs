using System.Text.RegularExpressions;

namespace MeshWeaver.Mesh.Query;

/// <summary>
/// Parser for RSQL/FIQL query strings.
/// Supports the full RSQL specification plus extensions for $search and $scope.
/// </summary>
public partial class RsqlParser
{
    // Operator patterns in order of precedence (longest first)
    private static readonly (string Pattern, RsqlOperator Op)[] Operators =
    [
        ("=ge=", RsqlOperator.GreaterOrEqual),
        ("=le=", RsqlOperator.LessOrEqual),
        ("=gt=", RsqlOperator.GreaterThan),
        ("=lt=", RsqlOperator.LessThan),
        ("=in=", RsqlOperator.In),
        ("=out=", RsqlOperator.NotIn),
        ("=like=", RsqlOperator.Like),
        ("!=", RsqlOperator.NotEqual),
        ("==", RsqlOperator.Equal),
    ];

    /// <summary>
    /// Parses an RSQL query string into a ParsedQuery.
    /// </summary>
    /// <param name="query">The RSQL query string</param>
    /// <returns>A parsed query with AST and reserved parameters</returns>
    public ParsedQuery Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ParsedQuery.Empty;

        // Extract reserved parameters first
        var (rsqlPart, textSearch, scope) = ExtractReservedParams(query);

        // Parse the RSQL filter expression
        RsqlNode? filter = null;
        if (!string.IsNullOrWhiteSpace(rsqlPart))
        {
            var tokens = Tokenize(rsqlPart);
            var position = 0;
            filter = ParseOr(tokens, ref position);
        }

        return new ParsedQuery(filter, textSearch, scope);
    }

    /// <summary>
    /// Extracts $search and $scope parameters from the query, returning the remaining RSQL.
    /// </summary>
    private (string RsqlPart, string? TextSearch, QueryScope Scope) ExtractReservedParams(string query)
    {
        string? textSearch = null;
        var scope = QueryScope.Exact;
        var parts = new List<string>();

        // Split by semicolon to find reserved params
        foreach (var part in SplitTopLevel(query, ';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("$search=", StringComparison.OrdinalIgnoreCase))
            {
                textSearch = Uri.UnescapeDataString(trimmed[8..]);
            }
            else if (trimmed.StartsWith("$scope=", StringComparison.OrdinalIgnoreCase))
            {
                var scopeValue = trimmed[7..].ToLowerInvariant();
                scope = scopeValue switch
                {
                    "descendants" => QueryScope.Descendants,
                    "ancestors" => QueryScope.Ancestors,
                    "hierarchy" => QueryScope.Hierarchy,
                    _ => QueryScope.Exact
                };
            }
            else if (!string.IsNullOrWhiteSpace(trimmed))
            {
                parts.Add(trimmed);
            }
        }

        return (string.Join(";", parts), textSearch, scope);
    }

    /// <summary>
    /// Splits a string by a delimiter, but only at the top level (not inside parentheses or quotes).
    /// </summary>
    private static List<string> SplitTopLevel(string input, char delimiter)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        var inQuotes = false;
        var quoteChar = '\0';

        foreach (var c in input)
        {
            if (inQuotes)
            {
                current.Append(c);
                if (c == quoteChar)
                    inQuotes = false;
            }
            else if (c == '"' || c == '\'')
            {
                inQuotes = true;
                quoteChar = c;
                current.Append(c);
            }
            else if (c == '(')
            {
                depth++;
                current.Append(c);
            }
            else if (c == ')')
            {
                depth--;
                current.Append(c);
            }
            else if (c == delimiter && depth == 0)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    /// <summary>
    /// Tokenizes the RSQL input into a list of tokens.
    /// </summary>
    private List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < input.Length)
        {
            var c = input[i];

            // Skip whitespace
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // Parentheses
            if (c == '(')
            {
                tokens.Add(new Token(TokenType.LeftParen, "("));
                i++;
                continue;
            }

            if (c == ')')
            {
                tokens.Add(new Token(TokenType.RightParen, ")"));
                i++;
                continue;
            }

            // Logical operators
            if (c == ';')
            {
                tokens.Add(new Token(TokenType.And, ";"));
                i++;
                continue;
            }

            if (c == ',')
            {
                tokens.Add(new Token(TokenType.Or, ","));
                i++;
                continue;
            }

            // Check for keyword operators (and, or)
            if (i + 3 <= input.Length)
            {
                var three = input.Substring(i, 3).ToLowerInvariant();
                if (three == "and" && (i + 3 >= input.Length || !char.IsLetterOrDigit(input[i + 3])))
                {
                    tokens.Add(new Token(TokenType.And, "and"));
                    i += 3;
                    continue;
                }
            }

            if (i + 2 <= input.Length)
            {
                var two = input.Substring(i, 2).ToLowerInvariant();
                if (two == "or" && (i + 2 >= input.Length || !char.IsLetterOrDigit(input[i + 2])))
                {
                    tokens.Add(new Token(TokenType.Or, "or"));
                    i += 2;
                    continue;
                }
            }

            // Try to match a comparison (selector operator value)
            var comparisonMatch = TryParseComparison(input, i);
            if (comparisonMatch != null)
            {
                tokens.Add(new Token(TokenType.Comparison, comparisonMatch.Value.Text, comparisonMatch.Value.Condition));
                i += comparisonMatch.Value.Length;
                continue;
            }

            // Unknown character, skip
            i++;
        }

        return tokens;
    }

    /// <summary>
    /// Tries to parse a comparison at the given position.
    /// </summary>
    private (string Text, int Length, RsqlCondition Condition)? TryParseComparison(string input, int start)
    {
        // Parse selector (property name, can contain dots for nested properties)
        var selectorEnd = start;
        while (selectorEnd < input.Length && IsValidSelectorChar(input[selectorEnd]))
        {
            selectorEnd++;
        }

        if (selectorEnd == start)
            return null;

        var selector = input[start..selectorEnd];

        // Find the operator
        RsqlOperator? op = null;
        var opLength = 0;

        foreach (var (pattern, rsqlOp) in Operators)
        {
            if (selectorEnd + pattern.Length <= input.Length &&
                input.Substring(selectorEnd, pattern.Length).Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                op = rsqlOp;
                opLength = pattern.Length;
                break;
            }
        }

        if (op == null)
            return null;

        var valueStart = selectorEnd + opLength;

        // Parse value(s)
        var (values, valueLength) = ParseValues(input, valueStart, op.Value);

        var totalLength = selectorEnd - start + opLength + valueLength;
        var text = input.Substring(start, totalLength);

        return (text, totalLength, new RsqlCondition(selector, op.Value, values));
    }

    /// <summary>
    /// Parses values for a comparison. Handles lists for =in= and =out=.
    /// </summary>
    private (string[] Values, int Length) ParseValues(string input, int start, RsqlOperator op)
    {
        if (start >= input.Length)
            return ([], 0);

        // Check for list syntax (value1,value2) for =in= and =out=
        if (input[start] == '(' && (op == RsqlOperator.In || op == RsqlOperator.NotIn))
        {
            var end = input.IndexOf(')', start);
            if (end == -1)
                end = input.Length;

            var listContent = input[(start + 1)..end];
            var values = listContent.Split(',')
                .Select(v => UnquoteValue(v.Trim()))
                .ToArray();

            return (values, end - start + 1);
        }

        // Single value - parse until we hit a logical operator or end
        var valueEnd = start;
        var inQuotes = false;
        var quoteChar = '\0';

        while (valueEnd < input.Length)
        {
            var c = input[valueEnd];

            if (inQuotes)
            {
                if (c == quoteChar)
                    inQuotes = false;
                valueEnd++;
            }
            else if (c == '"' || c == '\'')
            {
                inQuotes = true;
                quoteChar = c;
                valueEnd++;
            }
            else if (c == ';' || c == ',' || c == ')' || char.IsWhiteSpace(c))
            {
                // Check if this might be 'and' or 'or' keyword
                if (char.IsWhiteSpace(c))
                {
                    var remaining = input[valueEnd..].TrimStart();
                    if (remaining.StartsWith("and", StringComparison.OrdinalIgnoreCase) ||
                        remaining.StartsWith("or", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    valueEnd++;
                }
                else
                {
                    break;
                }
            }
            else
            {
                valueEnd++;
            }
        }

        var value = UnquoteValue(input[start..valueEnd].Trim());
        return ([value], valueEnd - start);
    }

    /// <summary>
    /// Removes surrounding quotes from a value.
    /// </summary>
    private static string UnquoteValue(string value)
    {
        if (value.Length >= 2)
        {
            if ((value.StartsWith('"') && value.EndsWith('"')) ||
                (value.StartsWith('\'') && value.EndsWith('\'')))
            {
                return value[1..^1];
            }
        }
        return value;
    }

    private static bool IsValidSelectorChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-';

    /// <summary>
    /// Parses OR expressions (lowest precedence).
    /// </summary>
    private RsqlNode? ParseOr(List<Token> tokens, ref int position)
    {
        var left = ParseAnd(tokens, ref position);
        if (left == null)
            return null;

        var orChildren = new List<RsqlNode> { left };

        while (position < tokens.Count && tokens[position].Type == TokenType.Or)
        {
            position++; // consume OR
            var right = ParseAnd(tokens, ref position);
            if (right != null)
                orChildren.Add(right);
        }

        return orChildren.Count == 1 ? orChildren[0] : new RsqlOr(orChildren);
    }

    /// <summary>
    /// Parses AND expressions (higher precedence than OR).
    /// </summary>
    private RsqlNode? ParseAnd(List<Token> tokens, ref int position)
    {
        var left = ParsePrimary(tokens, ref position);
        if (left == null)
            return null;

        var andChildren = new List<RsqlNode> { left };

        while (position < tokens.Count && tokens[position].Type == TokenType.And)
        {
            position++; // consume AND
            var right = ParsePrimary(tokens, ref position);
            if (right != null)
                andChildren.Add(right);
        }

        return andChildren.Count == 1 ? andChildren[0] : new RsqlAnd(andChildren);
    }

    /// <summary>
    /// Parses primary expressions (comparisons and groups).
    /// </summary>
    private RsqlNode? ParsePrimary(List<Token> tokens, ref int position)
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

        if (token.Type == TokenType.Comparison)
        {
            position++;
            return new RsqlComparison(token.Condition!);
        }

        return null;
    }

    private enum TokenType
    {
        Comparison,
        And,
        Or,
        LeftParen,
        RightParen
    }

    private record Token(TokenType Type, string Value, RsqlCondition? Condition = null);
}
