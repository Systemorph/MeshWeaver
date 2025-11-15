namespace MeshWeaver.GridModel;

public record PivotConfiguration
{
    public IReadOnlyCollection<PivotDimension> RowDimensions { get; init; } = [];
    public IReadOnlyCollection<PivotDimension> ColumnDimensions { get; init; } = [];
    public IReadOnlyCollection<PivotAggregate> Aggregates { get; init; } = [];
    public bool ShowRowTotals { get; init; } = true;
    public bool ShowColumnTotals { get; init; } = true;
}

public record PivotDimension
{
    public required string Field { get; init; }
    public required string DisplayName { get; init; }
    public required string PropertyPath { get; init; }
}

public record PivotAggregate
{
    public required string Field { get; init; }
    public required string DisplayName { get; init; }
    public required string PropertyPath { get; init; }
    public AggregateFunction Function { get; init; } = AggregateFunction.Sum;
    public string? Format { get; init; }
}

public enum AggregateFunction
{
    Sum,
    Average,
    Count,
    Min,
    Max
}
