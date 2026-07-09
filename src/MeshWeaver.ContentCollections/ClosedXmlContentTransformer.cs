using System.Collections.Immutable;
using System.Text;
using ClosedXML.Excel;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Transforms .xlsx / .xls spreadsheets to Markdown tables using ClosedXML — one table per
/// worksheet, rows keyed by their Excel row number and columns by their Excel letter.
/// Registered as IContentTransformer via DI. Restores the reader that the old agent
/// <c>ContentPlugin.ReadExcelFile</c> provided before the read path was consolidated onto the
/// content-collection transformer seam (only <c>.docx</c> survived that move). Without this, a
/// spreadsheet read through the content-collection file path falls through to the raw StreamReader
/// and returns the binary OpenXML zip bytes decoded as UTF-8.
/// </summary>
public class ClosedXmlContentTransformer : IContentTransformer
{
    private static readonly ImmutableHashSet<string> Extensions =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, ".xlsx", ".xls");

    /// <summary>The file extensions this transformer handles (<c>.xlsx</c>, <c>.xls</c>).</summary>
    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    /// <summary>Renders every worksheet of a spreadsheet stream as a Markdown table.</summary>
    /// <param name="input">The spreadsheet stream to convert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The workbook rendered as Markdown, one <c>## Sheet: {name}</c> section per worksheet.</returns>
    public async Task<string> TransformToMarkdownAsync(Stream input, CancellationToken ct = default)
    {
        // ClosedXML needs random access — buffer first so a non-seekable source stream
        // (e.g. an Azure blob read stream) parses correctly.
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        using var workbook = new XLWorkbook(buffer);
        var sb = new StringBuilder();

        foreach (var ws in workbook.Worksheets)
        {
            ct.ThrowIfCancellationRequested();
            sb.AppendLine($"## Sheet: {ws.Name}");
            sb.AppendLine();

            var used = ws.RangeUsed();
            if (used is null)
            {
                sb.AppendLine("(No data)");
                sb.AppendLine();
                continue;
            }

            var firstRow = used.FirstRow().RowNumber();
            var lastRow = used.LastRow().RowNumber();
            var lastCol = used.LastColumn().ColumnNumber();

            var columnHeaders = new List<string> { "Row" };
            for (var c = 1; c <= lastCol; c++)
                columnHeaders.Add(GetExcelColumnLetter(c));

            sb.AppendLine("| " + string.Join(" | ", columnHeaders) + " |");
            sb.AppendLine("|" + string.Concat(columnHeaders.Select(_ => "---:|")));

            for (var r = firstRow; r <= lastRow; r++)
            {
                var rowVals = new List<string> { r.ToString() };
                for (var c = 1; c <= lastCol; c++)
                {
                    var raw = ws.Cell(r, c).GetValue<string>();
                    var val = raw?.Replace('\n', ' ').Replace('\r', ' ').Replace("|", "\\|").Trim();
                    rowVals.Add(string.IsNullOrEmpty(val) ? "" : val);
                }

                sb.AppendLine("| " + string.Join(" | ", rowVals) + " |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Converts a 1-based column number to its Excel letter (1=A, 27=AA).</summary>
    private static string GetExcelColumnLetter(int columnNumber)
    {
        var columnLetter = "";
        while (columnNumber > 0)
        {
            var modulo = (columnNumber - 1) % 26;
            columnLetter = Convert.ToChar('A' + modulo) + columnLetter;
            columnNumber = (columnNumber - 1) / 26;
        }
        return columnLetter;
    }
}
