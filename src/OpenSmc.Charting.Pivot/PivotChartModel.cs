using OpenSmc.Charting.Enums;

namespace OpenSmc.Charting.Pivot;

public record PivotChartModel
{
    public HashSet<string> ColumnGroupings { get; init; } = new();
    public HashSet<string> RowGroupings { get; init; } = new();
    public IList<PivotElementDescriptor> ColumnDescriptors { get; set; } = new List<PivotElementDescriptor>();
    public IList<PivotChartRow> Rows { get; init; } = new List<PivotChartRow>();
}

public record PivotChartRow
{
    public ChartType DataSetType { get; set; }
    public string Stack { get; set; }
    public double? SmoothingCoefficient { get; set; }
    public bool Filled { get; set; }
    public PivotElementDescriptor Descriptor { get; init; }
    public IList<(string ColSystemName, double? Value)> DataByColumns { get; init; } =  new List<(string ColSystemName, double? Value)>();
}

public record PivotElementDescriptor
{
    public string SystemName { get; init; }
    public string DisplayName { get; init; }
    public IList<(string SystemName, string DisplayName, string GrouperName)> Coordinates { get; set; } = new List<(string SystemName, string DisplayName, string GrouperName)>();
}
