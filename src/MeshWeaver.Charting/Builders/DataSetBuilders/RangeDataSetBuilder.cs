using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Bar;

namespace MeshWeaver.Charting.Builders.DataSetBuilders;

public abstract record RangeDataSetBuilder<TDataSetBuilder, TDataSet> : DataSetBuilderBase<TDataSetBuilder, TDataSet>
    where TDataSet : DataSet, IDataSetWithStack, new()
    where TDataSetBuilder : RangeDataSetBuilder<TDataSetBuilder, TDataSet>
{
    public TDataSetBuilder WithDataRange(IEnumerable<int> rawDataFrom, IEnumerable<int> rawDataTo)
        => WithDataRange(rawDataFrom.Select(x => (double?)x), rawDataTo.Select(x => (double?)x));

    public TDataSetBuilder WithDataRange(IEnumerable<double> rawDataFrom, IEnumerable<int> rawDataTo)
        => WithDataRange(rawDataFrom.Select(x => (double?)x), rawDataTo.Select(x => (double?)x));

    public TDataSetBuilder WithDataRange(IEnumerable<int> rawDataFrom, IEnumerable<double> rawDataTo)
        => WithDataRange(rawDataFrom.Select(x => (double?)x), rawDataTo.Select(x => (double?)x));

    public TDataSetBuilder WithDataRange(IEnumerable<double> rawDataFrom, IEnumerable<double> rawDataTo)
        => WithDataRange(rawDataFrom.Select(x => (double?)x), rawDataTo.Select(x => (double?)x));

    public TDataSetBuilder WithDataRange(IEnumerable<int?> rawDataFrom, IEnumerable<int?> rawDataTo)
        => WithDataRange(rawDataFrom.Select(x => (double?)x), rawDataTo.Select(x => (double?)x));

    public TDataSetBuilder WithDataRange(IEnumerable<double?> rawDataFrom, IEnumerable<int?> rawDataTo)
        => WithDataRange(rawDataFrom.Select(x => x), rawDataTo.Select(x => (double?)x));

    public TDataSetBuilder WithDataRange(IEnumerable<int?> rawDataFrom, IEnumerable<double?> rawDataTo)
        => WithDataRange(rawDataFrom.Select(x => (double?)x), rawDataTo.Select(x => x));

    public TDataSetBuilder WithDataRange(IEnumerable<double?> rawDataFrom, IEnumerable<double?> rawDataTo)
    {
        var rangeData = rawDataFrom.Zip(rawDataTo, (from, to) => new[] { from, to });
        var dataSet = new TDataSet { Data = rangeData };
        return (TDataSetBuilder)(this with { DataSet = dataSet });
    }

    public TDataSetBuilder WithDataRange(IEnumerable<int[]> rangeData, string label = null, Func<TDataSetBuilder, TDataSetBuilder> func = null, string stack = null)
        => WithDataRangeCore(rangeData, label, func, stack);

    public TDataSetBuilder WithDataRange(IEnumerable<WaterfallBar> rangeData, string label = null, Func<TDataSetBuilder, TDataSetBuilder> func = null, string stack = null)
        => WithDataRangeCore(rangeData, label, func, stack);

    private TDataSetBuilder WithDataRangeCore(IEnumerable<object> rangeData, string label = null, Func<TDataSetBuilder, TDataSetBuilder> func = null, string stack = null)
    {
        var builder = WithDataRange(rangeData);
        if (!string.IsNullOrWhiteSpace(label))
            builder = builder.WithLabel(label);
        if (!string.IsNullOrEmpty(stack))
            builder = builder.WithStack(stack);
        if (func != null)
            builder = func(builder);
        return builder;
    }

    public TDataSetBuilder WithDataRange(IEnumerable<object> rangeData)
    {
        var dataSet = new TDataSet { Data = rangeData };
        return (TDataSetBuilder)(this with { DataSet = dataSet });
    }

    public TDataSetBuilder WithStack(string stack)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { Stack = stack } });

}
