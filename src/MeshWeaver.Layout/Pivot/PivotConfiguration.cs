namespace MeshWeaver.Layout.Pivot;

/// <summary>
/// Configures the layout and interactive behaviour of a pivot table control,
/// including which dimensions and aggregates are active, which are available
/// for user selection, and which interactive features are enabled.
/// </summary>
public record PivotConfiguration
{
    /// <summary>Active dimensions displayed as row headers in the pivot table.</summary>
    public IReadOnlyCollection<PivotDimension> RowDimensions { get; init; } = [];
    /// <summary>Active dimensions displayed as column headers in the pivot table.</summary>
    public IReadOnlyCollection<PivotDimension> ColumnDimensions { get; init; } = [];
    /// <summary>Active aggregate measures currently shown in the pivot table.</summary>
    public IReadOnlyCollection<PivotAggregate> Aggregates { get; init; } = [];
    /// <summary>Dimensions that users can drag into the row or column areas when field-picking is enabled.</summary>
    public IReadOnlyCollection<PivotDimension> AvailableDimensions { get; init; } = [];
    /// <summary>Aggregate measures that users can activate when field-picking is enabled.</summary>
    public IReadOnlyCollection<PivotAggregate> AvailableAggregates { get; init; } = [];
    /// <summary>Whether row-total cells are rendered. Defaults to <c>true</c>.</summary>
    public bool ShowRowTotals { get; init; } = true;
    /// <summary>Whether column-total cells are rendered. Defaults to <c>true</c>.</summary>
    public bool ShowColumnTotals { get; init; } = true;
    /// <summary>Whether users can click dimension headers to sort. Defaults to <c>true</c>.</summary>
    public bool AllowSorting { get; init; } = true;
    /// <summary>Whether filter controls are shown on dimensions. Defaults to <c>true</c>.</summary>
    public bool AllowFiltering { get; init; } = true;
    /// <summary>Whether users can drill into a grouped cell to see its contributing rows. Defaults to <c>true</c>.</summary>
    public bool AllowDrillDown { get; init; } = true;
    /// <summary>Whether users can open a field-picker panel to change active dimensions and aggregates. Defaults to <c>true</c>.</summary>
    public bool AllowFieldsPicking { get; init; } = true;
    /// <summary>Whether the pivot table paginates its rows. Defaults to <c>true</c>.</summary>
    public bool AllowPaging { get; init; } = true;
    /// <summary>Number of rows per page when paging is enabled. Defaults to 50.</summary>
    public int PageSize { get; init; } = 50;
}

/// <summary>
/// Describes a single pivot dimension used as a row or column grouping axis.
/// </summary>
public record PivotDimension
{
    /// <summary>Unique field identifier used to reference this dimension internally.</summary>
    public required string Field { get; init; }
    /// <summary>Human-readable label shown in the pivot table header for this dimension.</summary>
    public required string DisplayName { get; init; }
    /// <summary>Dot-separated path to the data property this dimension maps to.</summary>
    public required string PropertyPath { get; init; }
    /// <summary>Fully qualified or short type name of the underlying data property.</summary>
    public required string TypeName { get; init; }
    /// <summary>CSS width of the dimension column (e.g., "120px"). Null lets the renderer decide.</summary>
    public string? Width { get; init; }
    /// <summary>Whether users can filter on this dimension. Defaults to <c>true</c>.</summary>
    public bool Filterable { get; init; } = true;
    /// <summary>Whether users can sort on this dimension. Defaults to <c>true</c>.</summary>
    public bool Sortable { get; init; } = true;
    /// <summary>Default sort direction; null means unsorted initially.</summary>
    public SortOrder? SortOrder { get; init; }
}

/// <summary>
/// Describes a single aggregate measure column in a pivot table.
/// </summary>
public record PivotAggregate
{
    /// <summary>Unique field identifier used to reference this aggregate internally.</summary>
    public required string Field { get; init; }
    /// <summary>Human-readable label shown in the pivot column header for this aggregate.</summary>
    public required string DisplayName { get; init; }
    /// <summary>Dot-separated path to the data property this aggregate operates on.</summary>
    public required string PropertyPath { get; init; }
    /// <summary>Type name of the underlying data property.</summary>
    public required string TypeName { get; init; }
    /// <summary>Aggregation function applied to grouped values. Defaults to <see cref="AggregateFunction.Sum"/>.</summary>
    public AggregateFunction Function { get; init; } = AggregateFunction.Sum;
    /// <summary>Optional numeric format string (e.g., "N2", "C") applied to displayed values. Null uses the default rendering.</summary>
    public string? Format { get; init; }
    /// <summary>Horizontal text alignment of aggregate cells. Defaults to <see cref="TextAlign.Right"/>.</summary>
    public TextAlign TextAlign { get; init; } = TextAlign.Right;
    /// <summary>Default sort direction; null means unsorted initially.</summary>
    public SortOrder? SortOrder { get; init; }
}

/// <summary>
/// Specifies the aggregation function applied to grouped values in a pivot aggregate column.
/// </summary>
public enum AggregateFunction
{
    /// <summary>Sums all values in the group.</summary>
    Sum,
    /// <summary>Computes the arithmetic mean of values in the group.</summary>
    Average,
    /// <summary>Counts the number of rows in the group.</summary>
    Count,
    /// <summary>Returns the minimum value in the group.</summary>
    Min,
    /// <summary>Returns the maximum value in the group.</summary>
    Max
}

/// <summary>
/// Specifies the horizontal text alignment for a pivot aggregate cell.
/// </summary>
public enum TextAlign
{
    /// <summary>Align text to the left edge of the cell.</summary>
    Left,
    /// <summary>Center the text within the cell.</summary>
    Center,
    /// <summary>Align text to the right edge of the cell.</summary>
    Right,
    /// <summary>Stretch text to fill the full width of the cell.</summary>
    Justify
}

/// <summary>
/// Specifies the sort direction for a pivot dimension or aggregate.
/// </summary>
public enum SortOrder
{
    /// <summary>Sort values from lowest to highest.</summary>
    Ascending,
    /// <summary>Sort values from highest to lowest.</summary>
    Descending
}
