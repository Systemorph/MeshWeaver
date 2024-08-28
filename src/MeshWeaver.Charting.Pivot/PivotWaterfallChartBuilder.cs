using System.Collections.Immutable;
using MeshWeaver.Charting.Builders;
using MeshWeaver.Charting.Builders.Chart;
using MeshWeaver.Charting.Builders.Options;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Charting.Pivot;


public record PivotWaterfallChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    : PivotChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, BarChart, BarDataSet>, IPivotWaterfallChartBuilder
    where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
{
    private Func<PivotElementDescriptor, bool> totalsFilter;

    private List<double> deltas = [];
    private ImmutableList<Func<WaterfallChartOptions, WaterfallChartOptions>> WaterfallOptions { get; init; } = [];
    private ImmutableList<Func<WaterfallChartOptions, WaterfallChartOptions>> ExtraWaterfallOptions { get; set; } = [];

    public PivotWaterfallChartBuilder(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        : base(pivotBuilder)
    {
        Chart = Charts.Bar([]);
    }

    private IPivotWaterfallChartBuilder WithWaterfallOptions(Func<WaterfallChartOptions, WaterfallChartOptions> option)
        => this with { WaterfallOptions = WaterfallOptions.Add(option), };

    public IPivotWaterfallChartBuilder WithLegendItems(string incrementsLabel = null, string decrementsLabel = null, string totalLabel = null)
        => WithWaterfallOptions(w => w.WithLegendItems(incrementsLabel, decrementsLabel, totalLabel));

    public IPivotWaterfallChartBuilder WithStylingOptions(Func<WaterfallStylingBuilder, WaterfallStylingBuilder> func)
        => WithWaterfallOptions(w => w.WithStylingOptions(func));

    public IPivotWaterfallChartBuilder WithRangeOptionsBuilder(Func<ChartOptions, ChartOptions> func)
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
        => WithWaterfallOptions(w => w.WithConnectors());

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
        {
            // TODO V10: reconsider to get rid of this "mutability" approach here (2024/08/28, Dmitry Kalabin)
            deltas = row.DataByColumns.Select(x => (double)x.Value!).ToList();
            ExtraWaterfallOptions = ExtraWaterfallOptions.Add(w => w.WithBarDataSetOptions(o => o.WithBarThickness(20)));
            Chart = Chart.WithLabels(pivotChartModel.ColumnDescriptors.Select(x => x.DisplayName).ToArray());
        }
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

            // TODO V10: reconsider to get rid of this "mutability" approach here (2024/08/28, Dmitry Kalabin)
            ExtraWaterfallOptions = ExtraWaterfallOptions.Add(w => w.WithTotalsAtPositions(totals));
        }
    }

    protected override void ApplyCustomChartConfigs()
    {
        base.ApplyCustomChartConfigs();
        Chart = Chart.ToWaterfallChart(deltas, o => WaterfallOptions.Concat(ExtraWaterfallOptions).Aggregate(o, (x, modifier) => modifier(x)));
    }

    protected override void AddOptions(PivotChartModel pivotChartModel)
    {
    }
}

// TODO V10: extract common base to avoid duplication (2023/10/05, Ekaterina Mishina)
public record PivotHorizontalWaterfallChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    : PivotChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, BarChart, BarDataSet>, IPivotWaterfallChartBuilder
    where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
{
    private Func<PivotElementDescriptor, bool> totalsFilter;

    private List<double> deltas = [];
    private ImmutableList<Func<HorizontalWaterfallChartOptions, HorizontalWaterfallChartOptions>> WaterfallOptions { get; init; } = [];
    private ImmutableList<Func<HorizontalWaterfallChartOptions, HorizontalWaterfallChartOptions>> ExtraWaterfallOptions { get; set; } = [];

    public PivotHorizontalWaterfallChartBuilder(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        : base(pivotBuilder)
    {
        Chart = Charts.Bar([]);
    }

    private IPivotWaterfallChartBuilder WithWaterfallOptions(Func<HorizontalWaterfallChartOptions, HorizontalWaterfallChartOptions> option)
        => this with { WaterfallOptions = WaterfallOptions.Add(option), };

    public IPivotWaterfallChartBuilder WithLegendItems(string incrementsLabel = null, string decrementsLabel = null, string totalLabel = null)
        => WithWaterfallOptions(w => w.WithLegendItems(incrementsLabel, decrementsLabel, totalLabel));

    public IPivotWaterfallChartBuilder WithStylingOptions(Func<WaterfallStylingBuilder, WaterfallStylingBuilder> func)
        => WithWaterfallOptions(w => w.WithStylingOptions(func));

    public IPivotWaterfallChartBuilder WithRangeOptionsBuilder(Func<ChartOptions, ChartOptions> func)
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
        => WithWaterfallOptions(w => w.WithConnectors());

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
        {
            // TODO V10: reconsider to get rid of this "mutability" approach here (2024/08/28, Dmitry Kalabin)
            deltas = row.DataByColumns.Select(x => (double)x.Value!).ToList();
            ExtraWaterfallOptions = ExtraWaterfallOptions.Add(w => w.WithBarDataSetOptions(o => o.WithBarThickness(20)));
            Chart = Chart.WithLabels(pivotChartModel.ColumnDescriptors.Select(x => x.DisplayName).ToArray());
        }
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

            // TODO V10: reconsider to get rid of this "mutability" approach here (2024/08/28, Dmitry Kalabin)
            ExtraWaterfallOptions = ExtraWaterfallOptions.Add(w => w.WithTotalsAtPositions(totals));
        }
    }

    protected override void ApplyCustomChartConfigs()
    {
        base.ApplyCustomChartConfigs();
        Chart = Chart.ToHorizontalWaterfallChart(deltas, o => WaterfallOptions.Concat(ExtraWaterfallOptions).Aggregate(o, (x, modifier) => modifier(x)));
    }

    protected override void AddOptions(PivotChartModel pivotChartModel)
    {
    }
}
