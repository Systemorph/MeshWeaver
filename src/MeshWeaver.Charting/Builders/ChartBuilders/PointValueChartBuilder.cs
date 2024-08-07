using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.ChartBuilders;

public record PointValueChartBuilder : ChartBuilderBase<PointValueChartBuilder, BubbleDataSet, PointValueOptionsBuilder, BubbleDataSetBuilder>
{
    public PointValueChartBuilder(Chart chartModel = null, PointValueOptionsBuilder optionsBuilder = null)
        : base(chartModel ?? new Chart(ChartType.Bubble), optionsBuilder ?? new PointValueOptionsBuilder()) { }

    public PointValueChartBuilder() : this(new Chart(ChartType.Bubble)) { }

    public PointValueChartBuilder WithData(IEnumerable<(int x, int y, int radius)> values, Func<BubbleDataSet, BubbleDataSet> dataSetModifier = null)
    {
        var valueTuples = values.ToList();
        return WithData(valueTuples.Select(e => (double)e.x), valueTuples.Select(e => (double)e.y), valueTuples.Select(e => (double)e.radius), dataSetModifier);
    }

    public PointValueChartBuilder WithData(IEnumerable<int> x, IEnumerable<int> y, IEnumerable<double> radius, Func<BubbleDataSet, BubbleDataSet> dataSetModifier = null) => WithData(x.Select(e => (double)e), y.Select(e => (double)e), radius, dataSetModifier);

    public PointValueChartBuilder WithData(IEnumerable<double> x, IEnumerable<int> y, IEnumerable<double> radius, Func<BubbleDataSet, BubbleDataSet> dataSetModifier = null) => WithData(x.Select(e => e), y.Select(e => (double)e), radius, dataSetModifier);

    public PointValueChartBuilder WithData(IEnumerable<int> x, IEnumerable<double> y, IEnumerable<double> radius, Func<BubbleDataSet, BubbleDataSet> dataSetModifier = null) => WithData(x.Select(e => (double)e), y.Select(e => e), radius, dataSetModifier);

    public PointValueChartBuilder WithData(IEnumerable<double> x, IEnumerable<double> y, IEnumerable<double> radius, Func<BubbleDataSet, BubbleDataSet> dataSetModifier = null)
    {
        var xList = x.ToList();
        var yList = y.ToList();
        var radiusList = radius.ToList();
        if (xList.Count != yList.Count || xList.Count != radiusList.Count)
            throw new InvalidOperationException();

        var newData = ChartModel.Data ?? new ChartData();
        var pointData = Enumerable.Range(0, xList.Count)
                                  .Select(i => new BubbleData { X = xList[i], Y = yList[i], R = radiusList[i] });
        var dataSet = new BubbleDataSet { Data = pointData };
        if (dataSetModifier != null)
            dataSet = dataSetModifier(dataSet);
        return this with { ChartModel = ChartModel with { Data = newData.WithDataSets(dataSet) } };
    }
}
