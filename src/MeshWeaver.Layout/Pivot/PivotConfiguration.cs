namespace MeshWeaver.Layout.Pivot;

public record PivotConfiguration
{
    public IReadOnlyCollection<PivotDimension> RowDimensions { get; init; } = [];
    public IReadOnlyCollection<PivotDimension> ColumnDimensions { get; init; } = [];
    public IReadOnlyCollection<PivotAggregate> Aggregates { get; init; } = [];
    public IReadOnlyCollection<PivotDimension> AvailableDimensions { get; init; } = [];
    public IReadOnlyCollection<PivotAggregate> AvailableAggregates { get; init; } = [];
    public bool ShowRowTotals { get; init; } = true;
    public bool ShowColumnTotals { get; init; } = true;
    public bool AllowSorting { get; init; } = true;
    public bool AllowFiltering { get; init; } = true;
    public bool AllowDrillDown { get; init; } = true;
    public bool AllowFieldsPicking { get; init; } = true;
    public bool AllowPaging { get; init; } = true;
    public int PageSize { get; init; } = 50;
}

public record PivotDimension
{
    public required string Field { get; init; }
    public required string DisplayName { get; init; }
    public required string PropertyPath { get; init; }
    public required string TypeName { get; init; }
    public string? Width { get; init; }
    public bool Filterable { get; init; } = true;
    public bool Sortable { get; init; } = true;
    public SortOrder? SortOrder { get; init; }
}

public record PivotAggregate
{
    public required string Field { get; init; }
    public required string DisplayName { get; init; }
    public required string PropertyPath { get; init; }
    public required string TypeName { get; init; }
    public AggregateFunction Function { get; init; } = AggregateFunction.Sum;
    public string? Format { get; init; }
    public TextAlign TextAlign { get; init; } = TextAlign.Right;
    public SortOrder? SortOrder { get; init; }
}

public enum AggregateFunction
{
    Sum,
    Average,
    Count,
    Min,
    Max
}

public enum TextAlign
{
    Left,
    Center,
    Right,
    Justify
}

public enum SortOrder
{
    Ascending,
    Descending
}
