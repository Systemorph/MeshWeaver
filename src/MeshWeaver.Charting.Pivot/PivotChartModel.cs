using System.Collections.Immutable;
using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Pivot;

public record PivotChartModel
{
    public HashSet<object> ColumnGroupings { get; init; } = new();
    public HashSet<object> RowGroupings { get; init; } = new();
    public IList<PivotElementDescriptor> ColumnDescriptors { get; set; } =
        new List<PivotElementDescriptor>();
    public IList<PivotChartRow> Rows { get; init; } = new List<PivotChartRow>();
}

public record PivotChartRow
{
    public ChartType DataSetType { get; set; }
    public object Stack { get; set; } = null!;
    public double? SmoothingCoefficient { get; set; }
    public bool Filled { get; set; }
    public PivotElementDescriptor Descriptor { get; init; } = null!;
    public List<(object ColSystemName, double? Value)> DataByColumns { get; init; } = new();
}

public record PivotElementDescriptor
{
    public object Id { get; init; } = null!;
    public string DisplayName { get; init; } = null!;
    public List<(object Id, string DisplayName, object GrouperName)> Coordinates { get; set; } =
        new();
}
