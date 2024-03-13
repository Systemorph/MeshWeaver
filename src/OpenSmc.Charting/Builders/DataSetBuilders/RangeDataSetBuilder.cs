using OpenSmc.Charting.Models;

namespace OpenSmc.Charting.Builders.DataSetBuilders;

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

    public TDataSetBuilder WithDataRange(IEnumerable<object> rangeData)
    {
        var dataSet = new TDataSet { Data = rangeData };
        return (TDataSetBuilder)(this with { DataSet = dataSet });
    }

    public TDataSetBuilder WithStack(string stack)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { Stack = stack } });

}