using MeshWeaver.Charting.Builders.Chart;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Charting.Pivot;


public record PivotWaterfallChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    : PivotChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, WaterfallChart, FloatingBarDataSet>, IPivotWaterfallChartBuilder
    where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
{
    private Func<PivotElementDescriptor, bool> totalsFilter;

    public PivotWaterfallChartBuilder(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        : base(pivotBuilder)
    {
        Chart = new WaterfallChart();
    }

    public IPivotWaterfallChartBuilder WithLegendItems(string incrementsLabel = null, string decrementsLabel = null, string totalLabel = null)
    {
        Chart = Chart.WithLegendItems(incrementsLabel, decrementsLabel, totalLabel);
        return this;
    }

    public IPivotWaterfallChartBuilder WithStylingOptions(Func<WaterfallStylingBuilder, WaterfallStylingBuilder> func)
    {
        Chart = Chart.WithStylingOptions(func);
        return this;
    }

    public IPivotWaterfallChartBuilder WithRangeOptionsBuilder(Func<RangeOptionsBuilder, RangeOptionsBuilder> func)
    {
        Chart = Chart.WithOptions(func);
        return this;
    }

    public IPivotWaterfallChartBuilder WithTotals(Func<PivotElementDescriptor, bool> filter)
    {
        totalsFilter = filter;
        return this;
    }

    public IPivotWaterfallChartBuilder WithConnectors()
    {
        Chart = Chart.WithConnectors();
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
            Chart = Chart.WithDeltas(row.DataByColumns.Select(x => (double)x.Value!))
                                       .WithBarDataSetOptions(o => o.WithBarThickness(20))
                                       .WithLabels(pivotChartModel.ColumnDescriptors.Select(x => x.DisplayName).ToArray());
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

            Chart = Chart.WithTotalsAtPositions(totals);
        }
    }

    protected override void AddOptions(PivotChartModel pivotChartModel)
    {
    }
}

// TODO V10: extract common base to avoid duplication (2023/10/05, Ekaterina Mishina)
public record PivotHorizontalWaterfallChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    : PivotChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, HorizontalWaterfallChart, HorizontalFloatingBarDataSet>, IPivotWaterfallChartBuilder
    where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
{
    private Func<PivotElementDescriptor, bool> totalsFilter;

    public PivotHorizontalWaterfallChartBuilder(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        : base(pivotBuilder)
    {
        Chart = new HorizontalWaterfallChart();
    }

    public IPivotWaterfallChartBuilder WithLegendItems(string incrementsLabel = null, string decrementsLabel = null, string totalLabel = null)
    {
        Chart = Chart.WithLegendItems(incrementsLabel, decrementsLabel, totalLabel);
        return this;
    }

    public IPivotWaterfallChartBuilder WithStylingOptions(Func<WaterfallStylingBuilder, WaterfallStylingBuilder> func)
    {
        Chart = Chart.WithStylingOptions(func);
        return this;
    }

    public IPivotWaterfallChartBuilder WithRangeOptionsBuilder(Func<RangeOptionsBuilder, RangeOptionsBuilder> func)
    {
        Chart = Chart.WithOptions(func);
        return this;
    }

    public IPivotWaterfallChartBuilder WithTotals(Func<PivotElementDescriptor, bool> filter)
    {
        totalsFilter = filter;
        return this;
    }

    public IPivotWaterfallChartBuilder WithConnectors()
    {
        Chart = Chart.WithConnectors();
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
            Chart = Chart.WithDeltas(row.DataByColumns.Select(x => (double)x.Value!))
                                       .WithBarDataSetOptions(o => o.WithBarThickness(20))
                                       .WithLabels(pivotChartModel.ColumnDescriptors.Select(x => x.DisplayName).ToArray());
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

            Chart = Chart.WithTotalsAtPositions(totals);
        }
    }

    protected override void AddOptions(PivotChartModel pivotChartModel)
    {
    }
}
