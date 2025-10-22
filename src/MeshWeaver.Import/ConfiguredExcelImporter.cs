using ClosedXML.Excel;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Utils;

namespace MeshWeaver.Import;

/// <summary>
/// Imports entities from Excel files using declarative configuration.
/// </summary>
/// <typeparam name="T">The entity type to import</typeparam>
public class ConfiguredExcelImporter<T> where T : class
{
    private readonly Func<Dictionary<string, object?>, T> entityBuilder;

    public ConfiguredExcelImporter(Func<Dictionary<string, object?>, T> entityBuilder)
    {
        this.entityBuilder = entityBuilder;
    }

    public IEnumerable<T> Import(Stream stream, string sourceName, ExcelImportConfiguration config)
    {
        using var wb = new XLWorkbook(stream);
        var ws = string.IsNullOrWhiteSpace(config.WorksheetName) ? wb.Worksheets.First() : wb.Worksheet(config.WorksheetName);

        // Pre-read total cells for allocations (tolerate duplicates/invalid addresses)
        var allocationTotals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in config.Allocations)
        {
            if (string.IsNullOrWhiteSpace(a.TotalCell))
                continue;
            // Only accept simple A1-style addresses; skip anything suspicious
            if (!IsValidCellAddress(a.TotalCell))
                continue;
            var totalVal = GetCellDecimal(ws, a.TotalCell) ?? 0m;
            // Last write wins if duplicates exist; avoids ArgumentException
            allocationTotals[a.TotalCell] = totalVal;
        }

        // Compute weight denominators lazily
        // Use worksheet-level RowsUsed() (returns IXLRow) to avoid IXLRangeRow vs IXLRow mismatch
        var rows = ws.RowsUsed().Where(r => r.RowNumber() >= config.DataStartRow);

        // Filter out total rows using flexible detection
        if (config.TotalRowMarkers.Count > 0)
            rows = rows.Where(r => !IsTotalRow(r, config));

        // Apply expression-based row ignores (e.g., Address == null)
        if (config.IgnoreRowExpressions.Count > 0)
            rows = rows.Where(r => !IsIgnoredByExpressions(r, config));

        // Pre-calc denominators for each allocation (sum of weights over data rows)
        var allocationDenominators = new Dictionary<AllocationMapping, decimal>();
        foreach (var alloc in config.Allocations)
        {
            decimal denom = 0m;
            foreach (var row in rows)
            {
                denom += SumWeightColumns(row, alloc.WeightColumns);
            }
            allocationDenominators[alloc] = denom == 0 ? 1 : denom; // avoid div by zero
        }

        // Need to re-enumerate rows (since enumerated above). Re-evaluate rows sequence.
        rows = ws.RowsUsed().Where(r => r.RowNumber() >= config.DataStartRow);
        if (config.TotalRowMarkers.Count > 0)
            rows = rows.Where(r => !IsTotalRow(r, config));
        if (config.IgnoreRowExpressions.Count > 0)
            rows = rows.Where(r => !IsIgnoredByExpressions(r, config));

        foreach (var row in rows)
        {
            var entityData = new Dictionary<string, object?>();
            // Source provenance
            entityData["SourceRow"] = row.RowNumber();
            entityData["SourceFile"] = sourceName;

            foreach (var map in config.Mappings)
            {
                object? value = map.Kind switch
                {
                    MappingKind.Direct => map.SourceColumns.Count > 0 ? GetCellValue(row, map.SourceColumns.First()) : null,
                    MappingKind.Sum => SumColumns(row, map.SourceColumns),
                    MappingKind.Difference => DiffColumns(row, map.SourceColumns),
                    MappingKind.Constant => map.ConstantValue,
                    _ => null
                };
                entityData[map.TargetProperty.ToPascalCase()!] = value;
            }

            // Allocation mappings
            foreach (var alloc in config.Allocations)
            {
                allocationTotals.TryGetValue(alloc.TotalCell ?? string.Empty, out var total);
                var weight = SumWeightColumns(row, alloc.WeightColumns);
                var denom = allocationDenominators[alloc];
                var allocated = denom == 0 ? 0 : (total * (weight / denom));
                entityData[alloc.TargetProperty.ToPascalCase()!] = allocated;
            }

            yield return entityBuilder(entityData);
        }
    }

    public IEnumerable<T> Import(string filePath, ExcelImportConfiguration config)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        foreach (var entity in Import(fs, Path.GetFileName(filePath), config))
            yield return entity;
    }

    private static bool IsTotalRow(IXLRow row, ExcelImportConfiguration config)
    {
        if (config.TotalRowMarkers.Count == 0) return false;
        IEnumerable<IXLCell> cells;
        if (config.TotalRowScanColumns.Count > 0)
        {
            cells = config.TotalRowScanColumns.Select(col => row.Worksheet.Cell(row.RowNumber(), ColumnLetterToNumber(col)));
        }
        else if (config.TotalRowScanAllCells)
        {
            cells = row.CellsUsed();
        }
        else
        {
            cells = new[] { row.Cell(1) };
        }
        foreach (var cell in cells)
        {
            var text = GetStringSafe(cell).Trim();
            if (string.IsNullOrEmpty(text)) continue;
            foreach (var marker in config.TotalRowMarkers)
            {
                if (config.TotalRowMatchExact)
                {
                    if (string.Equals(text, marker, StringComparison.OrdinalIgnoreCase)) return true;
                }
                else
                {
                    if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
        }
        return false;
    }

    private static bool IsIgnoredByExpressions(IXLRow row, ExcelImportConfiguration config)
    {
        foreach (var expr in config.IgnoreRowExpressions)
        {
            if (string.IsNullOrWhiteSpace(expr)) continue;
            // Support minimal syntax: "Property == null" or "Property != null"
            var parts = expr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 3 && parts[1] is "==" or "!=" && parts[2].Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                var targetProp = parts[0];
                var op = parts[1];
                var isNull = EvaluatePropertyNull(row, config, targetProp);
                if ((op == "==" && isNull) || (op == "!=" && !isNull))
                    return true;
            }
        }
        return false;
    }

    private static bool EvaluatePropertyNull(IXLRow row, ExcelImportConfiguration config, string targetProperty)
    {
        // Find mapping for targetProperty; if none, treat as null (so expression can still skip)
        var map = config.Mappings.FirstOrDefault(m => string.Equals(m.TargetProperty, targetProperty, StringComparison.OrdinalIgnoreCase));
        if (map is null)
            return true;

        object? val = map.Kind switch
        {
            MappingKind.Direct => map.SourceColumns.Count > 0 ? GetCellValue(row, map.SourceColumns.First()) : null,
            MappingKind.Sum => SumColumns(row, map.SourceColumns),
            MappingKind.Difference => DiffColumns(row, map.SourceColumns),
            MappingKind.Constant => map.ConstantValue,
            _ => null
        };

        if (val is null) return true;
        if (val is string s) return string.IsNullOrWhiteSpace(s);
        if (val is decimal dec) return dec == 0m; // treat zero as null-like for numbers? keep false
        return false;
    }

    private static object? GetCellValue(IXLRow row, string columnLetter)
    {
        var cell = row.Worksheet.Cell(row.RowNumber(), ColumnLetterToNumber(columnLetter));
        cell = ResolveMergedAnchor(cell);
        if (cell.DataType == XLDataType.Number)
        {
            if (cell.TryGetValue(out decimal dec)) return dec;
            if (cell.TryGetValue(out double dbl)) return (decimal)dbl;
            if (cell.TryGetValue(out int i)) return i;
        }
        var s = GetStringSafe(cell);
        return s;
    }

    private static decimal SumColumns(IXLRow row, IEnumerable<string> columnLetters)
        => columnLetters.Select(c => GetCellDecimal(row.Worksheet, c + row.RowNumber()) ?? 0m).Sum();

    private static decimal SumWeightColumns(IXLRow row, IEnumerable<string> columnLetters)
    {
        decimal sum = 0m;
        foreach (var col in columnLetters)
        {
            var val = GetCellDecimal(row.Worksheet, col + row.RowNumber());
            if (val.HasValue) sum += val.Value;
        }
        return sum;
    }

    private static decimal DiffColumns(IXLRow row, IEnumerable<string> columnLetters)
    {
        var cols = columnLetters.Take(2).ToArray();
        var a = GetCellDecimal(row.Worksheet, cols.ElementAtOrDefault(0) + row.RowNumber()) ?? 0m;
        var b = GetCellDecimal(row.Worksheet, cols.ElementAtOrDefault(1) + row.RowNumber()) ?? 0m;
        return b - a;
    }

    private static decimal? GetCellDecimal(IXLWorksheet ws, string cellAddress)
    {
        var cell = ws.Cell(cellAddress);
        cell = ResolveMergedAnchor(cell);
        if (cell.DataType == XLDataType.Number)
        {
            if (cell.TryGetValue(out decimal dec)) return dec;
            if (cell.TryGetValue(out double dbl)) return (decimal)dbl;
            if (cell.TryGetValue(out int i)) return i;
        }
        var str = GetStringSafe(cell);
        // Try invariant culture first, then current culture, allow currency/thousands
        if (decimal.TryParse(str, System.Globalization.NumberStyles.Number | System.Globalization.NumberStyles.AllowCurrencySymbol, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        if (decimal.TryParse(str, System.Globalization.NumberStyles.Number | System.Globalization.NumberStyles.AllowCurrencySymbol, System.Globalization.CultureInfo.CurrentCulture, out parsed))
            return parsed;
        return null;
    }

    private static IXLCell ResolveMergedAnchor(IXLCell cell)
    {
        try
        {
            // If the cell is part of a merged range, always read from the range's first cell
            var ws = cell.Worksheet;
            foreach (var range in ws.MergedRanges)
            {
                // ClosedXML ranges include helper Contains overloads; fall back to address comparison if needed
                if (range.Contains(cell))
                {
                    var first = range.FirstCell();
                    return first ?? cell;
                }
            }
        }
        catch
        {
            // Be defensive; on any issue, use the original cell
        }
        return cell;
    }

    private static string GetStringSafe(IXLCell cell)
    {
        try
        {
            return cell.GetString();
        }
        catch
        {
            try { return cell.Value.ToString() ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    private static int ColumnLetterToNumber(string columnLetter)
    {
        int sum = 0;
        foreach (char c in columnLetter.ToUpperInvariant())
        {
            if (c < 'A' || c > 'Z') continue;
            sum *= 26;
            sum += (c - 'A' + 1);
        }
        return sum;
    }

    private static bool IsValidCellAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        int i = 0;
        // Optional leading '$' for absolute column
        if (i < address.Length && address[i] == '$') i++;

        // One or more letters (column)
        int startLetters = i;
        while (i < address.Length && char.IsLetter(address[i])) i++;
        if (i == startLetters) return false; // no letters

        // Optional '$' before row
        if (i < address.Length && address[i] == '$') i++;

        // One or more digits (row)
        int startDigits = i;
        while (i < address.Length && char.IsDigit(address[i])) i++;
        if (i == startDigits) return false; // no digits

        // Must consume entire string
        return i == address.Length;
    }
}
