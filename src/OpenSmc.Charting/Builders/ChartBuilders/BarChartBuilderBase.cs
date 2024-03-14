using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Models;
using Systemorph.Charting.Models;

namespace OpenSmc.Charting.Builders.ChartBuilders;

public abstract record BarChartBuilderBase<TBuilder, TDataSet, TOptionsBuilder, TDataSetBuilder> : ArrayChartBuilder<TBuilder, TDataSet, TOptionsBuilder, TDataSetBuilder>
    where TBuilder : BarChartBuilderBase<TBuilder, TDataSet, TOptionsBuilder, TDataSetBuilder>, new()
    where TDataSet : BarDataSet, new()
    where TDataSetBuilder : ArrayDataSetBuilder<TDataSetBuilder, TDataSet>, new()
    where TOptionsBuilder : ArrayOptionsBuilder<TOptionsBuilder>, new()
{
    protected BarChartBuilderBase(Chart chartModel, ArrayOptionsBuilder<TOptionsBuilder> optionsBuilder = null)
        : base(chartModel, optionsBuilder) { }
}