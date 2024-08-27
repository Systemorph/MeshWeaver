using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Helpers;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Bar;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Models.Options.Scales;
using MeshWeaver.Charting.Models.Options.Tooltips;
using MeshWeaver.Utils;

namespace MeshWeaver.Charting.Builders.Chart;

public record FloatingBarChart
    : RangeChart<FloatingBarChart, FloatingBarDataSet>
{
    public FloatingBarChart(IReadOnlyCollection<FloatingBarDataSet> dataSets) : base(dataSets, ChartType.Bar) { }
}

public record HorizontalFloatingBarChart
    : RangeChart<HorizontalFloatingBarChart, HorizontalFloatingBarDataSet>
{
    public HorizontalFloatingBarChart(IReadOnlyCollection<HorizontalFloatingBarDataSet> dataSets)
        : base(dataSets, ChartType.Bar)
    {
        Options = Options.WithIndexAxis("y");
    }
}

public abstract record WaterfallChartBase<TChart, TDataSet, TDataSetBuilder> : RangeChart<TChart, TDataSet>
    where TChart : WaterfallChartBase<TChart, TDataSet, TDataSetBuilder>
    where TDataSet : BarDataSetBase, IDataSetWithStack, new()
    where TDataSetBuilder : FloatingBarDataSetBuilderBase<TDataSetBuilder, TDataSet>, new()
{
    protected WaterfallChartBase(IReadOnlyCollection<TDataSet> dataSets, ChartType chartType)
        : base(dataSets, chartType)
    {
    }

    private List<double> deltas;
    private readonly HashSet<int> totalIndexes = new();
    private string incrementsLabel = ChartConst.Hidden;
    private string decrementsLabel = ChartConst.Hidden;
    private string totalLabel = ChartConst.Hidden;
    private bool datasetsReady;
    private bool includeConnectors;
    private Func<LineDataSetBuilder, LineDataSetBuilder> connectorDataSetModifier = d => d.ThinLine();
    private Func<TDataSetBuilder, TDataSetBuilder> barDataSetModifier;
    private WaterfallStylingBuilder StylingBuilder { get; set; } = new();

    public override Models.Chart ToChart()
    {
        if (datasetsReady)
            return base.ToChart();
        var styling = StylingBuilder.Build();

        var tmp = this;
        if (Data.Labels is null)
            tmp = tmp.WithLabels(Enumerable.Range(1, deltas.Count).Select(i => i.ToString()).ToArray());

        var labels = Data.Labels?.ToArray();

        var dataModel = deltas.CalculateModel(labels, totalIndexes);

        datasetsReady = true;

        var incrementRanges = dataModel.IncrementRanges.Select(d => new IncrementBar(d.range, d.label, d.delta, styling)).ToList();
        var decrementRanges = dataModel.DecrementRanges.Select(d => new DecrementBar(d.range, d.label, d.delta, styling)).ToList();
        var totalRanges = dataModel.TotalRanges.Select(d => new TotalBar(d.range, d.label, d.delta, styling)).ToList();

        var dataset1 = (TDataSet)new TDataSetBuilder()
            .WithDataRange(incrementRanges, incrementsLabel, dsb => (barDataSetModifier is null ? dsb : barDataSetModifier(dsb))
                                                                .WithParsing()
                                                                //.WithBarThickness("flex")
                                                                .WithBackgroundColor(ChartColor.FromHexString(styling.IncrementColor))
                                                                .WithHoverBackgroundColor(ChartColor.FromHexString(styling.IncrementColor)))
            .Build();
        var dataset2 = (TDataSet)new TDataSetBuilder()
            .WithDataRange(decrementRanges, decrementsLabel, dsb => (barDataSetModifier is null ? dsb : barDataSetModifier(dsb))
                                                                .WithParsing()
                                                                //.WithBarThickness("flex")
                                                                .WithBackgroundColor(ChartColor.FromHexString(styling.DecrementColor))
                                                                .WithHoverBackgroundColor(ChartColor.FromHexString(styling.DecrementColor)))
            .Build();
        var dataset3 = (TDataSet)new TDataSetBuilder()
            .WithDataRange(totalRanges, totalLabel, dsb => (barDataSetModifier is null ? dsb : barDataSetModifier(dsb))
                                                                .WithParsing()
                                                                //.WithBarThickness("flex")
                                                                .WithBackgroundColor(ChartColor.FromHexString(styling.TotalColor))
                                                                .WithHoverBackgroundColor(ChartColor.FromHexString(styling.TotalColor)))
            .Build();

        tmp = tmp with { DataSets = [dataset1, dataset2, dataset3] };  // HACK V10: This looks extreemely bad to combine this immutability style "with" constraint with all the rest of the logic to just mutate single instance (2024/08/22, Dmitry Kalabin)

        if (includeConnectors)
        {
            LineDataSetBuilder Builder(LineDataSetBuilder b, IEnumerable<double?> data)
            {
                var builder = b.WithData(data).WithLabel(ChartConst.Hidden).WithDataLabels(new DataLabels { Display = false }).SetType(ChartType.Line);
                return connectorDataSetModifier != null
                           ? connectorDataSetModifier(builder)
                           : builder;
            }

            tmp = tmp
                  .WithDataSet(Builder(new(), dataModel.FirstDottedValues).Build())
                  .WithDataSet(Builder(new(), dataModel.SecondDottedValues).Build())
                  .WithDataSet(Builder(new(), dataModel.ThirdDottedValues).Build());
        }

        var palette = new[] { styling.IncrementColor, styling.DecrementColor, styling.TotalColor, styling.TotalColor, styling.TotalColor, styling.TotalColor };
        tmp = tmp.WithLegend(lm => lm with
        {
            Labels = (lm.Labels ?? new LegendLabel()) with
            {
                Filter = $"item => item.text !== '{ChartConst.Hidden}'"
            }
        })
                 .WithDataLabels(o => o with
                 {
                     Color = $"context => context.dataset.data[context.dataIndex].{nameof(WaterfallBar.DataLabelColor).ToCamelCase()}",
                     Align = $"context => context.dataset.data[context.dataIndex].{nameof(WaterfallBar.DataLabelAlignment).ToCamelCase()}",
                     Anchor = $"context => context.dataset.data[context.dataIndex].{nameof(WaterfallBar.DataLabelAlignment).ToCamelCase()}",
                     Display = true,//$"context => context.dataset.data[context.dataIndex].{nameof(WaterfallBar.Range).ToCamelCase()} != null",
                     Formatter = $"(value, context) => context.dataset.data[context.dataIndex]?.{nameof(WaterfallBar.DataLabel).ToCamelCase()}"
                 })
                 .WithColorPalette(palette);

        tmp = tmp.WithOptions(o => o.WithPlugins(p => p with { Tooltip = (p.Tooltip ?? new ToolTip()) with { Enabled = false } }));

        return tmp.ToChart();
    }

    public TChart WithLegendItems(string incrementsLabel = null, string decrementsLabel = null, string totalLabel = null)
    {
        this.incrementsLabel = incrementsLabel;
        this.decrementsLabel = decrementsLabel;
        this.totalLabel = totalLabel;
        return (TChart)this;
    }

    public TChart WithStylingOptions(Func<WaterfallStylingBuilder, WaterfallStylingBuilder> func)
    {
        StylingBuilder = func(StylingBuilder);
        return (TChart)this;
    }

    /// <summary>
    /// Show lines that connect bars.
    /// </summary>
    /// <param name="connectorLineModifier">Override default dataset modifier (by default it's b => b.Dashed()</param>
    /// <returns>The builder</returns>
    public TChart WithConnectors(Func<LineDataSetBuilder, LineDataSetBuilder> connectorLineModifier = null)
    {
        if (connectorLineModifier != null)
            connectorDataSetModifier = connectorLineModifier;
        includeConnectors = true;
        return (TChart)this;
    }

    public TChart WithTotalsAtPositions(HashSet<int> totalIndexes)
    {
        this.totalIndexes.UnionWith(totalIndexes);
        return (TChart)this;
    }

    public TChart WithTotalsAtPositions(IEnumerable<int> totalIndexes)
        => WithTotalsAtPositions(totalIndexes.ToHashSet());

    public TChart WithTotalsAtPositions(params int[] totalIndexes)
        => WithTotalsAtPositions(totalIndexes.ToHashSet());

    public TChart WithDeltas(List<double> deltas)
    {
        this.deltas = deltas;
        return (TChart)this;
    }
    public TChart WithBarDataSetOptions(Func<TDataSetBuilder, TDataSetBuilder> barDataSetModifier)
    {
        this.barDataSetModifier = barDataSetModifier;
        return (TChart)this;
    }

    public TChart WithDeltas(IEnumerable<double> deltas)
        => WithDeltas(deltas.ToList());

    public TChart WithDeltas(params double[] deltas)
        => WithDeltas(deltas.AsEnumerable());

    /// <summary>
    /// Add one more value that will be a sum and mark it as total
    /// </summary>
    /// <returns>The builder</returns>
    public TChart WithLastAsTotal()
    {
        deltas = deltas.Append(deltas.Sum()).ToList();
        totalIndexes.Add(deltas.Count - 1);
        return (TChart)this;
    }
}

public record WaterfallChart
    : WaterfallChartBase<WaterfallChart, FloatingBarDataSet, FloatingBarDataSetBuilder>/*(ChartModel, (OptionsBuilder ?? new RangeOptionsBuilder()).Stacked("x")
                                                                                                                                                               .HideAxis("y")
                                                                                                                                                               .HideGrid("x"))*/
{
    public WaterfallChart(IReadOnlyCollection<FloatingBarDataSet> dataSets) : base(dataSets, ChartType.Bar)
    {
        Options = Options
            .Stacked("x")
            .HideAxis("y")
            .HideGrid("x");
    }

    //public WaterfallChart() : this(new Chart(ChartType.Bar)) { }
}

public record HorizontalWaterfallChart//(Chart ChartModel = null, RangeOptionsBuilder OptionsBuilder = null)
    : WaterfallChartBase<HorizontalWaterfallChart, HorizontalFloatingBarDataSet, HorizontalFloatingBarDataSetBuilder>/*(ChartModel, (OptionsBuilder ?? new RangeOptionsBuilder())
                                                                                                                                                .Stacked("y")
                                                                                                                                                //.HideAxis("x")
                                                                                                                                                .Grace<CartesianLinearScale>("x","10%")
                                                                                                                                                // TODO V10: understand why the line below helps and find less random approach (2023/10/08, Ekaterina Mishina)
                                                                                                                                                .SuggestedMax("x", 10) // this helps in case of all negative values
                                                                                                                                                .ShortenAxisNumbers("x")
                                                                                                                                                )*/
{
    public HorizontalWaterfallChart(IReadOnlyCollection<HorizontalFloatingBarDataSet> dataSets) : base(dataSets, ChartType.Bar)
    {
        Options = Options
            .Stacked("y")
            //.HideAxis("x")
            .Grace<CartesianLinearScale>("x", "10%")
            // TODO V10: understand why the line below helps and find less random approach (2023/10/08, Ekaterina Mishina)
            .SuggestedMax("x", 10) // this helps in case of all negative values
            .ShortenAxisNumbers("x")
            .WithIndexAxis("y")
        ;
    }

    //public HorizontalWaterfallChartBuilder()
    //    : this(new Chart(ChartType.Bar))
    //{
    //    OptionsBuilder = OptionsBuilder.WithIndexAxis("y");
    //}
}
