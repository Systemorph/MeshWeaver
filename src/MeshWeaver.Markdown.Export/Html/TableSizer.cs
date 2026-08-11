using System.Globalization;
using HtmlAgilityPack;

namespace MeshWeaver.Markdown.Export.Html;

/// <summary>
/// Gives every markdown table explicit, content-proportional column widths.
///
/// <para><b>Why this exists.</b> Markdig emits a bare <c>&lt;table&gt;</c> and leaves sizing to the
/// browser's auto-layout. A mail client has none worth relying on: Outlook renders through Word,
/// which lays a table out on whatever widths it is GIVEN. Two consequences drive everything
/// below:</para>
/// <list type="bullet">
/// <item><description><b>Word ignores <c>&lt;colgroup&gt;</c>.</b> The obvious fix — one
/// <c>&lt;col width&gt;</c> per column — is silently dropped, and the table renders with Word's own
/// guess. The width has to be repeated on EVERY <c>&lt;td&gt;</c>/<c>&lt;th&gt;</c>, as both the
/// attribute and the inline style, which is what this class does.</description></item>
/// <item><description><b>Equal columns are wrong.</b> A table with a 3-character "No." column and
/// a paragraph-length "Comment" column reads terribly at 50/50. Widths are therefore proportional
/// to each column's actual content volume.</description></item>
/// </list>
///
/// <para>The proportion is <b>damped by a square root</b> rather than used raw: a column with ten
/// times the text does not deserve ten times the width — it wraps happily — whereas a short column
/// squeezed to nothing becomes unreadable. Square-root damping plus a floor and a cap keeps a
/// paragraph column from starving the others while leaving a tiny column legible.</para>
/// </summary>
public static class TableSizer
{
    /// <summary>Narrowest a column may be, in percent — below this even short values wrap badly.</summary>
    private const double MinPercent = 11.0;

    /// <summary>Widest a column may be, in percent — stops one prose column eating the table.</summary>
    private const double MaxPercent = 42.0;

    /// <summary>Passes of clamp-and-redistribute. Converges well before this in practice.</summary>
    private const int BalancePasses = 4;

    private const string TableStyle =
        "width:100%;border-collapse:collapse;table-layout:fixed;margin:14px 0 20px 0;"
        + "font-size:13px;line-height:1.45";

    private const string CellStyle =
        "border:1px solid " + MarkupStyles.BorderColor + ";padding:8px 10px;vertical-align:top;text-align:left";

    private const string HeaderCellExtra = ";background:#f1f5f9;font-weight:700";

    /// <summary>
    /// Rewrites every CONTENT table under <paramref name="root"/> in place. Layout tables this
    /// renderer generated itself (marked <c>role="presentation"</c> — the card grids) are skipped:
    /// they already carry deliberate widths.
    /// </summary>
    public static int SizeTables(HtmlNode root)
    {
        var tables = root.Descendants("table")
            .Where(t => !string.Equals(t.GetAttributeValue("role", string.Empty), "presentation",
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var table in tables)
            SizeTable(table);

        return tables.Count;
    }

    private static void SizeTable(HtmlNode table)
    {
        var rows = table.Descendants("tr").ToList();
        if (rows.Count == 0)
            return;

        var grid = rows
            .Select(r => r.ChildNodes
                .Where(c => c.Name is "td" or "th")
                .ToList())
            .ToList();

        var columns = grid.Count == 0 ? 0 : grid.Max(r => r.Count);
        if (columns == 0)
            return;

        var widths = ComputeWidths(grid, columns);

        foreach (var cells in grid)
        {
            for (var i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                var width = (i < columns ? widths[i] : 100.0 / columns)
                    .ToString("0.###", CultureInfo.InvariantCulture) + "%";
                var isHeader = string.Equals(cell.Name, "th", StringComparison.OrdinalIgnoreCase);

                // BOTH the attribute and the style: Word prefers the attribute, modern clients
                // the style, and disagreeing with itself is how a table ends up ragged.
                cell.SetAttributeValue("width", width);
                cell.SetAttributeValue("style",
                    $"width:{width};{CellStyle}{(isHeader ? HeaderCellExtra : string.Empty)}");
            }
        }

        // 🚨 Deliberately NOT role="presentation". These are the document's OWN tables — data the
        // author wrote — and marking them presentational strips their semantics from a screen
        // reader, which is a real loss for the reader and buys nothing: the sizing below is what
        // Word needs, and Word does not consult `role`. Only the LAYOUT tables the card grid is
        // built from carry role="presentation", and those are set by AreaMarkupRenderer, which
        // knows they are scaffolding rather than content.
        table.SetAttributeValue("cellpadding", "0");
        table.SetAttributeValue("cellspacing", "0");
        table.SetAttributeValue("border", "0");
        table.SetAttributeValue("style", TableStyle);

        // Word ignores it and it would only contradict the per-cell widths above.
        foreach (var colgroup in table.Descendants("colgroup").ToList())
            colgroup.Remove();
    }

    /// <summary>
    /// Column widths in percent: square-root-damped mean content length per column, normalised,
    /// then clamped into [<see cref="MinPercent"/>, <see cref="MaxPercent"/>] with the slack
    /// redistributed across the columns still free to move.
    /// </summary>
    internal static double[] ComputeWidths(IReadOnlyList<List<HtmlNode>> grid, int columns)
    {
        var weights = new double[columns];
        for (var c = 0; c < columns; c++)
        {
            // Measure the BODY rows: a header label ("Comment") says nothing about how much text
            // the column actually carries. Fall back to all rows for a header-only table.
            var lengths = grid
                .Where(r => r.Count > c)
                .Select(r => (double)TextLength(r[c]))
                .ToList();
            var body = lengths.Count > 1 ? lengths.Skip(1).ToList() : lengths;
            var mean = body.Count == 0 ? 1.0 : Math.Max(body.Average(), 1.0);
            weights[c] = Math.Sqrt(mean);
        }

        var total = weights.Sum();
        var percent = total <= 0
            ? Enumerable.Repeat(100.0 / columns, columns).ToArray()
            : weights.Select(w => 100.0 * w / total).ToArray();

        for (var pass = 0; pass < BalancePasses; pass++)
        {
            var pinned = Enumerable.Range(0, columns)
                .Where(i => percent[i] < MinPercent || percent[i] > MaxPercent)
                .ToDictionary(i => i, i => Math.Clamp(percent[i], MinPercent, MaxPercent));

            if (pinned.Count == 0)
                break;

            var free = Enumerable.Range(0, columns).Where(i => !pinned.ContainsKey(i)).ToList();
            var remaining = 100.0 - pinned.Values.Sum();
            if (free.Count == 0 || remaining <= 0)
            {
                percent = Enumerable.Range(0, columns)
                    .Select(i => pinned.TryGetValue(i, out var v) ? v : percent[i])
                    .ToArray();
                break;
            }

            var freeTotal = free.Sum(i => percent[i]);
            if (freeTotal <= 0)
                freeTotal = 1;

            percent = Enumerable.Range(0, columns)
                .Select(i => pinned.TryGetValue(i, out var v)
                    ? v
                    : percent[i] / freeTotal * remaining)
                .ToArray();
        }

        return percent;
    }

    /// <summary>Visible text length of a cell — markup contributes no width.</summary>
    private static int TextLength(HtmlNode cell)
    {
        var text = HtmlEntity.DeEntitize(cell.InnerText) ?? string.Empty;
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Length;
    }
}
