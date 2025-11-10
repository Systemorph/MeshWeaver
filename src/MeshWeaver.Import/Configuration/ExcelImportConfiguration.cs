using System.Diagnostics.CodeAnalysis;

namespace MeshWeaver.Import.Configuration;

/// <summary>
/// Configuration describing how to transform an Excel worksheet into typed entities.
/// </summary>
public class ExcelImportConfiguration : ImportConfiguration
{
    public ExcelImportConfiguration()
    {
    }

    [SetsRequiredMembers]
    public ExcelImportConfiguration(string name, string address, string worksheetName, int dataStartRow, HashSet<string> totalRowMarkers, bool totalRowScanAllCells, bool totalRowMatchExact, List<string> ignoreRowExpressions)
    {
        Name = name;
        Address = address;
        WorksheetName = worksheetName;
        DataStartRow = dataStartRow;
        TotalRowMarkers = totalRowMarkers;
        TotalRowScanAllCells = totalRowScanAllCells;
        TotalRowMatchExact = totalRowMatchExact;
        IgnoreRowExpressions = ignoreRowExpressions;
    }

    /// <summary>
    /// The fully qualified type name of the entity to import (e.g., "MeshWeaver.Insurance.Domain.PropertyRisk").
    /// Used for automatic entity instantiation.
    /// </summary>
    public string? TypeName { get; set; }

    /// <summary>
    /// Name of the worksheet within the Excel file to process.
    /// </summary>
    public string WorksheetName { get; set; } = string.Empty;
    /// <summary>
    /// 1-based row index where data rows start. Do not count the heading row, this number refers to the first data row.
    /// </summary>
    public int DataStartRow { get; set; } = 2;
    /// <summary>
    /// Column-to-property mappings that create or derive entity fields from worksheet columns.
    /// </summary>
    public List<ColumnMapping> Mappings { get; set; } = new();
    /// <summary>
    /// Rules for allocating a total value across rows proportionally to given weight columns.
    /// </summary>
    public List<AllocationMapping> Allocations { get; set; } = new();
    /// <summary>
    /// Row markers indicating a total/grand-total row that should be ignored.
    /// </summary>
    public HashSet<string> TotalRowMarkers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// When true, scan all used cells in the row; otherwise restrict to <see cref="TotalRowScanColumns"/>.
    /// </summary>
    public bool TotalRowScanAllCells { get; set; } = true;
    /// <summary>
    /// Optional column letters to scan for total markers when <see cref="TotalRowScanAllCells"/> is false.
    /// </summary>
    public List<string> TotalRowScanColumns { get; set; } = new();
    /// <summary>
    /// When true, total markers must match exactly; otherwise a substring match is used.
    /// </summary>
    public bool TotalRowMatchExact { get; set; } = false;

    /// <summary>
    /// Expressions evaluated per row; if any evaluates to true, the row is ignored.
    /// Example: "Address == null" to skip rows without an address.
    /// </summary>
    public List<string> IgnoreRowExpressions { get; set; } = new();
}

/// <summary>
/// Supported mapping strategies from worksheet columns to entity properties.
/// </summary>
public enum MappingKind
{
    /// <summary>Copy the value directly from a single column.</summary>
    Direct,
    /// <summary>Sum the values of multiple columns.</summary>
    Sum,
    /// <summary>Use a fixed constant value.</summary>
    Constant,
    /// <summary>Compute difference of two columns as (second - first).</summary>
    Difference
}

/// <summary>
/// Describes how to populate a specific target entity property from one or more worksheet columns.
/// </summary>
public class ColumnMapping
{
    /// <summary>Destination entity property name (e.g. Country, Id).</summary>
    public string TargetProperty { get; set; } = string.Empty;
    /// <summary>Strategy for deriving the value.</summary>
    public MappingKind Kind { get; set; } = MappingKind.Direct;
    /// <summary>
    /// For Direct: one column letter (e.g. "B"). For Sum/Difference: multiple column letters.
    /// </summary>
    public List<string> SourceColumns { get; set; } = new();
    /// <summary>Used when <see cref="Kind"/> is Constant.</summary>
    public object? ConstantValue { get; set; }
}

/// <summary>
/// Allocates a total value (from a single cell, e.g. C3) proportionally to row weights (e.g. TSI columns), row by row.
/// </summary>
public class AllocationMapping
{
    /// <summary>Destination entity property to populate (e.g. EqTsiBi).</summary>
    public string TargetProperty { get; set; } = string.Empty;
    /// <summary>Cell address of the overall total (e.g. C3).</summary>
    public string TotalCell { get; set; } = string.Empty;
    /// <summary>Column letters used as weights to distribute the total (e.g. ["G"]).</summary>
    public List<string> WeightColumns { get; set; } = new();
    /// <summary>Optional target property to also set the currency for allocated values.</summary>
    public string? CurrencyProperty { get; set; }
}
