using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.ChartBuilders;

public record PointChartBuilder : ChartBuilderBase<PointChartBuilder, LineScatterDataSet, PointOptionsBuilder, LineScatterDataSetBuilder>
{
    public PointChartBuilder(Chart chartModel = null, PointOptionsBuilder optionsBuilder = null)
        : base(chartModel ?? new Chart(ChartType.Scatter), optionsBuilder ?? new PointOptionsBuilder()) { }

    public PointChartBuilder() : this(new Chart(ChartType.Scatter)) { }

    public PointChartBuilder WithDataPoint(IEnumerable<double> x, IEnumerable<int> y) => WithDataSet(b => b.WithDataPoint(x, y));
    public PointChartBuilder WithDataPoint(IEnumerable<int> x, IEnumerable<double> y) => WithDataSet(b => b.WithDataPoint(x, y));
    public PointChartBuilder WithDataPoint(IEnumerable<int> x, IEnumerable<int> y) => WithDataSet(b => b.WithDataPoint(x, y));
    public PointChartBuilder WithDataPoint(IEnumerable<double> x, IEnumerable<double> y) => WithDataSet(b => b.WithDataPoint(x, y));

    public PointChartBuilder WithDataPoint(IEnumerable<(int x, int y)> points) => WithDataSet(b => b.WithDataPoint(points));
    public PointChartBuilder WithDataPoint(IEnumerable<(int x, double y)> points) => WithDataSet(b => b.WithDataPoint(points));
    public PointChartBuilder WithDataPoint(IEnumerable<(double x, int y)> points) => WithDataSet(b => b.WithDataPoint(points));
    public PointChartBuilder WithDataPoint(IEnumerable<(double x, double y)> points) => WithDataSet(b => b.WithDataPoint(points));

    public override PointChartBuilder WithLabels(params string[] names) => DataSets.Count <= names.Length ? this with
    {
        DataSets = DataSets.Select((ds, i) => ds with
        {
            Label = names[i]
        })
                           .ToList()
    } : throw new Exception("Provided fewer labels than data sets");

    public override PointChartBuilder WithLabels(IEnumerable<string> names) => WithLabels(names as string[] ?? names.ToArray());

}