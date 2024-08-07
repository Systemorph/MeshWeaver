using System.Diagnostics.CodeAnalysis;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.DataSetBuilders;

[SuppressMessage("ReSharper", "WithExpressionModifiesAllMembers")]
public record LineScatterDataSetBuilder : LineDataSetBuilderBase<LineScatterDataSetBuilder, LineScatterDataSet>
{
    public LineScatterDataSetBuilder WithDataPoint(IEnumerable<double> x, IEnumerable<int> y) => WithDataPoint(x, y.Select(v => (double)v));
    public LineScatterDataSetBuilder WithDataPoint(IEnumerable<int> x, IEnumerable<double> y) => WithDataPoint(x.Select(v => (double)v), y);
    public LineScatterDataSetBuilder WithDataPoint(IEnumerable<int> x, IEnumerable<int> y) => WithDataPoint(x.Select(v => (double)v), y.Select(v => (double)v));

    public LineScatterDataSetBuilder WithDataPoint(IEnumerable<double> x, IEnumerable<double> y)
    {
        var xList = x.ToList();
        var yList = y.ToList();
        if (xList.Count != yList.Count)
            throw new InvalidOperationException();

        var pointData = xList.Zip(yList, (a, v) => new PointData { X = a, Y = v });

        var dataSet = new LineScatterDataSet { Data = pointData };
        return this with { DataSet = dataSet };
    }

    public LineScatterDataSetBuilder WithDataPoint(IEnumerable<(int x, int y)> points) => WithDataPoint(points.Select(p => ((double)p.x, (double)p.y)));
    public LineScatterDataSetBuilder WithDataPoint(IEnumerable<(int x, double y)> points) => WithDataPoint(points.Select(p => ((double)p.x, p.y)));
    public LineScatterDataSetBuilder WithDataPoint(IEnumerable<(double x, int y)> points) => WithDataPoint(points.Select(p => (p.x, (double)p.y)));

    public LineScatterDataSetBuilder WithDataPoint(IEnumerable<(double x, double y)> points)
    {
        var dataSets = new LineScatterDataSet { Data = points.Select(p => new PointData { X = p.x, Y = p.y }) };
        return this with { DataSet = dataSets };
    }
}