using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Models;

namespace OpenSmc.Charting.Builders.ChartBuilders;

public abstract record ArrayChartBuilder<TBuilder, TDataSet, TOptionsBuilder, TDataSetBuilder> 
    : ChartBuilderBase<TBuilder, TDataSet, TOptionsBuilder, TDataSetBuilder>
    where TBuilder : ArrayChartBuilder<TBuilder, TDataSet, TOptionsBuilder, TDataSetBuilder>, new()
    where TDataSet : DataSet, new()
    where TDataSetBuilder : ArrayDataSetBuilder<TDataSetBuilder, TDataSet>, new()
    where TOptionsBuilder : ArrayOptionsBuilder<TOptionsBuilder>, new()
{
    protected ArrayChartBuilder(Chart chartModel, ArrayOptionsBuilder<TOptionsBuilder> optionsBuilder = null)
        : base(chartModel, (TOptionsBuilder)(optionsBuilder ?? new TOptionsBuilder())) { }

    public override Chart ToChart()
    {
        var chart = base.ToChart();

        // if (chart.Data.Labels is null)
        // {
        //     var maxLen = chart.Data.DataSets.Select(ds => ds.Data?.Count() ?? 0).DefaultIfEmpty(1).Max();
        //
        //     chart = chart with
        //                      {
        //                          Data = chart.Data with
        //                                 {
        //                                     Labels = Enumerable.Range(1, maxLen).Select(i => i.ToString())
        //                                 }
        //                      };
        // }

        return chart;
    }

    public TBuilder WithData(IEnumerable<double> rawData)
        => WithDataSet(d => d.WithData(rawData));

    public TBuilder WithData(IEnumerable<int> rawData)
        => WithDataSet(d => d.WithData(rawData));

    public TBuilder WithData(IEnumerable<int?> rawData)
        => WithDataSet(d => d.WithData(rawData));

    public TBuilder WithData(IEnumerable<double?> rawData)
        => WithDataSet(d => d.WithData(rawData));
}
