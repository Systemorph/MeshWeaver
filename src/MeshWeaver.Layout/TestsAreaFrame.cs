using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Data;

namespace MeshWeaver.Layout;

/// <summary>
/// One read of a <c>Tests</c> area as it travels the sync stream (a serialized area store) — the
/// shape a consumer that is NOT the page needs: is the run still going, which cases are where, and
/// what did the verdict say. Pure over the JSON.
///
/// <para>The framework writes chrome into every area subscription (<c>$Menu…</c>, <c>$Banner</c>,
/// <c>$Dialog</c>); chrome never counts — the Approvals menu entry's icon IS the ✅ emoji.</para>
/// </summary>
/// <param name="Materialized">False until the requested area's own control has landed.</param>
/// <param name="Transient">True for a frame that a later one replaces: a streamed progress frame
/// (<see cref="AreaFrameClassifier.TestsRunningId"/>) or the compile-progress page.</param>
/// <param name="NotFound">True when the hub answered that no renderer exists for the area.</param>
/// <param name="Title">The frame's title line (progress or verdict), when it has one.</param>
/// <param name="Rows">The cases the frame lists as grid rows (empty for a table rendered as markdown).</param>
/// <param name="Text">Every other content string that carries a verdict glyph or summary.</param>
public sealed record TestsAreaFrame(
    bool Materialized,
    bool Transient,
    bool NotFound,
    string? Title,
    ImmutableArray<TestsAreaFrame.Row> Rows,
    ImmutableArray<string> Text)
{
    /// <summary>One case as the area renders it.</summary>
    /// <param name="Class">The class (or suite) column.</param>
    /// <param name="Case">The case's name.</param>
    /// <param name="Result">Its status glyph / verdict.</param>
    /// <param name="Time">Its elapsed time.</param>
    /// <param name="Output">Its failure message and output lines.</param>
    public sealed record Row(string Class, string Case, string Result, string Time, string Output)
    {
        /// <summary>The key a case is tracked by across frames — class AND case, because two
        /// classes can carry a case of the same name.</summary>
        public string Key => $"{Class}\u001f{Case}";
    }

    private static readonly Regex PassSummary = new(@"(\d+)\s*/\s*(\d+)\s+passed", RegexOptions.CultureInvariant);

    /// <summary>True when the frame is a VERDICT and every counted case passed.</summary>
    public bool Passed
    {
        get
        {
            if (!Materialized || Transient || NotFound)
                return false;
            var all = Rows.Select(r => r.Result).Concat(Text).Append(Title ?? "").ToImmutableArray();
            if (all.Any(s => s.Contains('❌')))
                return false;
            var summary = all.Select(s => PassSummary.Match(s)).FirstOrDefault(m => m.Success);
            return summary is null
                ? all.Any(s => s.Contains('✅'))
                : summary.Groups[1].Value == summary.Groups[2].Value;
        }
    }

    /// <summary>
    /// How many counted cases passed, out of how many: the verdict's own <c>N/M passed</c> when it
    /// carries one, otherwise the rows (✅ over every row that is not ⏭ skipped).
    /// </summary>
    public (int Passed, int Total) Counts()
    {
        var summary = Text.Prepend(Title ?? "").Select(s => PassSummary.Match(s)).FirstOrDefault(m => m.Success);
        if (summary is not null)
            return (int.Parse(summary.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(summary.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        var counted = Rows.Where(r => !r.Result.StartsWith('⏭')).ToImmutableArray();
        return (counted.Count(r => r.Result.StartsWith('✅')), counted.Length);
    }

    /// <summary>Reads one serialized area store for the area <paramref name="area"/>.</summary>
    /// <param name="store">The frame as the sync stream carries it.</param>
    /// <param name="area">The area name (normally <c>Tests</c>).</param>
    public static TestsAreaFrame Read(JsonElement store, string area = "Tests")
    {
        var rows = ImmutableArray.CreateBuilder<Row>();
        var text = ImmutableArray.CreateBuilder<string>();
        string? title = null;
        var transient = false;
        var notFound = false;
        var materialized = false;
        if (store.ValueKind == JsonValueKind.Object
            && store.TryGetProperty(LayoutAreaReference.Areas, out var areas)
            && areas.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in areas.EnumerateObject())
            {
                var name = entry.Name.Trim('"');
                if (name.StartsWith('$'))
                    continue;
                if (name == area && entry.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    materialized = true;
                if (name == area || name.StartsWith(area + "/", StringComparison.Ordinal))
                    Walk(entry.Value);
            }
        }
        return new TestsAreaFrame(materialized, transient, notFound, title, rows.ToImmutable(), text.ToImmutable());

        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    if (e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        var value = id.GetString();
                        if (value is AreaFrameClassifier.TestsRunningId or AreaFrameClassifier.CompileProgressId)
                            transient = true;
                        else if (value == AreaFrameClassifier.AreaNotFoundId)
                            notFound = true;
                    }
                    if (e.TryGetProperty("case", out var c) && e.TryGetProperty("result", out var r))
                    {
                        rows.Add(new Row(Str(e, "class"), Str(c), Str(r), Str(e, "time"), Str(e, "output")));
                        return;
                    }
                    foreach (var p in e.EnumerateObject())
                        Walk(p.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray())
                        Walk(item);
                    break;
                case JsonValueKind.String:
                    var s = e.GetString() ?? "";
                    if (s.Contains("**Area not found**", StringComparison.Ordinal))
                        notFound = true;
                    else if (title is null && s.Contains(" — ", StringComparison.Ordinal) && s.Contains("ests", StringComparison.Ordinal))
                        title = Regex.Replace(s, "<[^>]+>", "").Trim();
                    else if (s.Contains('✅') || s.Contains('❌') || PassSummary.IsMatch(s))
                        text.Add(s);
                    break;
            }
        }
    }

    private static string Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString();

    private static string Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) ? Str(v) : "";

    /// <summary>
    /// The rows of <paramref name="next"/> that are new, or whose result or output changed, since
    /// <paramref name="printed"/> — and the state to carry to the next frame. A row whose only
    /// change is its ticking time is not a change.
    /// </summary>
    /// <param name="printed">The rows as last reported, by <see cref="Row.Key"/>.</param>
    /// <param name="next">The frame just read.</param>
    public static (ImmutableArray<Row> Changed, ImmutableDictionary<string, Row> Printed) Changes(
        ImmutableDictionary<string, Row> printed, TestsAreaFrame next)
    {
        var changed = ImmutableArray.CreateBuilder<Row>();
        foreach (var row in next.Rows)
        {
            if (printed.TryGetValue(row.Key, out var before)
                && before.Result == row.Result && before.Output == row.Output)
                continue;
            printed = printed.SetItem(row.Key, row);
            changed.Add(row);
        }
        return (changed.ToImmutable(), printed);
    }

    /// <summary>One console line for a row.</summary>
    /// <param name="row">The row.</param>
    public static string Line(Row row) =>
        $"{row.Result} {row.Case}{(row.Time.Length > 0 ? $" ({row.Time})" : "")}{(row.Output.Length > 0 ? $" — {row.Output}" : "")}";
}
