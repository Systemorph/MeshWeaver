using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Bar;

namespace MeshWeaver.Charting.Builders.ChartBuilders;

public abstract record RangeChartBuilder<TChartBuilder, TDataSet, TDataSetBuilder> : ChartBuilderBase<TChartBuilder, TDataSet, RangeOptionsBuilder, TDataSetBuilder>
    where TChartBuilder : ChartBuilderBase<TChartBuilder, TDataSet, RangeOptionsBuilder, TDataSetBuilder>, new()
    where TDataSet : DataSet, IDataSetWithStack, new()
    where TDataSetBuilder : RangeDataSetBuilder<TDataSetBuilder, TDataSet>, new()
{
    protected RangeChartBuilder(Chart chartModel, RangeOptionsBuilder optionsBuilder)
        : base(chartModel, optionsBuilder ?? new RangeOptionsBuilder()) { }

    public TChartBuilder WithDataRange(IEnumerable<int> rawDataFrom, IEnumerable<int> rawDataTo)
        => WithDataSet(b => b.WithDataRange(rawDataFrom, rawDataTo));

    public TChartBuilder WithDataRange(IEnumerable<double> rawDataFrom, IEnumerable<int> rawDataTo)
        => WithDataSet(b => b.WithDataRange(rawDataFrom, rawDataTo));

    public TChartBuilder WithDataRange(IEnumerable<int> rawDataFrom, IEnumerable<double> rawDataTo)
        => WithDataSet(b => b.WithDataRange(rawDataFrom, rawDataTo));

    public TChartBuilder WithDataRange(IEnumerable<double> rawDataFrom, IEnumerable<double> rawDataTo)
        => WithDataSet(b => b.WithDataRange(rawDataFrom, rawDataTo));
    
    public TChartBuilder WithDataRange(IEnumerable<int?> rawDataFrom, IEnumerable<int?> rawDataTo)
        => WithDataSet(b => b.WithDataRange(rawDataFrom, rawDataTo));

    public TChartBuilder WithDataRange(IEnumerable<double?> rawDataFrom, IEnumerable<int?> rawDataTo)
        => WithDataSet(b => b.WithDataRange(rawDataFrom, rawDataTo));

    public TChartBuilder WithDataRange(IEnumerable<int?> rawDataFrom, IEnumerable<double?> rawDataTo)
        => WithDataSet(b => b.WithDataRange(rawDataFrom, rawDataTo));

    public TChartBuilder WithDataRange(IEnumerable<double?> rawDataFrom, IEnumerable<double?> rawDataTo)
        => WithDataSet(b => b.WithDataRange(rawDataFrom, rawDataTo));

    public TChartBuilder WithDataRange(IEnumerable<double[]> rangeData, string label = null, Func<TDataSetBuilder, TDataSetBuilder> func = null, string stack = null)
        => WithDataRangeCore(rangeData, label, func, stack);
    public TChartBuilder WithDataRange(IEnumerable<double?[]> rangeData, string label = null, Func<TDataSetBuilder, TDataSetBuilder> func = null, string stack = null)
        => WithDataRangeCore(rangeData, label, func, stack);
    public TChartBuilder WithDataRange(IEnumerable<int[]> rangeData, string label = null, Func<TDataSetBuilder, TDataSetBuilder> func = null, string stack = null)
        => WithDataRangeCore(rangeData, label, func, stack);
    public TChartBuilder WithDataRange(IEnumerable<int?[]> rangeData, string label = null, Func<TDataSetBuilder, TDataSetBuilder> func = null, string stack = null)
        => WithDataRangeCore(rangeData, label, func, stack);
    public TChartBuilder WithDataRange(IEnumerable<WaterfallBar> rangeData, string label = null, Func<TDataSetBuilder, TDataSetBuilder> func = null, string stack = null)
        => WithDataRangeCore(rangeData, label, func, stack);

    private TChartBuilder WithDataRangeCore(IEnumerable<object> rangeData, string label = null, Func<TDataSetBuilder, TDataSetBuilder> func = null, string stack = null)
        => WithDataSet(b =>
                       {
                           var builder = b.WithDataRange(rangeData);
                           if (!String.IsNullOrWhiteSpace(label))
                               builder = builder.WithLabel(label);
                           if (!String.IsNullOrEmpty(stack))
                               builder = builder.WithStack(stack);
                           if (func != null)
                               builder = func(builder);
                           return builder;
                       });
}