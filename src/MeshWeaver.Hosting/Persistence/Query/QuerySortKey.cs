using System.Globalization;
using System.Text.Json;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// The ONE in-memory definition of how a <c>sort:</c> key compares — used by
/// <see cref="QueryEvaluator.OrderResults{T}"/> (the merge layer's re-sort in
/// <c>MeshQuery.ClipMergedInitial</c>, which every backend's result passes through) and by
/// <c>StorageAdapterMeshQueryProvider</c> before its load cap. The Postgres generator emits the
/// same contract in SQL (MeshWeaver.Plugins <c>PostgreSqlSqlGenerator.OrderByTerms</c>), so the rows
/// a backend's <c>LIMIT</c> keeps and the order the merge hands back agree.
///
/// <para><b>The contract</b> (documented in <c>Doc/DataMesh/QuerySyntax</c> → Sorting):</para>
/// <list type="bullet">
///   <item><b>Numbers</b> compare numerically (<c>9 &lt; 10 &lt; 100</c>), whatever their CLR type.</item>
///   <item><b>Everything else compares as TEXT</b>, case-insensitively (invariant culture, ordinal
///     tiebreak): strings as written; dates and date-times as ISO-8601 (<c>yyyy-MM-dd</c>,
///     <c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c> in UTC), which is chronological and is also the text a
///     JSON content field holds in a store — so a typed <see cref="DateOnly"/> and the same date
///     read as a JSON string are the same key; booleans as <c>false</c>/<c>true</c>.</item>
///   <item><b>Numbers rank before text</b> in both directions — a mixed-type field is grouped,
///     never interleaved.</item>
///   <item><b>Null or absent sorts LAST in both directions.</b> A row without the field is never
///     the "first" answer to either <c>sort:x</c> or <c>sort:x-desc</c>, and Postgres is told the
///     same with <c>NULLS LAST</c> (its own default puts nulls FIRST descending).</item>
/// </list>
///
/// <para>🚨 <b>Why a canonical key rather than the raw value.</b> The raw value's
/// <c>ToString()</c> was the fallback for every type the comparer did not know, and a node type's
/// compiled content class carries a date as <see cref="DateOnly"/>, whose invariant text is
/// <c>MM/dd/yyyy</c>: the merge re-sorted a correctly ordered Postgres page by MONTH, so
/// <c>sort:content.inception</c> and <c>sort:content.inception-desc</c> both put the 2026 row at an
/// end it did not belong to. Every key is therefore reduced to <see cref="double"/>,
/// <see cref="string"/> or <see langword="null"/> — a total order with no culture in it.</para>
/// </summary>
public static class QuerySortKey
{
    /// <summary>ISO-8601 round-trip form of a UTC instant — fixed width, so text order is time order.</summary>
    private const string UtcInstantFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>
    /// Reduces a resolved property value to its canonical sort key: a <see cref="double"/> for a
    /// number, a <see cref="string"/> for everything else, <see langword="null"/> for null/absent.
    /// </summary>
    /// <param name="value">The value a selector resolved to — CLR or <see cref="JsonElement"/>.</param>
    /// <returns>The canonical key.</returns>
    public static object? Normalize(object? value) => value switch
    {
        null => null,
        JsonElement element => NormalizeJson(element),
        string s => s,
        bool b => b ? "true" : "false",
        DateTimeOffset dto => dto.UtcDateTime.ToString(UtcInstantFormat, CultureInfo.InvariantCulture),
        DateTime dt => dt.Kind switch
        {
            DateTimeKind.Utc => dt.ToString(UtcInstantFormat, CultureInfo.InvariantCulture),
            DateTimeKind.Local => dt.ToUniversalTime().ToString(UtcInstantFormat, CultureInfo.InvariantCulture),
            // Unspecified: no zone to normalise from — the wall-clock ISO form, as JSON writes it.
            _ => dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        },
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
            => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static object? NormalizeJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Object or JsonValueKind.Array => element.GetRawText(),
        _ => null,
    };

    /// <summary>The comparer for canonical keys in the given direction — nulls last either way.</summary>
    /// <param name="descending">Whether the sort runs descending.</param>
    /// <returns>A total comparer over <see cref="Normalize"/>'s output.</returns>
    public static IComparer<object?> Comparer(bool descending)
        => descending ? KeyComparer.Descending : KeyComparer.Ascending;

    private sealed class KeyComparer(bool descending) : IComparer<object?>
    {
        public static readonly KeyComparer Ascending = new(false);
        public static readonly KeyComparer Descending = new(true);

        public int Compare(object? x, object? y)
        {
            // Null placement is NOT reversed by the direction: absent is last both ways.
            if (x is null)
                return y is null ? 0 : 1;
            if (y is null)
                return -1;

            // Rank (number before text) is not reversed either — a mixed field stays grouped.
            var xNumber = x is double;
            var yNumber = y is double;
            if (xNumber != yNumber)
                return xNumber ? -1 : 1;

            var c = xNumber
                ? ((double)x).CompareTo((double)y)
                : CompareText((string)x, (string)y);
            return descending ? -c : c;
        }

        private static int CompareText(string x, string y)
        {
            var c = string.Compare(x, y, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(x, y);
        }
    }
}
