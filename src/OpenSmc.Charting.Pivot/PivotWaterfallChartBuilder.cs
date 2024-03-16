using OpenSmc.Charting.Builders.ChartBuilders;
using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Enums;
using OpenSmc.Pivot.Builder;
using OpenSmc.Pivot.Models;
using Systemorph.Charting.Models;

namespace OpenSmc.Charting.Pivot;


public record PivotWaterfallChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    : PivotChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, WaterfallChartBuilder, FloatingBarDataSet, RangeOptionsBuilder, FloatingBarDataSetBuilder>, IPivotWaterfallChartBuilder
    where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
{
    private Func<PivotElementDescriptor, bool> totalsFilter;

    public PivotWaterfallChartBuilder(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        : base(pivotBuilder)
    {
        ChartBuilder = new WaterfallChartBuilder();
    }

    public IPivotWaterfallChartBuilder WithLegendItems(string incrementsLabel = null, string decrementsLabel = null, string totalLabel = null)
    {
        ChartBuilder = ChartBuilder.WithLegendItems(incrementsLabel, decrementsLabel, totalLabel);
        return this;
    }

    public IPivotWaterfallChartBuilder WithStylingOptions(Func<WaterfallStylingBuilder, WaterfallStylingBuilder> func)
    {
        ChartBuilder = ChartBuilder.WithStylingOptions(func);
        return this;
    }

    public IPivotWaterfallChartBuilder WithRangeOptionsBuilder(Func<RangeOptionsBuilder, RangeOptionsBuilder> func)
    {
        ChartBuilder = ChartBuilder.WithOptions(func);
        return this;
    }

    public IPivotWaterfallChartBuilder WithTotals(Func<PivotElementDescriptor, bool> filter)
    {
        totalsFilter = filter;
        return this;
    }

    public IPivotWaterfallChartBuilder WithConnectors()
    {
        ChartBuilder = ChartBuilder.WithConnectors();
        return this;
    }

    protected override PivotChartModel CreatePivotModel(PivotModel pivotModel)
    {
        return PivotChartModelBuilder.BuildFromPivotModel(pivotModel, ChartType.Bar, totalsFilter != null);
    }

    protected override void AddDataSets(PivotChartModel pivotChartModel)
    {
        if (pivotChartModel.Rows.Count != 1)
            throw new InvalidOperationException("There can be only 1 row");

        var row = pivotChartModel.Rows.Single();

        if (row.DataSetType == ChartType.Bar)
            ChartBuilder = ChartBuilder.WithDeltas(row.DataByColumns.Select(x => (double)x.Value!))
                                       .WithBarDataSetOptions(o => o.WithBarThickness(20))
                                       .WithLabels(pivotChartModel.ColumnDescriptors.Select(x => x.DisplayName));
        else
            throw new NotImplementedException("Only bar data set types are supported");

        if (totalsFilter != null)
        {
            var totals = new List<int>();
            var i = 0;
            foreach (var column in pivotChartModel.ColumnDescriptors)
            {
                if (totalsFilter(column))
                    totals.Add(i);
                i++;
            }

            ChartBuilder = ChartBuilder.WithTotalsAtPositions(totals);
        }
    }

    protected override void AddOptions(PivotChartModel pivotChartModel)
    {
    }
}

// TODO V10: extract common base to avoid duplication (2023/10/05, Ekaterina Mishina)
public record PivotHorizontalWaterfallChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    : PivotChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, HorizontalWaterfallChartBuilder, HorizontalFloatingBarDataSet, RangeOptionsBuilder, HorizontalFloatingBarDataSetBuilder>, IPivotWaterfallChartBuilder
    where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
{
    private Func<PivotElementDescriptor, bool> totalsFilter;

    public PivotHorizontalWaterfallChartBuilder(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        : base(pivotBuilder)
    {
        ChartBuilder = new HorizontalWaterfallChartBuilder();
    }

    public IPivotWaterfallChartBuilder WithLegendItems(string incrementsLabel = null, string decrementsLabel = null, string totalLabel = null)
    {
        ChartBuilder = ChartBuilder.WithLegendItems(incrementsLabel, decrementsLabel, totalLabel);
        return this;
    }

    public IPivotWaterfallChartBuilder WithStylingOptions(Func<WaterfallStylingBuilder, WaterfallStylingBuilder> func)
    {
        ChartBuilder = ChartBuilder.WithStylingOptions(func);
        return this;
    }

    public IPivotWaterfallChartBuilder WithRangeOptionsBuilder(Func<RangeOptionsBuilder, RangeOptionsBuilder> func)
    {
        ChartBuilder = ChartBuilder.WithOptions(func);
        return this;
    }

    public IPivotWaterfallChartBuilder WithTotals(Func<PivotElementDescriptor, bool> filter)
    {
        totalsFilter = filter;
        return this;
    }

    public IPivotWaterfallChartBuilder WithConnectors()
    {
        ChartBuilder = ChartBuilder.WithConnectors();
        return this;
    }

    protected override PivotChartModel CreatePivotModel(PivotModel pivotModel)
    {
        return PivotChartModelBuilder.BuildFromPivotModel(pivotModel, ChartType.Bar, totalsFilter != null);
    }

    protected override void AddDataSets(PivotChartModel pivotChartModel)
    {
        if (pivotChartModel.Rows.Count != 1)
            throw new InvalidOperationException("There can be only 1 row");

        var row = pivotChartModel.Rows.Single();

        if (row.DataSetType == ChartType.Bar)
            ChartBuilder = ChartBuilder.WithDeltas(row.DataByColumns.Select(x => (double)x.Value!))
                                       .WithBarDataSetOptions(o => o.WithBarThickness(20))
                                       .WithLabels(pivotChartModel.ColumnDescriptors.Select(x => x.DisplayName));
        else
            throw new NotImplementedException("Only bar data set types are supported");

        if (totalsFilter != null)
        {
            var totals = new List<int>();
            var i = 0;
            foreach (var column in pivotChartModel.ColumnDescriptors)
            {
                if (totalsFilter(column))
                    totals.Add(i);
                i++;
            }

            ChartBuilder = ChartBuilder.WithTotalsAtPositions(totals);
        }
    }

    protected override void AddOptions(PivotChartModel pivotChartModel)
    {
    }
}