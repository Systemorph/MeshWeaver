#nullable enable

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Provides fzf-style fuzzy matching and scoring for autocomplete items.
/// </summary>
public class FuzzyScorer
{
    // Scoring constants (inspired by fzf)
    private const int ScoreMatch = 16;
    private const int ScoreGapStart = -3;
    private const int ScoreGapExtension = -1;
    private const int BonusBoundary = 8;
    private const int BonusFirstCharMatch = 10;
    private const int BonusConsecutive = 4;
    private const int BonusCamelCase = 6;
    private const int BonusAfterSeparator = 7;

    /// <summary>
    /// Represents a scored item with match positions for highlighting.
    /// </summary>
    public record ScoredItem<T>(T Item, int Score, int[] MatchPositions);

    /// <summary>
    /// Scores and filters items based on a fuzzy query.
    /// </summary>
    /// <typeparam name="T">The type of items to score.</typeparam>
    /// <param name="items">The items to score.</param>
    /// <param name="query">The search query.</param>
    /// <param name="textSelector">Function to extract searchable text from an item.</param>
    /// <returns>Matching items sorted by score (highest first).</returns>
    public IEnumerable<ScoredItem<T>> Score<T>(
        IEnumerable<T> items,
        string query,
        Func<T, string> textSelector)
    {
        if (string.IsNullOrEmpty(query))
        {
            // No query = return all items with score 0
            return items.Select(item => new ScoredItem<T>(item, 0, []));
        }

        var results = new List<ScoredItem<T>>();
        var queryLower = query.ToLowerInvariant();

        foreach (var item in items)
        {
            var text = textSelector(item);
            if (string.IsNullOrEmpty(text))
                continue;

            var (score, positions) = CalculateScore(text, query, queryLower);
            if (score > int.MinValue)
            {
                results.Add(new ScoredItem<T>(item, score, positions));
            }
        }

        return results.OrderByDescending(r => r.Score);
    }

    /// <summary>
    /// Calculates the fuzzy match score between text and query.
    /// Returns (int.MinValue, []) if no match.
    /// </summary>
    private (int Score, int[] Positions) CalculateScore(string text, string query, string queryLower)
    {
        var textLower = text.ToLowerInvariant();
        var positions = new List<int>();
        var score = 0;
        var queryIndex = 0;
        var lastMatchIndex = -1;
        var consecutiveCount = 0;

        for (var textIndex = 0; textIndex < text.Length && queryIndex < query.Length; textIndex++)
        {
            if (textLower[textIndex] == queryLower[queryIndex])
            {
                positions.Add(textIndex);

                // Base match score
                score += ScoreMatch;

                // Bonus for exact case match
                if (text[textIndex] == query[queryIndex])
                {
                    score += 1;
                }

                // Bonus for first character match
                if (textIndex == 0)
                {
                    score += BonusFirstCharMatch;
                }

                // Bonus for match at word boundary
                if (textIndex > 0)
                {
                    var prevChar = text[textIndex - 1];
                    if (IsWordBoundary(prevChar))
                    {
                        score += BonusAfterSeparator;
                    }
                    else if (char.IsLower(prevChar) && char.IsUpper(text[textIndex]))
                    {
                        // CamelCase boundary
                        score += BonusCamelCase;
                    }
                }

                // Bonus for consecutive matches
                if (lastMatchIndex == textIndex - 1)
                {
                    consecutiveCount++;
                    score += BonusConsecutive * consecutiveCount;
                }
                else
                {
                    consecutiveCount = 0;

                    // Gap penalty (if not first match)
                    if (lastMatchIndex >= 0)
                    {
                        var gap = textIndex - lastMatchIndex - 1;
                        score += ScoreGapStart + (gap - 1) * ScoreGapExtension;
                    }
                }

                lastMatchIndex = textIndex;
                queryIndex++;
            }
        }

        // Check if all query characters were matched
        if (queryIndex < query.Length)
        {
            return (int.MinValue, []);
        }

        // Bonus for shorter target strings (prefer concise matches)
        score -= text.Length / 4;

        return (score, positions.ToArray());
    }

    private static bool IsWordBoundary(char c)
    {
        return c == ' ' || c == '_' || c == '-' || c == '/' || c == '\\' || c == '.' || c == ':';
    }
}
