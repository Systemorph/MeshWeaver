using System.Diagnostics.CodeAnalysis;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.DataSetBuilders;

[SuppressMessage("ReSharper", "WithExpressionModifiesAllMembers")]
public record BubbleDataSetBuilder : DataSetBuilderBase<BubbleDataSetBuilder, BubbleDataSet>, IDataSetWithPointStyleAndOrderBuilder<BubbleDataSetBuilder, BubbleDataSet>
{
    public BubbleDataSetBuilder WithData(IEnumerable<(int x, int y, int radius)> values)
    {
        var valueTuples = values.ToList();
        return WithData(valueTuples.Select(e => (double)e.x), valueTuples.Select(e => (double)e.y), valueTuples.Select(e => (double)e.radius));
    }

    public BubbleDataSetBuilder WithData(IEnumerable<int> x, IEnumerable<int> y, IEnumerable<double> radius)
        => WithData(x.Select(e => (double)e), y.Select(e => (double)e), radius);

    public BubbleDataSetBuilder WithData(IEnumerable<double> x, IEnumerable<int> y, IEnumerable<double> radius)
        => WithData(x.Select(e => e), y.Select(e => (double)e), radius);

    public BubbleDataSetBuilder WithData(IEnumerable<int> x, IEnumerable<double> y, IEnumerable<double> radius)
        => WithData(x.Select(e => (double)e), y.Select(e => e), radius);

    public BubbleDataSetBuilder WithData(IEnumerable<double> x, IEnumerable<double> y, IEnumerable<double> radius)
    {
        var xList = x.ToList();
        var yList = y.ToList();
        var radiusList = radius.ToList();
        if (xList.Count != yList.Count || xList.Count != radiusList.Count)
            throw new InvalidOperationException();

        var pointData = Enumerable.Range(0, xList.Count)
                                  .Select(i => new BubbleData { X = xList[i], Y = yList[i], R = radiusList[i] });
        var dataSet = new BubbleDataSet { Data = pointData };

        return new BubbleDataSetBuilder { DataSet = dataSet };
    }

    public BubbleDataSetBuilder WithPointStyle(Shapes shape)
        => this with { DataSet = DataSet with { PointStyle = shape } };

    public BubbleDataSetBuilder InOrder(int order)
        => this with { DataSet = DataSet with { Order = order } };

    public BubbleDataSetBuilder WithPointRotation(int r) => this with { DataSet = DataSet with { Rotation = r } };
    public BubbleDataSetBuilder WithPointRadius(int r) => this with { DataSet = DataSet with { Radius = r } };
}