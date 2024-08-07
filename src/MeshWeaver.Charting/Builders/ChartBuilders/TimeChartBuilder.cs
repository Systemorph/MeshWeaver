using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.ChartBuilders;

public record TimeChartBuilder : ChartBuilderBase<TimeChartBuilder, TimeLineDataSet, TimeOptionsBuilder, TimeLineDataSetBuilder>
{
    public TimeChartBuilder(Chart chartModel = null, TimeOptionsBuilder optionsBuilder = null)
        : base(chartModel ?? new Chart(ChartType.Line), optionsBuilder ?? new TimeOptionsBuilder()) { }

    public TimeChartBuilder() : this(new Chart(ChartType.Line)) { }

    public TimeChartBuilder WithData(IEnumerable<DateTime> dates, IEnumerable<int> rawData) => WithDataSet(b => b.WithData(dates, rawData));
    public TimeChartBuilder WithData(IEnumerable<string> times, IEnumerable<double> rawData) => WithDataSet(b => b.WithData(times, rawData));
    public TimeChartBuilder WithData(IEnumerable<string> times, IEnumerable<int> rawData) => WithDataSet(b => b.WithData(times, rawData));
    public TimeChartBuilder WithData(IEnumerable<DateTime> dates, IEnumerable<double> rawData) => WithDataSet(b => b.WithData(dates, rawData));
}