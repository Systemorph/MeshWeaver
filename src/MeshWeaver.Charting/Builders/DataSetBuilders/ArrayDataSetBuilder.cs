using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.DataSetBuilders;

public abstract record ArrayDataSetBuilder<TDataSetBuilder, TDataSet> : DataSetBuilderBase<TDataSetBuilder, TDataSet>
    where TDataSet : DataSet, new()
    where TDataSetBuilder : ArrayDataSetBuilder<TDataSetBuilder, TDataSet>
{
    public TDataSetBuilder WithData(IEnumerable<double> rawData)
        => WithData(rawData.Cast<double?>());

    public TDataSetBuilder WithData(IEnumerable<int> rawData)
        => WithData(rawData.Select(x => (double?)x));

    public TDataSetBuilder WithData(IEnumerable<int?> rawData)
        => WithData(rawData.Select(x => (double?)x));

    public TDataSetBuilder WithData(IEnumerable<double?> rawData)
    {
        var data = rawData.Select(x => (object)x);
        var dataSet = new TDataSet { Data = data };

        return (TDataSetBuilder)(this with { DataSet = dataSet });
    }
}