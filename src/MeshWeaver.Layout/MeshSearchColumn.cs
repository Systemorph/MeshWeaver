using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeshWeaver.Layout;

/// <summary>
/// One column of a <see cref="MeshSearchRenderMode.Table"/> result table: a header
/// <paramref name="Title"/> and the <paramref name="Field"/> each cell reads off the result node.
/// <para><paramref name="Field"/> is a query-language SELECTOR, read exactly the way the store reads
/// it for <c>sort:</c> and filters: a node field (<c>name</c>, <c>nodeType</c>, <c>lastModified</c>)
/// or a content path (<c>content.receivedOn</c>, <c>content.cedent.name</c>). The same string is the
/// column's sort key, so a header click orders by the very value the column shows.</para>
/// </summary>
/// <param name="Title">The header text. Authored, so it renders as authored — a consumer that
/// serves several languages passes the viewer's title.</param>
/// <param name="Field">The selector each cell reads, e.g. <c>name</c> or <c>content.receivedOn</c>.</param>
public record MeshSearchColumn(string Title, string Field)
{
    /// <summary>
    /// An invariant-culture .NET format string for the cell (<c>yyyy-MM-dd HH:mm</c>, <c>N0</c>,
    /// <c>P1</c>). Null formats dates as <c>yyyy-MM-dd</c> and everything else by its invariant
    /// default. See <see cref="FormatValue"/>.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// Whether clicking this column's header sorts by it (default true). Turn it off for a column
    /// whose selector the store cannot order by.
    /// </summary>
    public bool Sortable { get; init; } = true;

    /// <summary>The default date format of a cell: ISO date, the same on every machine.</summary>
    public const string DefaultDateFormat = "yyyy-MM-dd";

    /// <summary>The cell text of a boolean <c>true</c>; <c>false</c> renders as <see cref="FalseText"/>.
    /// Glyphs, so the cell needs no translation.</summary>
    public const string TrueText = "✓";

    /// <summary>The cell text of a boolean <c>false</c>.</summary>
    public const string FalseText = "✗";

    // An ISO-8601 date or date-time at the START of a string ("2026-10-01", "2026-10-01T09:30:00Z").
    // Only these are read as dates: DateTime.TryParse alone would turn "1/2" or "10.5" into one.
    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}.*)?$", RegexOptions.Compiled);

    /// <summary>
    /// The text of one cell for <paramref name="value"/> — culture-INDEPENDENT, so a table reads the
    /// same on every machine and in every viewer locale. Pure.
    /// <list type="bullet">
    /// <item>null (absent field, JSON null) ⇒ empty: an empty cell stays empty.</item>
    /// <item>A date (<see cref="DateTime"/>, <see cref="DateTimeOffset"/>, <see cref="DateOnly"/>, or an
    /// ISO-8601 string as JSON content carries it) ⇒ <paramref name="format"/> or <c>yyyy-MM-dd</c>, in
    /// the value's own offset (no conversion to the server's zone).</item>
    /// <item>A boolean ⇒ <see cref="TrueText"/> / <see cref="FalseText"/>.</item>
    /// <item>A number ⇒ <paramref name="format"/> in the invariant culture.</item>
    /// <item>A list ⇒ its items, comma-separated.</item>
    /// <item>Anything else ⇒ its invariant string.</item>
    /// </list>
    /// A <paramref name="format"/> the value cannot take renders the value by its default instead.
    /// </summary>
    /// <param name="value">The value the column's selector resolved to.</param>
    /// <param name="format">The column's <see cref="Format"/>.</param>
    public static string FormatValue(object? value, string? format = null)
    {
        switch (value)
        {
            case null:
                return "";
            case JsonElement json:
                return FormatJson(json, format);
            case string s:
                return IsoDate.IsMatch(s)
                       && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                           DateTimeStyles.AssumeUniversal, out var parsed)
                    ? FormatDate(parsed, format)
                    : s;
            case bool b:
                return b ? TrueText : FalseText;
            case DateTime dt:
                return FormatDate(dt, format);
            case DateTimeOffset dto:
                return FormatDate(dto, format);
            case DateOnly d:
                return FormatDate(d, format);
            case Enum e:
                return e.ToString();
            case IFormattable f:
                return FormatInvariant(f, format, fallback: null);
            case IEnumerable list:
                return string.Join(", ", list.Cast<object?>().Select(item => FormatValue(item, format))
                    .Where(text => text.Length > 0));
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }

    private static string FormatJson(JsonElement json, string? format) => json.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => FormatValue(json.GetString(), format),
        JsonValueKind.True => TrueText,
        JsonValueKind.False => FalseText,
        // Never a throwing read: a valid JSON number outside decimal's range (1e100) still renders,
        // as the same value typed as a double would.
        JsonValueKind.Number => json.TryGetInt64(out var l) ? FormatValue(l, format)
            : json.TryGetDecimal(out var m) ? FormatValue(m, format)
            : json.TryGetDouble(out var d) ? FormatValue(d, format)
            : json.GetRawText(),
        JsonValueKind.Array => string.Join(", ", json.EnumerateArray().Select(item => FormatJson(item, format))
            .Where(text => text.Length > 0)),
        _ => json.GetRawText(),
    };

    private static string FormatDate(IFormattable date, string? format) =>
        FormatInvariant(date, format ?? DefaultDateFormat, fallback: DefaultDateFormat);

    private static string FormatInvariant(IFormattable value, string? format, string? fallback)
    {
        try
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            // An authored format the value cannot take ("N0" on a date): the value in its kind's
            // default (a date stays ISO), not a dead table.
            return value.ToString(fallback, CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>
/// The order a <see cref="MeshSearchRenderMode.Table"/> header click put the results in: the
/// column's <paramref name="Field"/> and the direction. It is applied by the STORE — the view appends
/// <see cref="Term"/> (<c>sort:content.receivedOn-desc</c>) to the query it already sends — so paging
/// and "Load more" stay correct and nothing is re-sorted client-side.
/// </summary>
/// <param name="Field">The column's selector (<see cref="MeshSearchColumn.Field"/>).</param>
/// <param name="Descending">True for newest/largest/Z first.</param>
public record MeshSearchColumnSort(string Field, bool Descending = false)
{
    /// <summary>
    /// The store-side sort term: <c>sort:{Field}</c> or <c>sort:{Field}-desc</c>. The parser reads the
    /// text after the LAST hyphen as the direction, so a selector that itself contains one
    /// (<c>content.policy-id</c>) says <c>-asc</c> explicitly — bare, it would sort by
    /// <c>content.policy</c>.
    /// </summary>
    public string Term => Descending ? $"sort:{Field}-desc"
        : Field.Contains('-') ? $"sort:{Field}-asc"
        : $"sort:{Field}";

    /// <summary>
    /// The sort after a click on the header of <paramref name="field"/>: a column that is not the
    /// current sort starts ASCENDING; a second click on the same column flips the direction (and a
    /// third flips it back). Pure.
    /// </summary>
    /// <param name="current">The sort before the click; null when no header has been clicked.</param>
    /// <param name="field">The clicked column's <see cref="MeshSearchColumn.Field"/>.</param>
    public static MeshSearchColumnSort Toggle(MeshSearchColumnSort? current, string field) =>
        current is not null && string.Equals(current.Field, field, StringComparison.OrdinalIgnoreCase)
            ? current with { Descending = !current.Descending }
            : new MeshSearchColumnSort(field);

    /// <summary>
    /// <paramref name="query"/> ordered by <paramref name="sort"/>: the term is APPENDED to every line
    /// (a union query — one sub-query per line — is ordered by its FIRST line, so every line carries
    /// it). The query parser keeps the LAST <c>sort:</c> it reads, so the header's order wins over a
    /// <c>sort:</c> earlier in the line — the hidden query's, a Sort-by option's, or one the reader
    /// typed — while every filter of the query stays as it was. Null <paramref name="sort"/> returns
    /// the query unchanged. Pure.
    /// </summary>
    /// <param name="query">The full query the view sends (hidden query plus typed text).</param>
    /// <param name="sort">The header sort; null when no header has been clicked.</param>
    public static string Apply(string query, MeshSearchColumnSort? sort)
    {
        if (sort is null)
            return query;
        var lines = query.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return sort.Term;
        return string.Join("\n", lines.Select(line => $"{line} {sort.Term}"));
    }
}
